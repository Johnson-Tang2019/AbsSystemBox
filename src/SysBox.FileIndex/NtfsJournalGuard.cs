// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.FileIndex;

public static class NtfsJournalGuard
{
    public static void Validate(UsnJournal journal, ulong expectedJournalId, long checkpoint)
    {
        if (journal.JournalId != expectedJournalId || journal.FirstUsn < 0 || journal.NextUsn < journal.FirstUsn ||
            journal.LowestValidUsn < 0 || checkpoint < Math.Max(journal.FirstUsn, journal.LowestValidUsn) || checkpoint > journal.NextUsn)
            throw new SysBoxOperationException(new("REBUILD_REQUIRED", "USN Journal 已变化或日志存在缺口，需要重新建立索引。"));
    }
}
