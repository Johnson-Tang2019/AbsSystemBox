// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

[SupportedOSPlatform("windows")]
public sealed class GoldenFiledDisplayTransport : ICoolingDisplayTransport
{
    public Task<CoolingDevice[]> DiscoverAsync(CancellationToken token) => Task.Run(() => Discover(token), token);

    private static CoolingDevice[] Discover(CancellationToken token)
    {
        using var serial = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        var livePorts = serial?.GetValueNames().Select(name => serial.GetValue(name) as string).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1A86&PID_484A&MI_00");
        var devices = new List<CoolingDevice>();
        foreach (var instance in usb?.GetSubKeyNames().Take(32) ?? [])
        {
            token.ThrowIfCancellationRequested();
            using var key = usb!.OpenSubKey(instance);
            using var parameters = key?.OpenSubKey("Device Parameters");
            if (parameters?.GetValue("PortName") is string port && Regex.IsMatch(port, @"\ACOM[1-9][0-9]{0,3}\z") && livePorts.Contains(port))
                devices.Add(new(port, @"USB\VID_1A86&PID_484A&MI_00\" + instance));
        }
        return devices.ToArray();
    }

    public async Task<ICoolingDisplayConnection> ConnectAsync(CoolingDevice device, CancellationToken token)
    {
        return await Task.Run<ICoolingDisplayConnection>(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!Discover(token).Contains(device))
                throw new IOException("所选水冷屏已断开或设备身份已改变，请重新检测。");
            if (Marshal.SizeOf<Dcb>() != 28 || Marshal.SizeOf<CommTimeouts>() != 20)
                throw new InvalidOperationException("串口结构布局不匹配。");
            var handle = CreateFile(@"\\.\" + device.PortName, 0x40000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException("无法连接水冷屏，请退出占用串口的 GoldenFiled 后重试。", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            try
            {
                var dcb = new Dcb { Length = 28 };
                if (!GetCommState(handle, ref dcb)) throw new Win32Exception(Marshal.GetLastWin32Error());
                dcb.BaudRate = 1_000_000;
                dcb.Flags = 1;
                dcb.ByteSize = 8;
                dcb.Parity = 0;
                dcb.StopBits = 0;
                var timeouts = new CommTimeouts { WriteConstant = 500, ReadInterval = uint.MaxValue };
                if (!SetCommState(handle, ref dcb) || !SetCommTimeouts(handle, ref timeouts))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                token.ThrowIfCancellationRequested();
                return new Connection(handle);
            }
            catch { handle.Dispose(); throw; }
        }, token);
    }

    private sealed class Connection(SafeFileHandle handle) : ICoolingDisplayConnection
    {
        private readonly object gate = new();
        private bool disposed;
        public Task WriteAsync(int temperature, int fanRpm, CancellationToken token)
        {
            var frame = GoldenFiledDisplayProtocol.Encode(temperature, fanRpm);
            return Task.Run(() =>
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    token.ThrowIfCancellationRequested();
                    if (!WriteFile(handle, frame, (uint)frame.Length, out var written, IntPtr.Zero) || written != frame.Length)
                        throw new IOException("水冷屏写入失败或设备已断开。", new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }, token);
        }
        public void Dispose()
        {
            lock (gate) { disposed = true; handle.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length, BaudRate, Flags;
        public ushort Reserved, XonLimit, XoffLimit;
        public byte ByteSize, Parity, StopBits;
        public sbyte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
        public ushort Reserved1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadInterval, ReadMultiplier, ReadConstant, WriteMultiplier, WriteConstant;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle handle, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle handle, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(SafeFileHandle handle, ref CommTimeouts timeouts);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint count, out uint written, IntPtr overlapped);
}
