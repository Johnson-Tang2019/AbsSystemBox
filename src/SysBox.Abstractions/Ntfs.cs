// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public sealed record UsnRecord(string FileId, string ParentId, long Usn, uint Reason, uint Attributes, string Name, ushort Version);
public sealed record UsnJournal(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);
public sealed record NtfsBatch(ulong Continuation, IReadOnlyList<UsnRecord> Records, bool EndOfEnumeration);
public interface INtfsVolumeReader : IDisposable
{
    UsnJournal QueryJournal();
    NtfsBatch Enumerate(ulong continuation, long highUsn, CancellationToken cancellationToken);
    NtfsBatch ReadChanges(long startUsn, ulong journalId, CancellationToken cancellationToken);
}

public sealed record NtfsObject(string FileId, uint Attributes, long? LogicalBytes, long? AllocatedBytes, uint LinkCount,
    DateTimeOffset? LastWriteUtc, string[] Links, string State, string? Error = null);
public sealed record NtfsVolumeBinding(string VolumeId, string RootPath, string RootFileId);
public interface INtfsMetadataReader : INtfsVolumeReader
{
    NtfsObject[] ReadObjects(string[] fileIds, CancellationToken cancellationToken);
}
public static class NtfsIdentity
{
    public static string Volume(string value)
    {
        if (value.Length != 49 || !value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || !value.EndsWith("}\\", StringComparison.Ordinal) || !Guid.TryParseExact(value.Substring(11, 36), "D", out var guid))
            throw new ArgumentException("需要规范的本地卷 GUID。");
        return @"\\?\Volume{" + guid.ToString("D") + @"}\";
    }
    public static string FileId(string value)
    {
        if (value.Length is not (16 or 32) || !value.All(char.IsAsciiHexDigit))
            throw new ArgumentException("文件身份无效。");
        value = value.ToUpperInvariant();
        return value.Length == 32 && value[16..].All(c => c == '0') ? value[..16] : value;
    }
    public static string Relative(string value)
    {
        if (value.Length is < 1 or > 32760 || value[0] != '\\' || value.StartsWith(@"\\", StringComparison.Ordinal) || value.IndexOfAny(['\0', '/', ':']) >= 0 || value.Split('\\').Any(c => c is "." or ".."))
            throw new ArgumentException("卷内路径无效。");
        if (value != @"\" && value[1..].Contains(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("卷内路径不能包含空名称。");
        return value == @"\" ? value : value.TrimEnd('\\');
    }
}
