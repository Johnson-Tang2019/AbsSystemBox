// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SysBox.Abstractions;
namespace SysBox.Native;

public static class VolumeCapacityReader
{
    public static Task<VolumeCapacity[]> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        var result = new List<VolumeCapacity>();
        var budget = Stopwatch.StartNew();
        foreach (var drive in DriveInfo.GetDrives().Take(64))
        {
            token.ThrowIfCancellationRequested();
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;
            if (budget.Elapsed > TimeSpan.FromSeconds(2))
            {
                result.Add(new(drive.Name, "", null, null, "本轮容量查询预算已到"));
                continue;
            }
            try
            {
                result.Add(drive.IsReady ? new(drive.Name, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace, "已就绪") : new(drive.Name, "", null, null, "未就绪"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Add(new(drive.Name, "", null, null, "无法读取容量")); }
        }
        return result.ToArray();
    }, token);
}
