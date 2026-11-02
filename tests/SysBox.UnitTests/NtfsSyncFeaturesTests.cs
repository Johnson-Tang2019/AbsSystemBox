// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.FileIndex;
namespace SysBox.UnitTests;

public sealed class NtfsSyncFeaturesTests
{
    private static string Id(int value) => value.ToString("X16");
    private static UsnRecord Record(int file, int parent, long usn, uint reason, string name, uint attributes = 0) => new(Id(file), Id(parent), usn, reason, attributes, name, 2);
    private sealed class Volume : INtfsVolumeReader
    {
        public ulong Journal = 7; public long Next = 2; public Action? DuringEnumeration;
        public int FailReadAt, Reads;
        public List<UsnRecord> Baseline { get; } = [Record(1, 1, 1, 0, ".", 0x10), Record(2, 1, 1, 0, "a", 0x10), Record(3, 1, 1, 0, "b", 0x10)];
        public List<UsnRecord> Changes { get; } = [];
        public UsnJournal QueryJournal() => new(Journal, 0, Next, 0);
        public NtfsBatch Enumerate(ulong continuation, long highUsn, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (continuation > 0)
                return new(continuation, [], true);
            DuringEnumeration?.Invoke();
            return new(1000, Baseline.ToArray(), false);
        }
        public NtfsBatch ReadChanges(long startUsn, ulong journalId, CancellationToken token)
        {
            if (++Reads == FailReadAt)
                throw new IOException("Injected interruption after a committed batch.");
            token.ThrowIfCancellationRequested();
            var records = Changes.Where(r => r.Usn >= startUsn).Take(128).ToArray();
            return new((ulong)(records.Length == 0 ? Next : records[^1].Usn + 1), records, false);
        }
        public void Dispose()
        {
        }
    }
    [Fact]
    public async Task EnumerationChangesAreReplayedBeforeGenerationIsPublishedAndRestartReplays1000Events()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-NtfsSync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new IndexDatabase(root);
            var engine = new NtfsSyncEngine(database);
            using var volume = new Volume();
            volume.DuringEnumeration = () => { volume.Changes.Add(Record(9, 2, 2, 0x100, "created-during-enumeration")); volume.Next = 3; };
            var first = await engine.BuildAsync("fixture", volume, default);
            Assert.Equal(3, first.Checkpoint);
            Assert.Equal("SynchronizedPartial", first.State);
            Assert.Contains(first.Entries, e => e.Name == "created-during-enumeration");
            long usn = 3;
            for (var i = 0; i < 250; i++)
            {
                var file = 100 + i;
                volume.Changes.Add(Record(file, 2, usn++, 0x100, "new" + i));
                volume.Changes.Add(Record(file, 2, usn++, 0x1000, "new" + i));
                volume.Changes.Add(Record(file, 3, usn++, 0x2000, "renamed" + i));
                volume.Changes.Add(Record(file, 3, usn++, i % 2 == 0 ? 0x200u : 0x2u, "renamed" + i));
            }
            volume.Next = usn;
            var restarted = new NtfsSyncEngine(new IndexDatabase(root));
            await restarted.CatchUpAsync("fixture", volume, default);
            var state = restarted.ReadSnapshot("fixture", 1000);
            Assert.Equal(1003, state.Checkpoint);
            Assert.Equal(first.Generation, state.Generation);
            Assert.Equal(129, state.Entries.Count);
            Assert.False(state.Truncated);
            for (var i = 0; i < 250; i++)
            {
                if (i % 2 == 0)
                    Assert.DoesNotContain(state.Entries, e => e.FileId == Id(100 + i));
                else
                {
                    var item = Assert.Single(state.Entries, e => e.FileId == Id(100 + i));
                    Assert.Equal("renamed" + i, item.Name);
                    Assert.Equal(Id(3), item.ParentId);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task InvalidBatchCannotAdvanceCheckpointOrPartiallyWriteItsEarlierRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-NtfsSync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = new NtfsSyncEngine(new(root));
            using var volume = new Volume();
            var before = await engine.BuildAsync("fixture", volume, default);
            volume.Changes.Add(Record(10, 2, 2, 0x100, "valid-before-invalid"));
            volume.Changes.Add(Record(11, 2, 3, 0x100, ".."));
            volume.Next = 4;
            Assert.Equal("REBUILD_REQUIRED", (await Assert.ThrowsAsync<SysBoxOperationException>(() => engine.CatchUpAsync("fixture", volume, default))).Error.Code);
            var after = engine.ReadSnapshot("fixture");
            Assert.Equal(before.Checkpoint, after.Checkpoint);
            Assert.Equal("RebuildRequired", after.State);
            Assert.DoesNotContain(after.Entries, e => e.FileId == Id(10));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task JournalReplacementDuringBuildPreservesThePreviousPublishedGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-NtfsSync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = new NtfsSyncEngine(new(root));
            using var volume = new Volume();
            var before = await engine.BuildAsync("fixture", volume, default);
            volume.Baseline.Add(Record(90, 2, 1, 0, "unpublished"));
            volume.DuringEnumeration = () => volume.Journal++;
            Assert.Equal("REBUILD_REQUIRED", (await Assert.ThrowsAsync<SysBoxOperationException>(() => engine.BuildAsync("fixture", volume, default))).Error.Code);
            var after = engine.ReadSnapshot("fixture");
            Assert.Equal(before.Generation, after.Generation);
            Assert.Equal(before.JournalId, after.JournalId);
            Assert.Equal(before.Checkpoint, after.Checkpoint);
            Assert.DoesNotContain(after.Entries, e => e.FileId == Id(90));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task InterruptedReplayRestartsAtItsLastCommittedCheckpointWithoutLosingData()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-NtfsRecovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = new NtfsSyncEngine(new(root));
            using var volume = new Volume();
            await engine.BuildAsync("fixture", volume, default);
            for (var i = 0; i < 200; i++)
                volume.Changes.Add(Record(10 + i, 2, 2 + i, 0x100, "file" + i));
            volume.Next = 202;
            volume.FailReadAt = 2;
            await Assert.ThrowsAsync<IOException>(() => engine.CatchUpAsync("fixture", volume, default));
            var interrupted = engine.ReadSnapshot("fixture");
            Assert.Equal(130, interrupted.Checkpoint);
            Assert.Equal(131, interrupted.Entries.Count);
            Assert.Equal("Failed", interrupted.State);
            volume.FailReadAt = 0;
            var restarted = new NtfsSyncEngine(new(root));
            await restarted.CatchUpAsync("fixture", volume, default);
            var complete = restarted.ReadSnapshot("fixture");
            Assert.Equal(202, complete.Checkpoint);
            Assert.Equal(203, complete.Entries.Count);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task UnsupportedHardlinkChangesRequireReconciliationAndNeverSilentlyDropOtherNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-NtfsLinks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = new NtfsSyncEngine(new(root));
            using var volume = new Volume();
            var before = await engine.BuildAsync("fixture", volume, default);
            volume.Changes.Add(Record(20, 2, 2, 0x10000, "second-link"));
            volume.Next = 3;
            Assert.Equal("REBUILD_REQUIRED", (await Assert.ThrowsAsync<SysBoxOperationException>(() => engine.CatchUpAsync("fixture", volume, default))).Error.Code);
            Assert.Equal(before.Checkpoint, engine.ReadSnapshot("fixture").Checkpoint);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ExplicitCacheClearAlsoRemovesExperimentalNtfsMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-NtfsClear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new IndexDatabase(root);
            var engine = new NtfsSyncEngine(database);
            using var volume = new Volume();
            await engine.BuildAsync("fixture", volume, default);
            await database.ClearAsync(default);
            Assert.Equal("NTFS_STATE_UNAVAILABLE", Assert.Throws<SysBoxOperationException>(() => engine.ReadSnapshot("fixture")).Error.Code);
        }
        finally { Directory.Delete(root, true); }
    }
}
