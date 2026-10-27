// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;
using SysBox.FileIndex;

namespace SysBox.UnitTests;

public sealed class IndexTests : IDisposable
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), "SysBox-IndexTests-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly IndexDatabase database;
    private readonly DirectoryWalkIndexer indexer;
    public IndexTests()
    {
        root = Path.Combine(sandbox, "样本 空格"); Directory.CreateDirectory(root);
        database = new(Path.Combine(sandbox, "cache")); indexer = new(database);
    }
    private Task<IndexProgress> Scan(IndexOptions? options = null, IIndexControl? control = null, IProgress<IndexProgress>? progress = null, CancellationToken token = default)
        => indexer.ScanAsync(root, options ?? new(), control ?? new IndexControl(), progress, token);
    private sealed class Callback(Action<IndexProgress> callback) : IProgress<IndexProgress> { public void Report(IndexProgress value) => callback(value); }

    [Fact]
    public async Task ScanReconcilesMetadataParentsAndManualRescan()
    {
        var child = Directory.CreateDirectory(Path.Combine(root, "子目录")).FullName;
        File.WriteAllBytes(Path.Combine(child, "模型 权重.pt"), new byte[123]);
        File.WriteAllText(Path.Combine(root, "empty.txt"), "");
        var report = await Scan(); Assert.Equal(IndexRunState.Complete, report.State);
        Assert.Equal(2, report.Files); Assert.Equal(2, report.Directories); Assert.Equal(123, report.LogicalBytes);
        var entries = await indexer.ReadEntriesAsync(root, 100, default);
        Assert.Equal(4, entries.Count); Assert.Equal((ulong)123, entries.Single(e => e.Name == "模型 权重.pt").LogicalSizeBytes);
        Assert.All(entries, e => Assert.Null(e.FileId));
        File.Move(Path.Combine(child, "模型 权重.pt"), Path.Combine(root, "moved.pt"));
        File.WriteAllBytes(Path.Combine(root, "moved.pt"), new byte[321]);
        report = await Scan(); Assert.Equal(321, report.LogicalBytes);
        entries = await indexer.ReadEntriesAsync(root, 100, default);
        Assert.DoesNotContain(entries, e => e.Name == "模型 权重.pt"); Assert.Contains(entries, e => e.Name == "moved.pt");
        using var db = new SqliteConnection($"Data Source={Path.Combine(database.DirectoryPath, "metadata.db")}"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar()); command.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(command.ExecuteScalar());
        command.CommandText = "SELECT count(*) FROM directory_entries WHERE parent_entry_id IS NULL"; Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task CancelAndLimitKeepPreviousPublishedGeneration()
    {
        File.WriteAllText(Path.Combine(root, "original"), "a"); await Scan();
        var generation = (await indexer.ReadRootsAsync(default)).Single().Generation;
        File.WriteAllText(Path.Combine(root, "new"), "b");
        using var source = new CancellationTokenSource();
        var report = await Scan(progress: new Callback(_ => source.Cancel()), token: source.Token);
        Assert.Equal(IndexRunState.Cancelled, report.State);
        Assert.Equal(generation, (await indexer.ReadRootsAsync(default)).Single().Generation);
        Assert.DoesNotContain(await indexer.ReadEntriesAsync(root, 100, default), e => e.Name == "new");
        report = await Scan(new() { MaxEntries = 1 }); Assert.Equal(IndexRunState.LimitReached, report.State);
        Assert.Equal(generation, (await indexer.ReadRootsAsync(default)).Single().Generation);
        await Scan(); Assert.Contains(await indexer.ReadEntriesAsync(root, 100, default), e => e.Name == "new");
    }

    [Fact]
    public async Task PauseCanResumeAndCancelWithoutHoldingWriteTransaction()
    {
        File.WriteAllText(Path.Combine(root, "file"), "a");
        var control = new IndexControl(); control.Pause();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Scan(control: control, progress: new Callback(p => { if (p.State == IndexRunState.Paused) paused.TrySetResult(); }));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(task.IsCompleted);
        control.Resume(); Assert.Equal(IndexRunState.Complete, (await task).State);
        control.Pause(); using var source = new CancellationTokenSource();
        paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var again = Scan(control: control, progress: new Callback(p => { if (p.State == IndexRunState.Paused) paused.TrySetResult(); }), token: source.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel(); Assert.Equal(IndexRunState.Cancelled, (await again).State);
    }

    [Fact]
    public async Task LinksExcludedFoldersAndCacheAreNeverTraversed()
    {
        Directory.CreateDirectory(Path.Combine(root, "exclude")); File.WriteAllText(Path.Combine(root, "exclude", "private"), "private");
        var link = Path.Combine(root, "loop");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { Arguments = $"/c mklink /J \"{link}\" \"{root}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            await process!.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, root);
        var report = await Scan(new() { ExcludedDirectories = [Path.Combine(root, "exclude")] });
        Assert.Equal(IndexRunState.Partial, report.State); Assert.Equal(1, report.ReparsePoints); Assert.Equal(1, report.Excluded);
        Assert.Equal(0, report.Files);
        // Remove the test link itself before cleanup, never its target.
        Directory.Delete(link);
    }

    [Fact]
    public async Task ClearOnlyTouchesCacheAndMissingRootKeepsGoodIndex()
    {
        var file = Path.Combine(root, "preserve"); File.WriteAllText(file, "user-data"); await Scan();
        var moved = root + "-moved"; Directory.Move(root, moved);
        try { Assert.Equal(IndexRunState.Failed, (await Scan()).State); Assert.NotEmpty(await indexer.ReadEntriesAsync(root, 100, default)); }
        finally { Directory.Move(moved, root); }
        await indexer.ClearAsync(default); Assert.Empty(await indexer.ReadRootsAsync(default));
        Assert.Equal("user-data", File.ReadAllText(file));
    }

    [Fact]
    public async Task RejectsFullVolumeAndInvalidBudgets()
    {
        await Assert.ThrowsAsync<SysBoxOperationException>(() => indexer.ScanAsync(Path.GetPathRoot(root)!, new(), new IndexControl(), null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Scan(new() { MaxEntries = 0 }));
    }

    [Fact]
    public async Task BatchesDeepTreeAndMultipleScopesRemainConsistent()
    {
        for (var i = 0; i < 600; i++) File.WriteAllText(Path.Combine(root, $"wide-{i}.txt"), "x");
        var deep = root;
        for (var i = 0; i < 60; i++) { deep = Path.Combine(deep, "deep"); Directory.CreateDirectory(deep); }
        File.WriteAllText(Path.Combine(deep, "long-path.txt"), "xx");
        var report = await Scan(); Assert.Equal(IndexRunState.Complete, report.State); Assert.Equal(601, report.Files);
        Assert.Equal(602, report.LogicalBytes);
        var other = Directory.CreateDirectory(Path.Combine(sandbox, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "other.txt"), "other");
        await indexer.ScanAsync(other, new(), new IndexControl(), null, default);
        Assert.Equal(2, (await indexer.ReadRootsAsync(default)).Count);
        Assert.DoesNotContain(await indexer.ReadEntriesAsync(root, 1000, default), e => e.Name == "other.txt");
        Assert.Single(await indexer.ReadEntriesAsync(other, 1000, default), e => !e.IsDirectory);
    }

    [Fact]
    public async Task ConcurrentWriterIsRejectedAndInterruptedStagingIsRemoved()
    {
        File.WriteAllText(Path.Combine(root, "file"), "data"); await Scan();
        using (var locked = new FileStream(Path.Combine(database.DirectoryPath, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => new DirectoryWalkIndexer(new(database.DirectoryPath)).ScanAsync(root, new(), new IndexControl(), null, default));
        using (var db = new SqliteConnection($"Data Source={Path.Combine(database.DirectoryPath, "metadata.db")}"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO file_objects(scope_id,generation,is_directory,attributes) SELECT scope_id,-1,0,0 FROM volumes";
            command.ExecuteNonQuery();
        }
        Assert.Equal(IndexRunState.Complete, (await Scan()).State);
        using var check = new SqliteConnection($"Data Source={Path.Combine(database.DirectoryPath, "metadata.db")}"); check.Open();
        using var query = check.CreateCommand(); query.CommandText = "SELECT count(*) FROM file_objects WHERE generation=-1";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        // The GUID sandbox is created and owned solely by this test instance.
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
    }
}
