// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Text.Json;
using SysBox.Native;

// Read-only metadata probe only. Enumeration is deliberately not a default operation.
if (args.Length != 2 || args[0] != "probe")
{
    Console.Error.WriteLine("用法：SysBox.NtfsLab probe X:（只查询 Journal，不枚举文件、不创建 Journal）");
    return 2;
}
try
{
    using var reader = new WindowsNtfsVolumeReader(args[1]);
    var journal = reader.QueryJournal();
    Console.WriteLine(JsonSerializer.Serialize(new { available = true, journal.JournalId, journal.FirstUsn, journal.NextUsn, journal.LowestValidUsn, enumeratedRecords = 0 }));
    return 0;
}
catch (Win32Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { available = false, nativeError = ex.NativeErrorCode,
        message = "没有卷级读取权限、Journal 不可用或操作失败；普通界面继续使用目录扫描。", enumeratedRecords = 0 }));
    return 3;
}
catch (Exception)
{
    Console.Error.WriteLine("卷能力不可用；没有改动卷或文件。"); return 4;
}
