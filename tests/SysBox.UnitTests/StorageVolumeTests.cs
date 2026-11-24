// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.FileIndex;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class StorageVolumeTests
{
    private static readonly string Root = Path.GetPathRoot(Path.GetTempPath())!;

    [Fact]
    public async Task VolumeAnalysisReadsItsSnapshotAndKeepsThatScopeForDescendants()
    {
        var browser = new VolumeBrowser();
        var viewModel = new StorageViewModel(browser, () => new IndexControl());

        await viewModel.AnalyzeVolumeAsync(Root);

        Assert.Equal(Root, browser.Scanned);
        Assert.Equal(Root, viewModel.Current?.ScopeRoot);
        Assert.Contains("部分覆盖", viewModel.Summary);
        var child = Path.Combine(Root, "child");
        await viewModel.LoadAsync(child);
        Assert.Equal(child, viewModel.Path);
        Assert.Equal(Root, browser.RequestedScope);
        Assert.Equal(12L, browser.RequestedGeneration);
    }

    [Fact]
    public async Task CancellingVolumeAnalysisRetainsThePriorSnapshot()
    {
        var browser = new VolumeBrowser { WaitForCancellation = true };
        var viewModel = new StorageViewModel(browser, () => new IndexControl());
        await viewModel.LoadAsync(Root);
        var previous = viewModel.Current;

        var scan = viewModel.AnalyzeVolumeAsync(Root);
        Assert.True(viewModel.IsBusy);
        viewModel.Cancel();
        await scan;

        Assert.Same(previous, viewModel.Current);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("取消", viewModel.Status);
    }

    [Fact]
    public async Task VolumeEntryPointRejectsDirectoryAndDevicePathsWithoutScanning()
    {
        var indexer = new DirectoryWalkIndexer(new IndexDatabase(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        foreach (var target in new[] { Path.Combine(Root, "child"), @"\\server\share\", @"\\?\Volume{bad}\" })
        {
            var failure = await Assert.ThrowsAsync<SysBoxOperationException>(
                () => indexer.ScanVolumeAsync(target, new IndexControl(), null, default));
            Assert.Equal("STORAGE_VOLUME_INVALID", failure.Error.Code);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => indexer.ScanVolumeAsync(Root, new IndexControl(), null, cancelled.Token));
    }

    private sealed class VolumeBrowser : IStorageVolumeBrowser
    {
        public string? Scanned { get; private set; }
        public string? RequestedScope { get; private set; }
        public long? RequestedGeneration { get; private set; }
        public bool WaitForCancellation { get; init; }

        public Task<IndexProgress> AnalyzeAsync(string path, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token)
            => throw new InvalidOperationException("Expected the volume-specific path.");

        public async Task<IndexProgress> AnalyzeVolumeAsync(string root, IIndexControl control,
            IProgress<IndexProgress>? progress, CancellationToken token)
        {
            Scanned = root;
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var now = DateTimeOffset.UtcNow;
            return new(root, IndexRunState.Partial, 1, 1, 1, 0, 0, 0, 24, now, now);
        }

        public Task<StorageView> ReadAsync(string path, StorageSort sort, int offset, int limit, CancellationToken token,
            string? scopeRoot = null, long? expectedGeneration = null)
        {
            RequestedScope = scopeRoot;
            RequestedGeneration = expectedGeneration;
            var summary = new StorageItem(path, path, true, 24, 0, 1, 1, 1, 0,
                DataCompleteness.Partial, DateTimeOffset.UtcNow);
            return Task.FromResult(new StorageView(summary, [], false, 12, "volume", Root, 1, "部分覆盖。"));
        }
    }
}
