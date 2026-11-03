// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Memory;
using SysBox.Native;
namespace SysBox.UnitTests;

public sealed class UiRevisionFeaturesTests
{
    private sealed class Processes : IWindowsMemoryApi
    {
        public bool IsSupported => true;
        public MemoryStatusEx ReadPhysical() => throw new NotSupportedException();
        public PerformanceInformation ReadPerformance() => throw new NotSupportedException();
        public ProcessIdBatch EnumerateProcessIds(CancellationToken token) => new(Enumerable.Range(1, 250).Select(i => (uint)i).ToArray(), true);
        public NativeProcessSample ReadProcess(uint id) => new(id, $"C:\\fixture\\p{id}.exe", DateTimeOffset.UtcNow, (300 - id) * 1048576UL, (id == 250 ? 10000UL : 1) * 1048576);
    }
    [Fact]
    public async Task PrivateCommitRankingHappensBeforeTheTwoHundredProcessLimit()
    {
        var monitor = new WindowsProcessMemoryMonitor(new Processes());
        var working = await monitor.ReadAsync(ProcessMemorySort.WorkingSet, default);
        var commit = await monitor.ReadAsync(ProcessMemorySort.PrivateCommit, default);
        Assert.Equal(200, commit.Value.Entries.Count);
        Assert.Equal(250u, commit.Value.Entries[0].ProcessId);
        Assert.DoesNotContain(working.Value.Entries, e => e.ProcessId == 250);
        Assert.Equal(250, commit.Value.ReadableCount);
    }
    [Fact]
    public void BatteryHealthDistinguishesNoBatteryUnknownCapacityAndMeasuredWear()
    {
        Assert.Equal("电池：无", BatteryHealth.Describe(new(BatteryChargeState.NotPresent, null, null, null, DateTimeOffset.UtcNow)));
        Assert.Contains("80.0%", BatteryHealth.Describe(new(BatteryChargeState.Discharging, null, 80000, 40000, DateTimeOffset.UtcNow, DesignCapacityMilliwattHours: 100000)));
        Assert.Contains("未提供", BatteryHealth.Describe(new(BatteryChargeState.Idle, null, 80000, 40000, DateTimeOffset.UtcNow)));
    }
    [Fact]
    public void NineAreasRejectForgedCommandsAndUseFixedNativeBufferLayouts()
    {
        var selected = MemoryCleanupCatalog.Validate(MemoryCleanupCatalog.Order.Reverse());
        Assert.Equal(9, selected.Length);
        Assert.Equal(MemoryCleanupArea.WorkingSets, selected[0]);
        Assert.Throws<ArgumentException>(() => MemoryCleanupCatalog.Validate([(MemoryCleanupArea)999]));
        Assert.Throws<ArgumentException>(() => MemoryCleanupCatalog.Validate([MemoryCleanupArea.Standby, MemoryCleanupArea.Standby]));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(SystemMemoryCleanup.Payload(MemoryCleanupArea.WorkingSets).Payload!));
        var cache = SystemMemoryCleanup.Payload(MemoryCleanupArea.ModifiedFileCache);
        Assert.Equal(81, cache.Class);
        Assert.Equal(64, cache.Payload!.Length);
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(cache.Payload.AsSpan(24)));
        Assert.Equal(24, SystemMemoryCleanup.Payload(MemoryCleanupArea.CombineMemory).Payload!.Length);
        Assert.Null(SystemMemoryCleanup.Payload(MemoryCleanupArea.RegistryCache).Payload);
        Assert.False(new CleanupPolicy().Enabled);
        Assert.Equal(90, new CleanupPolicy().ThresholdPercent);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void DockingGeometryKeepsWholeTextCircleAndFourEdgeBarsInsideTheWorkArea(double scale)
    {
        foreach (var edge in Enum.GetValues<BallEdge>())
        foreach (var collapsed in new[] { false, true })
        {
            if (edge == BallEdge.None && collapsed)
                continue;
            var r = FloatingBallLayout.Arrange(-900, 600, edge, collapsed, scale, -1920, 0, 1920, 1040);
            Assert.InRange(r.X, -1920, -r.Width);
            Assert.InRange(r.Y, 0, 1040 - r.Height);
            if (!collapsed)
            {
                Assert.Equal((int)Math.Ceiling(64 * scale), r.Width);
                Assert.Equal(r.Width, r.Height);
            }
            else
                Assert.Equal((int)Math.Ceiling(14 * scale), Math.Min(r.Width, r.Height));
        }
        Assert.Equal(BallEdge.Left, FloatingBallLayout.Detect(-1920 + 32 * scale, 400, scale, -1920, 0, 1920, 1040));
    }
}
