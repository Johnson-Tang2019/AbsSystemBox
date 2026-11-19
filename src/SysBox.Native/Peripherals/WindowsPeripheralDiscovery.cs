// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

internal sealed record PeripheralInterface(
    string Id,
    string GroupId,
    ushort? VendorId,
    ushort? ProductId,
    ushort? UsagePage,
    ushort? Usage,
    string? Manufacturer,
    string? ProductName,
    PeripheralConnection Connection);

public sealed partial class WindowsPeripheralDiscovery : IPeripheralDiscovery
{
    private const uint PresentDeviceInterfaces = 0x12;
    private const uint InterfaceActive = 0x01;
    private const uint InterfaceRemoved = 0x04;
    private const uint MaxInterfaces = 1024;
    private const int MaxPathBytes = 8192;
    private const int DevicePathOffset = sizeof(uint);
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const uint NoSuchDevNode = 0x0d;
    private const uint DeviceNotThere = 0x24;
    private const uint PropertyTypeGuid = 0x0d;
    private const uint PropertyTypeString = 0x12;
    private const uint PropertyTypeStringList = 0x2012;
    private const int HidStatusSuccess = 0x00110000;
    private static readonly Guid MachineContainerId = new("00000000-0000-0000-ffff-ffffffffffff");
    private static readonly Guid DevicePropertySet = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid UsbBusType = new("9d7debbc-c85d-11d1-9eb4-006008c3a19a");
    private static readonly DevicePropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly DevicePropertyKey BusReportedDescription = new(new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);
    private static readonly DevicePropertyKey HardwareIds = new(DevicePropertySet, 3);
    private static readonly DevicePropertyKey Manufacturer = new(DevicePropertySet, 13);
    private static readonly DevicePropertyKey FriendlyName = new(DevicePropertySet, 14);
    private static readonly DevicePropertyKey BusType = new(DevicePropertySet, 21);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(8);
    private int running;

    public async Task<Result<IReadOnlyList<PeripheralDevice>>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Failure("当前系统不支持 Windows 外设检测。", retryable: false);

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Failure("上一次设备检测仍在结束，请稍后刷新。");

        using var deadline = new CancellationTokenSource(DiscoveryTimeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = stop.Token;

        // Win32 metadata calls are synchronous. Keep one in-flight worker even when the caller stops waiting.
        var worker = Task.Run(() =>
        {
            try { return Discover(token); }
            finally { Volatile.Write(ref running, 0); }
        });
        try
        {
            return await worker.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return Failure("外设检测超时，请稍后刷新。");
        }
    }

    private static Result<IReadOnlyList<PeripheralDevice>> Discover(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var setupDataSize = IntPtr.Size == 8 ? 32 : 28;
        var detailHeaderSize = IntPtr.Size == 8 ? 8u : 6u;
        if (Marshal.SizeOf<DeviceInterfaceData>() != setupDataSize ||
            Marshal.SizeOf<DeviceInfoData>() != setupDataSize ||
            Marshal.SizeOf<DevicePropertyKey>() != 20 ||
            Marshal.SizeOf<HidAttributes>() != 12 ||
            Marshal.SizeOf<HidCaps>() != 64)
            return Failure("外设检测使用的 Windows 数据结构不受支持。", retryable: false);

        try
        {
            HidD_GetHidGuid(out var classGuid);
            using var devices = SetupDiGetClassDevs(in classGuid, IntPtr.Zero, IntPtr.Zero, PresentDeviceInterfaces);
            if (devices.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var interfaces = new List<PeripheralInterface>();
            var metadataBuffer = new byte[2048];
            var accessBlocked = false;
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var info = new DeviceInterfaceData { Size = (uint)setupDataSize };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, in classGuid, index, ref info))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        token.ThrowIfCancellationRequested();
                        var detected = PeripheralIdentity.Identify(interfaces);
                        if (detected.Count == 0 && accessBlocked)
                            return Result<IReadOnlyList<PeripheralDevice>>.Failure(new SysBoxError(
                                ErrorCodes.AccessDenied, "Windows 限制了外设信息的访问。", retryable: true));
                        return Result<IReadOnlyList<PeripheralDevice>>.Success(detected);
                    }
                    throw new Win32Exception(error);
                }

                if (index >= MaxInterfaces)
                    return Failure("设备接口数量超过检测上限。");
                if ((info.Flags & (InterfaceActive | InterfaceRemoved)) != InterfaceActive)
                    continue;

                var device = new DeviceInfoData { Size = (uint)setupDataSize };
                var path = GetDevicePath(devices, ref info, ref device, detailHeaderSize, token);
                if (path is not null && ReadInterface(path, device.Instance, metadataBuffer, token, ref accessBlocked) is { } entry)
                    interfaces.Add(entry);
            }
        }
        catch (Win32Exception error)
        {
            token.ThrowIfCancellationRequested();
            return Failure("无法读取 Windows 外设列表，请重试。",
                error.NativeErrorCode.ToString("X8", CultureInfo.InvariantCulture));
        }
        catch (InvalidDataException)
        {
            token.ThrowIfCancellationRequested();
            return Failure("Windows 返回了无效的设备接口信息，请重试。");
        }
    }

    private static string? GetDevicePath(DeviceInfoSetHandle devices, ref DeviceInterfaceData info,
        ref DeviceInfoData device, uint detailHeaderSize, CancellationToken token)
    {
        if (SetupDiGetDeviceInterfaceDetail(devices, ref info, null, 0, out var required, ref device))
            throw new InvalidDataException();
        var error = Marshal.GetLastPInvokeError();
        if (DeviceDisappeared(error))
            return null;
        if (error != ErrorInsufficientBuffer)
            throw new Win32Exception(error);
        if (required < detailHeaderSize || required > MaxPathBytes || (required & 1) != 0)
            throw new InvalidDataException();

        var bytes = new byte[checked((int)required)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, detailHeaderSize);
        token.ThrowIfCancellationRequested();
        if (!SetupDiGetDeviceInterfaceDetail(devices, ref info, bytes, required, out var returned, ref device))
        {
            error = Marshal.GetLastPInvokeError();
            if (DeviceDisappeared(error))
                return null;
            throw new Win32Exception(error);
        }

        // The UTF-16 path starts immediately after DWORD cbSize, including on x64.
        if (returned < DevicePathOffset + sizeof(char) || returned > required ||
            ReadText(bytes.AsSpan(DevicePathOffset, (int)returned - DevicePathOffset), trim: false) is not { } path ||
            !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new InvalidDataException();
        return path;
    }

    private static PeripheralInterface? ReadInterface(string path, uint instance, byte[] buffer, CancellationToken token,
        ref bool accessBlocked)
    {
        token.ThrowIfCancellationRequested();
        // Desired access zero is sufficient for HID metadata and cannot read input reports or write reports.
        using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var openError = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (DeviceDisappeared(openError))
            return null;

        var instanceId = GetInstanceId(instance, buffer);
        var container = ReadGuidProperty(instance, in ContainerId, buffer);
        var group = container is Guid id && id != Guid.Empty && id != MachineContainerId
            ? $"container:{id:D}"
            : instanceId is not null ? "devinst:" + instanceId : "interface:" + path;

        token.ThrowIfCancellationRequested();
        var attributes = new HidAttributes { Size = 12u };
        ushort? vendor = null;
        ushort? product = null;
        if (!handle.IsInvalid && HidD_GetAttributes(handle, ref attributes) && attributes.VendorId != 0)
        {
            vendor = attributes.VendorId;
            product = attributes.ProductId;
        }
        if (vendor is null && DeviceIds().Match(instanceId ?? path) is { Success: true } match)
        {
            vendor = ushort.Parse(match.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            product = ushort.Parse(match.Groups[2].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        token.ThrowIfCancellationRequested();
        var caps = !handle.IsInvalid ? ReadCapabilities(handle) : null;
        var usage = caps is HidCaps values
            ? (Page: (ushort?)values.UsagePage, Usage: (ushort?)values.Usage)
            : ReadUsageProperty(instance, buffer);

        token.ThrowIfCancellationRequested();
        var productName = !handle.IsInvalid && HidD_GetProductString(handle, buffer, (uint)buffer.Length)
            ? ReadText(buffer)
            : null;
        productName ??= ReadStringProperty(instance, in BusReportedDescription, buffer) ??
            ReadStringProperty(instance, in FriendlyName, buffer);
        token.ThrowIfCancellationRequested();
        var manufacturer = !handle.IsInvalid && HidD_GetManufacturerString(handle, buffer, (uint)buffer.Length)
            ? ReadText(buffer)
            : null;
        manufacturer ??= ReadStringProperty(instance, in Manufacturer, buffer);

        var connection = ReadConnection(instance, instanceId, buffer, token);
        token.ThrowIfCancellationRequested();
        if (CM_Get_DevNode_Status(out _, out _, instance, 0) is NoSuchDevNode or DeviceNotThere)
            return null;
        accessBlocked |= openError == ErrorAccessDenied && usage.Page is null;
        return new(path, group, vendor, product, usage.Page, usage.Usage, manufacturer, productName, connection);
    }

    private static bool DeviceDisappeared(int error) => error is 2 or 3 or 433 or 1167 or
        unchecked((int)0xe000020b) or unchecked((int)0xe0000225);

    private static HidCaps? ReadCapabilities(SafeFileHandle handle)
    {
        var success = HidD_GetPreparsedData(handle, out var data);
        using (data)
            return success && !data.IsInvalid && HidP_GetCaps(data, out var caps) == HidStatusSuccess ? caps : null;
    }

    private static (ushort? Page, ushort? Usage) ReadUsageProperty(uint instance, byte[] buffer)
    {
        var length = (uint)buffer.Length;
        if (CM_Get_DevNode_Property(instance, in HardwareIds, out var type, buffer, ref length, 0) != 0 ||
            type != PropertyTypeStringList || length > buffer.Length || (length & 1) != 0)
            return default;

        var remaining = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)length));
        const string prefix = "HID_DEVICE_UP:";
        while (remaining.IndexOf('\0') is var end && end > 0)
        {
            var id = remaining[..end];
            if (id.Length == prefix.Length + 11 && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                id.Slice(prefix.Length + 4, 3).Equals("_U:", StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(id.Slice(prefix.Length, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var page) &&
                ushort.TryParse(id[^4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var usage))
                return (page, usage);
            remaining = remaining[(end + 1)..];
        }

        return default;
    }

    private static PeripheralConnection ReadConnection(uint node, string? nodeId, byte[] buffer, CancellationToken token)
    {
        var usbReported = false;
        for (var depth = 0; depth < 8; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (nodeId is not null)
            {
                if (nodeId.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) ||
                    nodeId.StartsWith(@"BTHLEDEVICE\", StringComparison.OrdinalIgnoreCase) ||
                    nodeId.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase))
                    return PeripheralConnection.Bluetooth;
                if (nodeId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase))
                    return PeripheralConnection.Usb;
            }

            usbReported |= ReadGuidProperty(node, in BusType, buffer) == UsbBusType;
            if (CM_Get_Parent(out var parent, node, 0) != 0 || parent == node)
                break;
            node = parent;
            nodeId = GetInstanceId(node, buffer);
        }
        return usbReported ? PeripheralConnection.Usb : PeripheralConnection.Unknown;
    }

    private static string? GetInstanceId(uint node, byte[] buffer) =>
        CM_Get_Device_ID(node, buffer, (uint)(buffer.Length / sizeof(char)), 0) == 0
            ? ReadText(buffer, trim: false) : null;

    private static Guid? ReadGuidProperty(uint node, in DevicePropertyKey key, byte[] buffer)
    {
        var length = 16u;
        return CM_Get_DevNode_Property(node, in key, out var type, buffer, ref length, 0) == 0 &&
            type == PropertyTypeGuid && length == 16 ? new Guid(buffer.AsSpan(0, 16)) : null;
    }

    private static string? ReadStringProperty(uint node, in DevicePropertyKey key, byte[] buffer)
    {
        var length = (uint)buffer.Length;
        return CM_Get_DevNode_Property(node, in key, out var type, buffer, ref length, 0) == 0 &&
            type == PropertyTypeString && length <= buffer.Length ? ReadText(buffer.AsSpan(0, (int)length)) : null;
    }

    private static string? ReadText(ReadOnlySpan<byte> buffer, bool trim = true)
    {
        if ((buffer.Length & 1) != 0)
            return null;
        var chars = MemoryMarshal.Cast<byte, char>(buffer);
        var end = chars.IndexOf('\0');
        if (end <= 0)
            return null;
        chars = trim ? chars[..end].Trim() : chars[..end];
        foreach (var character in chars)
            if (char.IsControl(character))
                return null;
        return chars.IsEmpty ? null : new string(chars);
    }

    private static Result<IReadOnlyList<PeripheralDevice>> Failure(string message, string? details = null, bool retryable = true) =>
        Result<IReadOnlyList<PeripheralDevice>>.Failure(new SysBoxError(ErrorCodes.Unavailable, message, details, retryable));

    [GeneratedRegex(@"(?:^|[\\#&])VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?=[\\#&]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIds();

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid Class;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid Class;
        public uint Instance;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevicePropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    // HIDP_CAPS contains 32 USHORTs. We consume only its first two fields.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
    }

    private sealed class DeviceInfoSetHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    private sealed class HidPreparsedDataHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => HidD_FreePreparsedData(handle);
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", ExactSpelling = true, SetLastError = true)]
    private static extern DeviceInfoSetHandle SetupDiGetClassDevs(in Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoSetHandle devices, IntPtr device, in Guid classGuid,
        uint index, ref DeviceInterfaceData info);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(DeviceInfoSetHandle devices, ref DeviceInterfaceData info,
        [In, Out] byte[]? detail, uint size, out uint required, ref DeviceInfoData device);

    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Property(uint instance, in DevicePropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID(uint instance, [Out] byte[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint instance, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint instance, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("hid.dll", ExactSpelling = true)]
    private static extern void HidD_GetHidGuid(out Guid classGuid);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, [Out] byte[] buffer, uint length);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle device, [Out] byte[] buffer, uint length);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out HidPreparsedDataHandle data);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll", ExactSpelling = true)]
    private static extern int HidP_GetCaps(HidPreparsedDataHandle data, out HidCaps caps);
}
