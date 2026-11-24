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

internal sealed class ScriptChildProcess : IDisposable
{
    private const uint DetachedWithAttributes = 0x00080008;
    private const uint ExtendedStartupInfo = 0x00080000;
    private readonly SafeProcessHandle handle;
    private readonly HiddenConsole? console;

    private ScriptChildProcess(int id, SafeProcessHandle handle, StreamReader output, StreamReader error, HiddenConsole? console)
    {
        Id = id;
        this.handle = handle;
        Output = output;
        Error = error;
        this.console = console;
    }

    public int Id { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }

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
        console?.Dispose();
        handle.Dispose();
        Output.Dispose();
        Error.Dispose();
    }

    public static ScriptChildProcess Start(ProcessStartInfo info, ScriptProcessJob job, bool requiresConsole)
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
        HiddenConsole? console = null;
        try
        {
            using var stdin = new PipePair(descriptor.AddrOfPinnedObject());
            using var stdout = new PipePair(descriptor.AddrOfPinnedObject());
            using var stderr = new PipePair(descriptor.AddrOfPinnedObject());
            if (requiresConsole)
                console = HiddenConsole.Create(descriptor.AddrOfPinnedObject());
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
            using var attributes = new ProcessAttributes(inherited, job.Handle.DangerousGetHandle(),
                console?.Handle.DangerousGetHandle() ?? IntPtr.Zero);
            startup.Attributes = attributes.Address;
            var flags = console is null ? DetachedWithAttributes : ExtendedStartupInfo;
            var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            CreatedProcess created;
            bool started;
            try
            {
                startup.Startup.Desktop = desktop;
                var arguments = CommandLine(info);
                started = restricted is null
                    ? CreateProcessW(info.FileName, arguments, IntPtr.Zero, IntPtr.Zero,
                        true, flags, IntPtr.Zero, info.WorkingDirectory, ref startup, out created)
                    : CreateProcessAsUserW(restricted, info.FileName, arguments, IntPtr.Zero, IntPtr.Zero,
                        true, flags, IntPtr.Zero, info.WorkingDirectory, ref startup, out created);
                if (!started)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能启动脚本解释器。");
            }
            finally { Marshal.FreeHGlobal(desktop); }
            var process = new SafeProcessHandle(created.Process, true);
            using var thread = new SafeWaitHandle(created.Thread, true);
            StreamReader? stdoutReader = null;
            try
            {
                // Windows PowerShell writes redirected text using the new console's OEM code page.
                var encoding = requiresConsole
                    ? CodePagesEncodingProvider.Instance.GetEncoding((int)GetOEMCP()) ?? Encoding.UTF8
                    : Encoding.UTF8;
                stdoutReader = new StreamReader(stdout.TakeReadStream(), encoding);
                var result = new ScriptChildProcess(checked((int)created.ProcessId), process, stdoutReader,
                    new StreamReader(stderr.TakeReadStream(), encoding), console);
                console = null;
                return result;
            }
            catch
            {
                process.Dispose();
                stdoutReader?.Dispose();
                throw;
            }
        }
        finally
        {
            descriptor.Free();
            console?.Dispose();
        }
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
        private bool readTransferred, writeTransferred;

        public FileStream TakeReadStream()
        {
            var stream = new FileStream(Read, FileAccess.Read, bufferSize: 4096, isAsync: false);
            readTransferred = true;
            return stream;
        }

        public SafeFileHandle TakeWriteHandle()
        {
            writeTransferred = true;
            return Write;
        }

        public void Dispose()
        {
            if (!readTransferred)
                Read.Dispose();
            if (!writeTransferred)
                Write.Dispose();
        }
    }

    private sealed class HiddenConsole : IDisposable
    {
        private readonly SafeFileHandle input;
        private readonly FileStream output;
        private readonly Task drain;
        public SafePseudoConsoleHandle Handle { get; }

        private HiddenConsole(SafePseudoConsoleHandle handle, SafeFileHandle input, FileStream output)
        {
            Handle = handle;
            this.input = input;
            this.output = output;
            // Console control output must be drained separately from the actual script's stdout and stderr.
            drain = DrainAsync(output);
        }

        public static HiddenConsole Create(IntPtr descriptor)
        {
            using var input = new PipePair(descriptor);
            using var output = new PipePair(descriptor);
            var status = CreatePseudoConsole(new ConsoleSize { Columns = 80, Rows = 24 }, input.Read, output.Write, 0, out var value);
            if (status < 0)
                throw new Win32Exception($"无法为 Windows PowerShell 创建隐藏终端（0x{status:X8}）。");
            var handle = new SafePseudoConsoleHandle(value);
            try
            {
                var reader = output.TakeReadStream();
                return new(handle, input.TakeWriteHandle(), reader);
            }
            catch { handle.Dispose(); throw; }
        }

        private static async Task DrainAsync(FileStream output)
        {
            try { await output.CopyToAsync(Stream.Null).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            // Closing input first sends a console shutdown to the interpreter.
            Handle.Dispose();
            input.Dispose();
            output.Dispose();
            _ = drain;
        }
    }

    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsoleHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            ClosePseudoConsole(handle);
            return true;
        }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        private readonly GCHandle handles;
        private readonly GCHandle jobs;
        private bool initialized;
        public IntPtr Address { get; private set; }

        public ProcessAttributes(IntPtr[] inherited, IntPtr job, IntPtr console)
        {
            var count = console == IntPtr.Zero ? 2 : 3;
            nuint length = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref length);
            if (length is 0 or > 4096)
                throw new Win32Exception("无法分配脚本进程启动属性。");
            Address = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                handles = GCHandle.Alloc(inherited, GCHandleType.Pinned);
                jobs = GCHandle.Alloc(new[] { job }, GCHandleType.Pinned);
                if (!InitializeProcThreadAttributeList(Address, count, 0, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                if (!UpdateProcThreadAttribute(Address, 0, 0x00020002, handles.AddrOfPinnedObject(),
                    (nuint)(inherited.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero) ||
                    !UpdateProcThreadAttribute(Address, 0, 0x0002000D, jobs.AddrOfPinnedObject(),
                    (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法隔离脚本的继承句柄或进程组。");
                if (console != IntPtr.Zero &&
                    !UpdateProcThreadAttribute(Address, 0, 0x00020016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为 Windows PowerShell 接入隐藏终端。");
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
    private struct ConsoleSize { public short Columns, Rows; }

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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CreatePseudoConsole(ConsoleSize size, SafeFileHandle input, SafeFileHandle output,
        uint flags, out IntPtr console);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetOEMCP();
}
