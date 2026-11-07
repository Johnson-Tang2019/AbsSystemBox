// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

internal sealed class HardwareSensorSession : IDisposable
{
    private readonly Computer computer;
    private readonly bool privileged;
    private HardwareSensorSession(Computer computer, bool privileged) { this.computer = computer; this.privileged = privileged; }

    public static HardwareSensorSession Create(bool privileged)
    {
        if (privileged)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("CPU 和主板采集缺少操作系统权限。");
            using var driver = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (driver.IsInvalid) throw new IOException("PawnIO 驱动不可访问，CPU 和主板采集不可用。");
        }
        var computer = new Computer
        {
            IsCpuEnabled = privileged, IsMotherboardEnabled = privileged, IsGpuEnabled = true,
            IsControllerEnabled = false, IsMemoryEnabled = false, IsStorageEnabled = false,
            IsNetworkEnabled = false, IsPsuEnabled = false, IsBatteryEnabled = false, IsPowerMonitorEnabled = false
        };
        try { computer.Open(); return new(computer, privileged); }
        catch { computer.Close(); throw; }
    }

    public CoolingSensorSnapshot Read(CancellationToken token = default)
    {
        var result = new List<CoolingSensor>();
        var pending = new Queue<IHardware>(computer.Hardware.Take(32));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0 && visited.Count < 64 && result.Count < 256)
        {
            token.ThrowIfCancellationRequested();
            var hardware = pending.Dequeue();
            if (!visited.Add(hardware.Identifier.ToString())) continue;
            hardware.Update();
            foreach (var sensor in hardware.Sensors.Take(512))
            {
                if (result.Count >= 256) break;
                if (sensor.SensorType is not (SensorType.Temperature or SensorType.Fan) || sensor.Value is not { } value || !float.IsFinite(value)) continue;
                var kind = sensor.SensorType == SensorType.Temperature ? CoolingSensorKind.Temperature : CoolingSensorKind.Fan;
                if (value < 0 || value > (kind == CoolingSensorKind.Temperature ? 255 : 65535)) continue;
                var id = sensor.Identifier.ToString();
                if (id.Length > 256 || result.Any(item => item.Id == id)) continue;
                var name = hardware.Name + " · " + sensor.Name;
                result.Add(new(id, name.Length <= 256 ? name : name[..256], kind, value));
            }
            foreach (var child in hardware.SubHardware.Take(32))
                if (pending.Count < 64) pending.Enqueue(child);
        }
        return new(DateTimeOffset.UtcNow, result.ToArray(), privileged
            ? "独立采集 CPU、主板与显卡的实际温度和转速。"
            : "独立采集显卡温度与转速；CPU 和主板传感器需要授权采集服务。");
    }

    public void Dispose() => computer.Close();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
