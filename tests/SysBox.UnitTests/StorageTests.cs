// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;
using SysBox.FileIndex;
using SysBox.Native;
using SysBox.Presentation;
using SysBox.Storage;

namespace SysBox.UnitTests;

public sealed class StorageTests : IDisposable
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), "SysBox-StorageTests-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly IndexDatabase database;
    private readonly DirectoryWalkIndexer indexer;
    private readonly IndexedStorageBrowser browser;
    public StorageTests()
    {
        root = Directory.CreateDirectory(Path.Combine(sandbox, "source")).FullName;
        database = new(Path.Combine(sandbox, "cache")); indexer = new(database, new FakeSizes()); browser = new(database, indexer);
    }
    private sealed class FakeSizes : IFileSizeReader
    {
        public Result<FileSizeMetadata> Read(string path) => Path.GetFileName(path) == "unknown.txt" ?
            Result<FileSizeMetadata>.Failure(new("ACCESS_DENIED", "不可读")) :
            Result<FileSizeMetadata>.Success(new(new FileInfo(path).Length, 4096, Path.GetFileName(path) == "linked.txt" ? 2u : 1u, DateTimeOffset.UtcNow));
    }
    private Task<IndexProgress> Scan() => indexer.ScanAsync(root, new(), new IndexControl(), null, default);
    private Task<StorageView> Read(string? path = null) => browser.ReadAsync(path ?? root, StorageSort.LogicalBytes, 0, 100, default);

    [Fact]
    public async Task PostOrderTotalsCoverDirectFilesEmptyDirectoriesAndUnknownAllocations()
    {
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        var nested = Directory.CreateDirectory(Path.Combine(root, "a", "b", "c")).FullName;
        File.WriteAllBytes(Path.Combine(root, "direct.txt"), new byte[10]);
        File.WriteAllBytes(Path.Combine(nested, "linked.txt"), new byte[20]);
        File.WriteAllBytes(Path.Combine(nested, "unknown.txt"), new byte[30]);
        await Scan(); var view = await Read();
        Assert.Equal((await indexer.ReadEntriesAsync(root, 100, default))[0].VolumeId, view.ScopeId);
        Assert.Equal(60, view.Summary.LogicalBytes); Assert.Equal(8192, view.Summary.KnownAllocatedBytes);
        Assert.Equal(1, view.Summary.UnknownAllocatedFiles); Assert.Equal(1, view.Summary.HardLinkedEntries);
        Assert.Equal(3, view.Summary.FileCount); Assert.Equal(4, view.Summary.FolderCount);
        Assert.Equal(0, Assert.Single(view.Children, c => c.Name == "empty").LogicalBytes);
        Assert.Equal(50, (await Read(nested)).Summary.LogicalBytes);
        Assert.Equal(view.Summary.LogicalBytes, view.Children.Sum(c => c.LogicalBytes));
        Assert.Contains("未知", view.Notice); Assert.Contains("硬链接", view.Notice);
    }

    [Fact]
    public async Task ManualRescanMoveResizeAndDeleteNeverMixGenerations()
    {
        var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        File.WriteAllBytes(Path.Combine(first, "file"), new byte[15]); await Scan(); var old = await Read();
        File.Move(Path.Combine(first, "file"), Path.Combine(second, "file"));
        File.WriteAllBytes(Path.Combine(second, "file"), new byte[45]);
        Assert.Equal(15, (await Read()).Summary.LogicalBytes);
        await Scan(); var current = await Read(); Assert.NotEqual(old.Generation, current.Generation);
        Assert.Equal(0, (await Read(first)).Summary.LogicalBytes); Assert.Equal(45, (await Read(second)).Summary.LogicalBytes);
        File.Delete(Path.Combine(second, "file")); await Scan(); Assert.Equal(0, (await Read()).Summary.FileCount);
    }

    [Fact]
    public async Task PartialCoverageAndBoundedChildrenRemainExplicit()
    {
        Directory.CreateDirectory(Path.Combine(root, "excluded"));
        for (var i = 0; i < 215; i++) File.WriteAllBytes(Path.Combine(root, $"file-{i:D3}"), new byte[i]);
        await indexer.ScanAsync(root, new() { ExcludedDirectories = [Path.Combine(root, "excluded")] }, new IndexControl(), null, default);
        var view = await Read(); Assert.Equal(DataCompleteness.Partial, view.Summary.Completeness);
        Assert.Equal(100, view.Children.Count); Assert.True(view.HasMore); Assert.Equal(214, view.Children[0].LogicalBytes);
        var last = await browser.ReadAsync(root, StorageSort.FileCount, 200, 100, default); Assert.Equal(15, last.Children.Count); Assert.False(last.HasMore);
        await Assert.ThrowsAsync<SysBoxOperationException>(() => browser.ReadAsync(root, StorageSort.LogicalBytes, 0, 201, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ReadAsync(root, StorageSort.LogicalBytes, 0, 100, cancel.Token));
    }

    [Fact]
    public async Task MigrationFromVersionOnePreservesReadableOldIndexAndRequiresFreshTotals()
    {
        File.WriteAllText(Path.Combine(root, "file"), "sample"); await Scan();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(database.DirectoryPath, "metadata.db")}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            // Recreate the exact v1 columns on the existing fixture without changing source files.
            command.CommandText = "DROP TABLE folder_totals; ALTER TABLE file_objects DROP COLUMN size_state; ALTER TABLE file_objects DROP COLUMN size_updated_utc; ALTER TABLE file_objects DROP COLUMN link_count; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        Assert.NotEmpty(await indexer.ReadEntriesAsync(root, 100, default));
        Assert.Equal("STORAGE_NOT_READY", (await Assert.ThrowsAsync<SysBoxOperationException>(() => Read())).Error.Code);
        await Scan(); Assert.Equal(6, (await Read()).Summary.LogicalBytes);
        using var db = database.OpenReadConnection(); using var check = db.CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(check.ExecuteScalar());
    }

    [Fact]
    public async Task CancellingAnalysisKeepsPreviousFolderTotals()
    {
        File.WriteAllText(Path.Combine(root, "file"), "old"); await Scan(); var previous = await Read();
        File.WriteAllText(Path.Combine(root, "new"), "new");
        var control = new IndexControl(); control.Pause(); using var source = new CancellationTokenSource();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = browser.AnalyzeAsync(root, control, new Callback(p => { if (p.State == IndexRunState.Paused) paused.TrySetResult(); }), source.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10)); source.Cancel(); Assert.Equal(IndexRunState.Cancelled, (await task).State);
        Assert.Equal(previous.Generation, (await Read()).Generation); Assert.Equal(3, (await Read()).Summary.LogicalBytes);
    }
    private sealed class Callback(Action<IndexProgress> callback) : IProgress<IndexProgress> { public void Report(IndexProgress value) => callback(value); }

    [Fact]
    public async Task OverlappingScopesStayPinnedAndChangedGenerationRequiresReload()
    {
        var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        var file = Path.Combine(child, "file.txt"); File.WriteAllBytes(file, new byte[10]); await Scan();
        var vm = new StorageViewModel(browser, () => new IndexControl()); await vm.LoadAsync(root);
        var originalGeneration = vm.Current!.Generation;
        File.WriteAllBytes(file, new byte[20]); await indexer.ScanAsync(child, new(), new IndexControl(), null, default);
        Assert.Equal(20, (await Read(child)).Summary.LogicalBytes);
        await vm.LoadAsync(child); Assert.Equal(10, vm.Current!.Summary.LogicalBytes); Assert.Equal(originalGeneration, vm.Current.Generation);
        await Scan();
        await vm.UpAsync(); Assert.Null(vm.Current); Assert.Contains("快照已更新", vm.Status);
        await vm.LoadAsync(root, resetScope: true); Assert.Equal(20, vm.Current!.Summary.LogicalBytes);
    }

    [Fact]
    public void RealWindowsFileMetadataAndRepeatedHandlesAreReadOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(root, "metadata.txt"); File.WriteAllBytes(file, new byte[10000]);
        var reader = new WindowsFileSizeReader(); var metadata = reader.Read(file);
        Assert.True(metadata.IsSuccess); Assert.Equal(10000, metadata.Value.LogicalBytes); Assert.True(metadata.Value.AllocatedBytes >= 0);
        for (var i = 0; i < 100; i++) Assert.True(reader.Read(file).IsSuccess);
        // The source remains usable and its logical length stays unchanged after repeated metadata reads.
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(10000, exclusive.Length);
        Assert.False(reader.Read(root).IsSuccess); Assert.False(reader.Read(Path.Combine(root, "missing")).IsSuccess);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputBytes, IntPtr output, uint outputBytes, out uint returnedBytes, IntPtr overlapped);

    [Fact]
    public async Task RealNtfsHardLinksAndSparseFilesKeepLogicalAndAllocationMetricsSeparate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = Path.Combine(root, "original.bin"); var linked = Path.Combine(root, "alias.bin");
        File.WriteAllBytes(original, new byte[16384]);
        Assert.True(CreateHardLinkW(linked, original, IntPtr.Zero), $"CreateHardLink error {Marshal.GetLastWin32Error()}");
        var sparse = Path.Combine(root, "sparse.bin");
        using (var stream = new FileStream(sparse, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            // FSCTL_SET_SPARSE is applied only to this GUID-owned fixture file, never to a volume.
            Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
            stream.SetLength(1024 * 1024);
        }
        var native = new WindowsFileSizeReader();
        Assert.Equal(2u, native.Read(original).Value.LinkCount);
        var sparseInfo = native.Read(sparse).Value; Assert.Equal(1048576, sparseInfo.LogicalBytes);
        Assert.True(sparseInfo.AllocatedBytes < sparseInfo.LogicalBytes);
        var realIndexer = new DirectoryWalkIndexer(database, native);
        await realIndexer.ScanAsync(root, new(), new IndexControl(), null, default);
        var view = await Read(); Assert.Equal(1081344, view.Summary.LogicalBytes); Assert.Equal(2, view.Summary.HardLinkedEntries);
        Assert.Equal(0, view.Summary.UnknownAllocatedFiles);
        Assert.Equal(2 * native.Read(original).Value.AllocatedBytes + sparseInfo.AllocatedBytes, view.Summary.KnownAllocatedBytes);
    }

    [Fact]
    public async Task ViewModelNavigationAndLeavingSuppressLateReads()
    {
        Directory.CreateDirectory(Path.Combine(root, "child")); await Scan();
        var vm = new StorageViewModel(browser, () => new IndexControl()); await vm.UpAsync();
        await vm.LoadAsync(root); await vm.LoadAsync(Path.Combine(root, "child"));
        await vm.BackAsync(); Assert.Equal(root, vm.Path); await vm.ForwardAsync(); Assert.EndsWith("child", vm.Path);
        await vm.UpAsync(); Assert.Equal(root, vm.Path); Assert.False(vm.IsBusy);
        var delayed = new DelayedBrowser(); var late = new StorageViewModel(delayed, () => new IndexControl());
        var read = late.LoadAsync(root); late.Deactivate(); delayed.Completion.SetResult(await Read()); await read;
        Assert.Null(late.Current); Assert.Empty(late.Children); Assert.False(late.IsBusy);
    }
    private sealed class DelayedBrowser : IStorageBrowser
    {
        public TaskCompletionSource<StorageView> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StorageView> ReadAsync(string path, StorageSort sort, int offset, int limit, CancellationToken token,
            string? scopeRoot = null, long? expectedGeneration = null) => Completion.Task;
        public Task<IndexProgress> AnalyzeAsync(string path, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token) => throw new NotSupportedException();
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); }
}

public sealed class TreemapTests
{
    [Fact]
    public void AreasMatchWeightsStayInBoundsAndNeverOverlap()
    {
        var input = Enumerable.Range(1, 50).Select(i => new TreemapWeight(i.ToString(), null, i)).ToArray();
        var tiles = TreemapLayout.Create(input, 800, 300); var total = input.Sum(x => x.Bytes);
        Assert.Equal(50, tiles.Count);
        foreach (var tile in tiles)
        {
            Assert.InRange(tile.X, 0, 800); Assert.InRange(tile.Y, 0, 300);
            Assert.InRange(tile.X + tile.Width, 0, 800.000001); Assert.InRange(tile.Y + tile.Height, 0, 300.000001);
            Assert.Equal(tile.Item.Bytes / (double)total, tile.Width * tile.Height / 240000, 10);
        }
        for (var i = 0; i < tiles.Count; i++) for (var j = i + 1; j < tiles.Count; j++)
            Assert.True(tiles[i].X + tiles[i].Width <= tiles[j].X + 1e-8 || tiles[j].X + tiles[j].Width <= tiles[i].X + 1e-8 ||
                tiles[i].Y + tiles[i].Height <= tiles[j].Y + 1e-8 || tiles[j].Y + tiles[j].Height <= tiles[i].Y + 1e-8);
    }
    [Fact]
    public void ZeroInvalidAndManyItemsAreBounded()
    {
        Assert.Empty(TreemapLayout.Create([new("empty", null, 0)], 100, 100));
        Assert.Empty(TreemapLayout.Create([new("item", null, 1)], 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => TreemapLayout.Create([], double.NaN, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => TreemapLayout.Create([new("bad", null, -1)], 100, 100));
        var tiles = TreemapLayout.Create(Enumerable.Range(0, 1000).Select(i => new TreemapWeight(i.ToString(), null, 1)).ToArray(), 100, 100);
        Assert.Equal(64, tiles.Count); Assert.Equal(1000, tiles.Sum(t => t.Item.Bytes));
        Assert.Equal(10000, tiles.Sum(t => t.Width * t.Height), 6);
    }
}
