// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.FileIndex;
using SysBox.Search;
using SysBox.Storage;
namespace SysBox.UnitTests;

public sealed class NtfsProductionFeaturesTests
{
    [Fact]
    public async Task InterruptedNtfsTrackingMarksBothSearchHitsAndStorageAsHistoricalPartialData()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            using var volume = new Volume();
            var live = new NtfsLiveIndex(db);
            await live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            await live.SetFailureAsync(@"R:\", new SysBoxOperationException(new("REBUILD_REQUIRED", "Fixture gap")), default);
            var page = await new IndexedSearchService(db).QueryAsync(new("f.txt"), default);
            Assert.Equal(DataCompleteness.Partial, page.Completeness);
            Assert.Equal(DataCompleteness.Partial, Assert.Single(page.Hits).Completeness);
            Assert.Contains("增量同步已中断", page.Notice);
            var storage = await new IndexedStorageBrowser(db, new DirectoryWalkIndexer(db)).ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default);
            Assert.Equal(50, storage.Summary.LogicalBytes);
            Assert.Equal(DataCompleteness.Partial, storage.Summary.Completeness);
            Assert.Contains("增量同步已中断", storage.Notice);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ExplicitOrdinaryPublicationDetachesOnlyItsPreviousNtfsTrackingState()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-mode-" + Guid.NewGuid().ToString("N"));
        var sample = Path.Combine(folder, "sample");
        Directory.CreateDirectory(sample);
        await File.WriteAllTextAsync(Path.Combine(sample, "owned.txt"), "one");
        try
        {
            var db = new IndexDatabase(Path.Combine(folder, "index"));
            var index = new DirectoryWalkIndexer(db);
            await index.ScanAsync(sample, new(), new IndexControl(), null, default);
            var scope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sample)));
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(db.DirectoryPath, "metadata.db")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE ntfs_live(scope_id TEXT PRIMARY KEY,root TEXT,volume_id TEXT,root_file_id TEXT,generation INTEGER,journal TEXT,checkpoint INTEGER,tracking INTEGER,state TEXT,error TEXT); CREATE TABLE ntfs_cache(scope_id TEXT); CREATE TABLE ntfs_ids(scope_id TEXT); INSERT INTO ntfs_live VALUES($s,$r,'volume','id',1,'1',1,1,'Tracking',NULL)";
                command.Parameters.AddWithValue("$s", scope);
                command.Parameters.AddWithValue("$r", sample);
                command.ExecuteNonQuery();
            }
            Assert.Single(new NtfsLiveIndex(db).ReadStates());
            await index.ScanAsync(sample, new(), new IndexControl(), null, default);
            Assert.Empty(new NtfsLiveIndex(db).ReadStates());
            Assert.Single((await new IndexedSearchService(db).QueryAsync(new("owned.txt"), default)).Hits);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task SchemaTwoUpgradeRetainsBackupAndDriveLetterCaseCannotBypassStop()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-live-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            using var volume = new Volume();
            var live = new NtfsLiveIndex(db);
            await live.BuildAsync(new(VolumeId, @"r:\", Id(1)), volume, new IndexControl(), null, default);
            using (var connection = db.OpenReadConnection())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version";
                Assert.Equal(3L, command.ExecuteScalar());
            }
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "metadata.db")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE volumes DROP COLUMN snapshot_revision; PRAGMA user_version=2";
                command.ExecuteNonQuery();
            }
            await live.SetTrackingAsync(@"r:\", false, default);
            Assert.False(Assert.Single(live.ReadStates()).Tracking);
            Assert.Single(Directory.GetFiles(folder, "metadata.schema2-*.backup.db"));
            volume.Put(4, 0, 30, 32, ["\\a\\f.txt"]);
            volume.Change(4, 2);
            Assert.Null(await live.CatchUpAsync(@"R:\", volume, default));
            Assert.Equal(1, Assert.Single(live.ReadStates()).Checkpoint);
            await live.SetTrackingAsync(@"r:\", true, default);
            await live.CatchUpAsync(@"r:\", volume, default);
            Assert.Equal(2, Assert.Single(live.ReadStates()).Checkpoint);
            Assert.Equal("Tracking", Assert.Single(live.ReadStates()).State);
        }
        finally { Directory.Delete(folder, true); }
    }
    private static string Id(int value) => value.ToString("X16");
    private static readonly string VolumeId = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
    private sealed class Volume : INtfsMetadataReader
    {
        public ulong Journal = 7; public long Next = 1; public List<UsnRecord> Events = [];
        public Dictionary<string, NtfsObject> Objects = [];
        public bool FailReads;
        public Volume()
        {
            Put(1, 0x10, 0, 0, ["\\"]);
            Put(2, 0x10, 0, 0, ["\\a"]);
            Put(3, 0x10, 0, 0, ["\\b"]);
            Put(4, 0, 10, 16, ["\\a\\f.txt"]);
            Put(5, 0, 20, 32, ["\\a\\one.bin", "\\b\\two.bin"]);
        }
        public void Put(int id, uint attributes, long logical, long allocated, string[] links) => Objects[Id(id)] = new(Id(id), attributes, logical, allocated, (uint)links.Length, DateTimeOffset.UtcNow, links, "Ready");
        public UsnJournal QueryJournal() => new(Journal, 0, Next, 0);
        public NtfsBatch Enumerate(ulong continuation, long highUsn, CancellationToken token) => continuation != 0 ? new(continuation, [], true) : new(100, Objects.Values.Select(o => new UsnRecord(o.FileId, Id(1), 0, 0, o.Attributes, o.FileId == Id(1) ? "." : "seed", 2)).ToArray(), false);
        public NtfsBatch ReadChanges(long startUsn, ulong journalId, CancellationToken token) => new((ulong)Next, Events.Where(e => e.Usn >= startUsn).ToArray(), false);
        public NtfsObject[] ReadObjects(string[] ids, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailReads)
                throw new IOException("Fixture interruption");
            return ids.Select(id => Objects.TryGetValue(id, out var value) ? value : new NtfsObject(id, 0, null, null, 0, null, [], "Missing")).ToArray();
        }
        public void Change(int id, uint reason, uint attributes = 0)
        {
            Events.Add(new(Id(id), Id(1), Next++, reason, attributes, "event", 2));
        }
        public void Dispose()
        {
        }
    }
    [Fact]
    public async Task SharedQueriesObserveSizeMoveAndHardlinkDeltasWithAtomicRevisionChanges()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            var live = new NtfsLiveIndex(db);
            using var volume = new Volume();
            var report = await live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            Assert.Equal(3, report.Files);
            var storage = new IndexedStorageBrowser(db, new DirectoryWalkIndexer(db));
            var before = await storage.ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default);
            Assert.Equal(50, before.Summary.LogicalBytes);
            var search = new IndexedSearchService(db);
            var page = await search.QueryAsync(new("ext:bin", Limit: 1), default);
            Assert.True(page.HasMore);
            volume.Put(4, 0, 15, 24, ["\\a\\f.txt"]);
            volume.Change(4, 2);
            await live.CatchUpAsync(@"R:\", volume, default);
            Assert.Equal(55, (await storage.ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default)).Summary.LogicalBytes);
            Assert.Equal("STORAGE_CHANGED", (await Assert.ThrowsAsync<SysBoxOperationException>(() => storage.ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default, @"R:\", before.Generation))).Error.Code);
            Assert.Equal("INDEX_CHANGED", (await Assert.ThrowsAsync<SysBoxOperationException>(() => search.QueryAsync(new("ext:bin", Offset: 1, Limit: 1, Snapshot: page.Snapshot), default))).Error.Code);
            volume.Put(2, 0x10, 0, 0, ["\\b\\moved"]);
            volume.Change(2, 0x2000, 0x10);
            await live.CatchUpAsync(@"R:\", volume, default);
            var moved = await storage.ReadAsync(@"R:\b\moved", StorageSort.LogicalBytes, 0, 20, default);
            Assert.Equal(35, moved.Summary.LogicalBytes);
            Assert.Single((await search.QueryAsync(new("f.txt"), default)).Hits);
            volume.Put(5, 0, 20, 32, ["\\b\\two.bin"]);
            volume.Change(5, 0x10000);
            await live.CatchUpAsync(@"R:\", volume, default);
            Assert.Equal(35, (await storage.ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default)).Summary.LogicalBytes);
            Assert.Single((await search.QueryAsync(new("ext:bin"), default)).Hits);
            Assert.Equal(volume.Next, Assert.Single(live.ReadStates()).Checkpoint);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task JournalReplacementRequiresRebuildAndKeepsPublishedSearchResults()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-live-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            var live = new NtfsLiveIndex(db);
            using var volume = new Volume();
            await live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            volume.Journal++;
            Assert.Equal("REBUILD_REQUIRED", (await Assert.ThrowsAsync<SysBoxOperationException>(() => live.CatchUpAsync(@"R:\", volume, default))).Error.Code);
            Assert.Equal("RebuildRequired", Assert.Single(live.ReadStates()).State);
            Assert.Single((await new IndexedSearchService(db).QueryAsync(new("f.txt"), default)).Hits);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task HardlinkAllocationIsDeduplicatedByPhysicalIdentity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-unique-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            using var volume = new Volume();
            await new NtfsLiveIndex(db).BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            var view = await new IndexedStorageBrowser(db, new DirectoryWalkIndexer(db)).ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default);
            Assert.Equal(48, view.UniqueKnownAllocatedBytes);
            Assert.Equal(0, view.UniqueUnknownObjects);
            Assert.Equal(80, view.Summary.KnownAllocatedBytes);
            var hits = await new IndexedSearchService(db).QueryAsync(new("ext:bin"), default);
            Assert.Equal(Id(5), hits.Hits[0].Entry.FileId);
            Assert.Equal(hits.Hits[0].Entry.FileId, hits.Hits[1].Entry.FileId);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ThousandMixedJournalEventsConvergeWithoutRetainingDeletedIdentities()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-live-thousand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            using var volume = new Volume();
            var live = new NtfsLiveIndex(db);
            await live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            for (var i = 0; i < 250; i++)
            {
                volume.Change(100 + i, 0x100);
                volume.Change(100 + i, 0x2000);
                volume.Change(100 + i, 2);
                volume.Change(100 + i, 0x200);
            }
            await live.CatchUpAsync(@"R:\", volume, default);
            Assert.Equal(1001, Assert.Single(live.ReadStates()).Checkpoint);
            using var connection = db.OpenReadConnection();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM ntfs_cache";
            Assert.Equal(5L, query.ExecuteScalar());
            Assert.Equal(50, (await new IndexedStorageBrowser(db, new DirectoryWalkIndexer(db)).ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default)).Summary.LogicalBytes);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task FailedDeltaAndCancelledRebuildRetainCheckpointAndSnapshotThenRecover()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-live-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            using var volume = new Volume();
            var live = new NtfsLiveIndex(db);
            await live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            volume.Put(4, 0, 99, 104, ["\\a\\f.txt"]);
            volume.Change(4, 2);
            volume.FailReads = true;
            await Assert.ThrowsAsync<IOException>(() => live.CatchUpAsync(@"R:\", volume, default));
            Assert.Equal(1, Assert.Single(live.ReadStates()).Checkpoint);
            var storage = new IndexedStorageBrowser(db, new DirectoryWalkIndexer(db));
            Assert.Equal(50, (await storage.ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default)).Summary.LogicalBytes);
            await Assert.ThrowsAsync<IOException>(() => live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, cancel.Token));
            Assert.Single((await new IndexedSearchService(db).QueryAsync(new("f.txt"), default)).Hits);
            volume.FailReads = false;
            await live.CatchUpAsync(@"R:\", volume, default);
            Assert.Equal(139, (await storage.ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default)).Summary.LogicalBytes);
            Assert.Equal("Tracking", Assert.Single(live.ReadStates()).State);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task DirectoryDeletionPreservesExternalHardlinkAndNewNestedEntriesPublishAtomically()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-live-subtree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            using var volume = new Volume();
            var live = new NtfsLiveIndex(db);
            await live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            volume.Objects.Remove(Id(2));
            volume.Objects.Remove(Id(4));
            volume.Put(5, 0, 20, 32, ["\\b\\two.bin"]);
            volume.Change(2, 0x200, 0x10);
            await live.CatchUpAsync(@"R:\", volume, default);
            var search = new IndexedSearchService(db);
            Assert.Empty((await search.QueryAsync(new("f.txt"), default)).Hits);
            Assert.Single((await search.QueryAsync(new("ext:bin"), default)).Hits);
            volume.Put(8, 0, 7, 8, ["\\new\\child.txt"]);
            volume.Put(7, 0x10, 0, 0, ["\\new"]);
            volume.Change(8, 0x100);
            volume.Change(7, 0x100, 0x10);
            await live.CatchUpAsync(@"R:\", volume, default);
            var storage = new IndexedStorageBrowser(db, new DirectoryWalkIndexer(db));
            Assert.Equal(27, (await storage.ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default)).Summary.LogicalBytes);
            Assert.Single((await search.QueryAsync(new("child.txt"), default)).Hits);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task UnreadableMetadataIsPartialAndRetriesWithoutNewJournalEvents()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SysBox-live-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var db = new IndexDatabase(folder);
            using var volume = new Volume();
            var live = new NtfsLiveIndex(db);
            await live.BuildAsync(new(VolumeId, @"R:\", Id(1)), volume, new IndexControl(), null, default);
            var previous = volume.Objects[Id(4)];
            volume.Objects[Id(4)] = previous with
            {
                State = "Unavailable",
                Error = "ACCESS_DENIED",
                Links = [],
                LogicalBytes = null,
                AllocatedBytes = null
            };
            volume.Change(4, 2);
            var partial = await live.CatchUpAsync(@"R:\", volume, default);
            Assert.NotNull(partial);
            Assert.Equal(IndexRunState.Partial, partial.State);
            Assert.Equal(1, partial.IoErrors);
            volume.Put(4, 0, 30, 32, ["\\a\\f.txt"]);
            var recovered = await live.CatchUpAsync(@"R:\", volume, default);
            Assert.NotNull(recovered);
            Assert.Equal(IndexRunState.Complete, recovered.State);
            Assert.Equal(2, Assert.Single(live.ReadStates()).Checkpoint);
            Assert.Equal(70, (await new IndexedStorageBrowser(db, new DirectoryWalkIndexer(db)).ReadAsync(@"R:\", StorageSort.LogicalBytes, 0, 20, default)).Summary.LogicalBytes);
        }
        finally { Directory.Delete(folder, true); }
    }
}
