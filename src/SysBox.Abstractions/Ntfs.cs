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
