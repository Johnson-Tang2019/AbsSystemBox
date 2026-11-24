// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysBox.CoreHost;

internal sealed class ScriptChildProcess(int id, SafeProcessHandle handle, StreamReader output, StreamReader error) : IDisposable
{
    private const uint DetachedWithAttributes = 0x00080008;
    public int Id { get; } = id;
    public StreamReader Output { get; } = output;
    public StreamReader Error { get; } = error;

    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(handle, out var code))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        }
    }

    public async Task WaitAsync(CancellationToken token = default)
    {
        using var signal = new ProcessSignal(handle);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, static (state, _) =>
            ((TaskCompletionSource)state!).TrySetResult(), finished, Timeout.Infinite, executeOnlyOnce: true);
        try { await finished.Task.WaitAsync(token); }
        finally { registration.Unregister(null); }
    }

    public void Dispose()
    {
        handle.Dispose();
        Output.Dispose();
        Error.Dispose();
    }

    public static ScriptChildProcess Start(ProcessStartInfo info, ScriptProcessJob job)
    {
        if (Marshal.SizeOf<StartupWithAttributes>() != 112 || Marshal.SizeOf<SecurityAttributes>() != 24 ||
            Marshal.SizeOf<CreatedProcess>() != 24)
            throw new PlatformNotSupportedException("脚本托管要求 Windows x64。");

        using var identity = WindowsIdentity.GetCurrent();
        using var restricted = ScriptUserToken.RestrictIfElevated(identity);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(identity.User!);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var descriptor = GCHandle.Alloc(security.GetSecurityDescriptorBinaryForm(), GCHandleType.Pinned);
        try
        {
            using var stdin = new PipePair(descriptor.AddrOfPinnedObject());
            using var stdout = new PipePair(descriptor.AddrOfPinnedObject());
            using var stderr = new PipePair(descriptor.AddrOfPinnedObject());
            var startup = new StartupWithAttributes
            {
                Startup = new Startup
                {
                    Size = 112,
                    Flags = 0x00000101, // STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW
                    Show = 0,
                    Input = stdin.Read.DangerousGetHandle(),
                    Output = stdout.Write.DangerousGetHandle(),
                    Error = stderr.Write.DangerousGetHandle()
                }
            };
            var inherited = new[] { startup.Startup.Input, startup.Startup.Output, startup.Startup.Error };
            using var attributes = new ProcessAttributes(inherited, job.Handle.DangerousGetHandle());
            startup.Attributes = attributes.Address;
            var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            CreatedProcess created;
            bool started;
            try
            {
                startup.Startup.Desktop = desktop;
                var arguments = CommandLine(info);
                started = restricted is null
                    ? CreateProcessW(info.FileName, arguments, IntPtr.Zero, IntPtr.Zero,
                        true, DetachedWithAttributes, IntPtr.Zero, info.WorkingDirectory, ref startup, out created)
                    : CreateProcessAsUserW(restricted, info.FileName, arguments, IntPtr.Zero, IntPtr.Zero,
                        true, DetachedWithAttributes, IntPtr.Zero, info.WorkingDirectory, ref startup, out created);
                if (!started)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能启动 Python 解释器。");
            }
            finally { Marshal.FreeHGlobal(desktop); }
            var process = new SafeProcessHandle(created.Process, true);
            using var thread = new SafeWaitHandle(created.Thread, true);
            var stdoutReader = stdout.TakeReader();
            try
            {
                return new(checked((int)created.ProcessId), process, stdoutReader, stderr.TakeReader());
            }
            catch
            {
                process.Dispose();
                stdoutReader.Dispose();
                throw;
            }
        }
        finally { descriptor.Free(); }
    }

    private static StringBuilder CommandLine(ProcessStartInfo info)
    {
        var command = new StringBuilder();
        foreach (var argument in info.ArgumentList.Prepend(info.FileName))
        {
            if (command.Length > 0)
                command.Append(' ');
            if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
            {
                command.Append(argument);
                continue;
            }
            command.Append('"');
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                command.Append(character);
                slashes = 0;
            }
            command.Append('\\', slashes * 2).Append('"');
        }
        return command;
    }

    private sealed class ProcessSignal : WaitHandle
    {
        public ProcessSignal(SafeProcessHandle handle) => SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false);
    }

    private sealed class PipePair : IDisposable
    {
        public PipePair(IntPtr descriptor)
        {
            var attributes = new SecurityAttributes { Length = 24, Descriptor = descriptor, Inherit = 1 };
            if (!CreatePipe(out var read, out var write, ref attributes, 0))
            {
                var code = Marshal.GetLastWin32Error();
                read?.Dispose();
                write?.Dispose();
                throw new Win32Exception(code, "无法打开脚本的标准输入或输出通道。");
            }
            Read = read;
            Write = write;
        }

        public SafeFileHandle Read { get; }
        public SafeFileHandle Write { get; }
        private bool readerOwned;

        public StreamReader TakeReader()
        {
            var reader = new StreamReader(new FileStream(Read, FileAccess.Read, bufferSize: 4096, isAsync: false));
            readerOwned = true;
            return reader;
        }

        public void Dispose()
        {
            if (!readerOwned)
                Read.Dispose();
            Write.Dispose();
        }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        private readonly GCHandle handles;
        private readonly GCHandle jobs;
        private bool initialized;
        public IntPtr Address { get; private set; }

        public ProcessAttributes(IntPtr[] inherited, IntPtr job)
        {
            nuint length = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref length);
            if (length is 0 or > 4096)
                throw new Win32Exception("无法分配脚本进程启动属性。");
            Address = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                handles = GCHandle.Alloc(inherited, GCHandleType.Pinned);
                jobs = GCHandle.Alloc(new[] { job }, GCHandleType.Pinned);
                if (!InitializeProcThreadAttributeList(Address, 2, 0, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                if (!UpdateProcThreadAttribute(Address, 0, 0x00020002, handles.AddrOfPinnedObject(),
                    (nuint)(inherited.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero) ||
                    !UpdateProcThreadAttribute(Address, 0, 0x0002000D, jobs.AddrOfPinnedObject(),
                    (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法隔离脚本的继承句柄或进程组。");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Address == IntPtr.Zero)
                return;
            if (initialized)
                DeleteProcThreadAttributeList(Address);
            if (handles.IsAllocated)
                handles.Free();
            if (jobs.IsAllocated)
                jobs.Free();
            Marshal.FreeHGlobal(Address);
            Address = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Startup
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedData, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupWithAttributes { public Startup Startup; public IntPtr Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreatedProcess { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupWithAttributes startup, out CreatedProcess process);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags,
        IntPtr environment, string directory, ref StartupWithAttributes startup, out CreatedProcess process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
