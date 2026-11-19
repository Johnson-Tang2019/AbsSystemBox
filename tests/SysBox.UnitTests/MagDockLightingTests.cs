// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class MagDockLightingTests
{
    private const int PayloadStart = 7;

    [Fact]
    public void MatchesOnlyTheMagDockInputAndOutputCollection()
    {
        Assert.True(MagDockLightingReport.Matches(0x3837, 0x1012, 0xff00, 1, 65, 65));
        Assert.False(MagDockLightingReport.Matches(0x5253, 0x1012, 0xff00, 1, 65, 65));
        Assert.False(MagDockLightingReport.Matches(0x3837, 0x4150, 0xff00, 1, 65, 65));
        Assert.False(MagDockLightingReport.Matches(0x3837, 0x1012, 0xff01, 1, 65, 65));
        Assert.False(MagDockLightingReport.Matches(0x3837, 0x1012, 0xff00, 2, 65, 65));
        Assert.False(MagDockLightingReport.Matches(0x3837, 0x1012, 0xff00, 1, 65, 64));
        Assert.False(MagDockLightingReport.Matches(0x3837, 0x1012, 0xff00, 1, 64, 65));
    }

    [Fact]
    public void CapturedReportDecodesAndIncludesOpaqueBytesInTheRevision()
    {
        var request = MagDockLightingReport.CreateReadRequest();
        Assert.Equal(65, request.Length);
        Assert.Equal("00AA0700000000", Convert.ToHexString(request.AsSpan(0, PayloadStart)));
        Assert.False(request.AsSpan(PayloadStart).ContainsAnyExcept((byte)0));

        var report = CapturedReply();
        var snapshot = Decode(report);
        var state = snapshot.Describe();
        Assert.True(state.IsEnabled);
        Assert.Equal(3, state.Mode);
        Assert.Equal(0xff0000u, state.ColorRgb);
        Assert.Equal(2, state.Brightness);
        Assert.Equal(2, state.Speed);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, state.EditableModes);
        Assert.Equal(64, state.EditRevision.Length);
        Assert.Equal(state.EditRevision, Decode(report).Describe().EditRevision);

        report[PayloadStart + 12] = 0xa5;
        Assert.True(snapshot.SameSettings(Decode(report)));
        Assert.NotEqual(state.EditRevision, Decode(report).Describe().EditRevision);
    }

    [Fact]
    public void WriteContainsAllTenFieldsAndUsesTheReportedDirection()
    {
        var baseline = CapturedReply();
        baseline[PayloadStart + 2] = 7;
        baseline[PayloadStart + 9] = 0x55;
        baseline[PayloadStart + 29] = 4;
        var original = Decode(baseline);
        var desired = Assert.IsType<MagDockLightingReport.Snapshot>(original.Apply(
            new MagDockLightingChange(IsEnabled: false, Mode: 0, ColorRgb: 0x12abef, Brightness: 4)));
        var report = Assert.IsType<byte[]>(desired.CreateWriteRequest());
        Assert.Equal(65, report.Length);
        Assert.Equal("00AA270000000A00000702040012ABEF04", Convert.ToHexString(report.AsSpan(0, 17)));
        Assert.False(report.AsSpan(17).ContainsAnyExcept((byte)0));
        Assert.Equal(3, original.Describe().Mode);
        Assert.Equal(0xff0000u, original.Describe().ColorRgb);
        Assert.False(original.SameSettings(desired));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0xab)]
    [InlineData(2, 39)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 29)]
    [InlineData(6, 255)]
    [InlineData(PayloadStart, 2)]
    [InlineData(PayloadStart + 1, 6)]
    [InlineData(PayloadStart + 2, 3)]
    [InlineData(PayloadStart + 3, 5)]
    [InlineData(PayloadStart + 4, 5)]
    [InlineData(PayloadStart + 5, 2)]
    public void RejectsMalformedRepliesAndUnknownStates(int index, int badValue)
    {
        var report = CapturedReply();
        report[index] = (byte)badValue;
        Assert.Null(MagDockLightingReport.Decode(report));
    }

    [Fact]
    public void RejectsUnderfilledAndOverfilledReports()
    {
        Assert.Null(MagDockLightingReport.Decode(CapturedReply().AsSpan(0, 64)));
        Assert.Null(MagDockLightingReport.Decode(new byte[66]));
        Assert.Null(MagDockLightingReport.Decode(MagDockLightingReport.CreateReadRequest()));
    }

    [Fact]
    public void ReadsWindowsCaptureWithADistinctDirectionAndColorEcho()
    {
        // The live Windows descriptor reports 65 bytes. Payload byte 9 echoes blue and byte 29 is direction.
        var report = new byte[65];
        Convert.FromHexString("00AA070200001E010006020400FF4CE8E8001E0400004D43484F5345204D6167446F636B01")
            .CopyTo(report, 0);
        var snapshot = Decode(report);
        Assert.Equal(0, snapshot.Describe().Mode);
        Assert.Equal(0xff4ce8u, snapshot.Describe().ColorRgb);
        Assert.Equal(4, snapshot.Describe().Brightness);
        Assert.Equal(2, snapshot.Describe().Speed);
        var write = Assert.IsType<byte[]>(snapshot.CreateWriteRequest());
        Assert.Equal("00AA270000000A010006020400FF4CE801", Convert.ToHexString(write.AsSpan(0, 17)));
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(5, 0)]
    [InlineData(3, 1)]
    public void MusicAndSyncCanBeReadButCannotBeEdited(int mode, int sync)
    {
        var report = CapturedReply();
        report[PayloadStart + 1] = (byte)mode;
        report[PayloadStart + 5] = (byte)sync;
        var snapshot = Decode(report);
        Assert.Equal(mode, snapshot.Describe().Mode);
        Assert.Empty(snapshot.Describe().EditRevision);
        Assert.Empty(snapshot.Describe().EditableModes);
        Assert.Null(snapshot.Apply(new MagDockLightingChange(IsEnabled: false)));
        Assert.Null(snapshot.CreateWriteRequest());
    }

    [Fact]
    public void RejectsUnsupportedChangesAndHonorsTheReportedModeCount()
    {
        var report = CapturedReply();
        report[PayloadStart + 2] = 4;
        var original = Decode(report);
        Assert.Equal(new[] { 0, 1, 2, 3 }, original.Describe().EditableModes);
        Assert.Null(original.Apply(new MagDockLightingChange(Mode: 4)));
        Assert.Null(original.Apply(new MagDockLightingChange(Mode: 5)));
        Assert.Null(original.Apply(new MagDockLightingChange(Brightness: -1)));
        Assert.Null(original.Apply(new MagDockLightingChange(Speed: 5)));
        Assert.Null(original.Apply(new MagDockLightingChange(ColorRgb: 0x12abef)));
        Assert.Null(original.Apply(new MagDockLightingChange(Mode: 1, ColorRgb: 0x1000000)));
    }

    [Fact]
    public void RestoresOnlyWhenPartialReadbackContainsOldAndRequestedBytes()
    {
        var report = CapturedReply();
        var original = Decode(report);
        var desired = Assert.IsType<MagDockLightingReport.Snapshot>(
            original.Apply(new MagDockLightingChange(Mode: 0, Brightness: 4)));
        report[PayloadStart + 1] = 0;
        var partial = Decode(report);
        Assert.False(partial.SameSettings(original));
        Assert.False(partial.SameSettings(desired));
        Assert.True(original.CanRestoreAfter(desired, partial));

        report[PayloadStart + 4] = 3;
        Assert.False(original.CanRestoreAfter(desired, Decode(report)));
        report[PayloadStart + 4] = 2;
        report[PayloadStart + 12] = 0xa5;
        Assert.False(original.CanRestoreAfter(desired, Decode(report)));
        report[PayloadStart + 12] = 0;
        report[PayloadStart + 29] = 1;
        Assert.False(original.CanRestoreAfter(desired, Decode(report)));
        report[PayloadStart + 29] = 0;
        report[PayloadStart + 5] = 1;
        Assert.False(original.CanRestoreAfter(desired, Decode(report)));
    }

    private static MagDockLightingReport.Snapshot Decode(byte[] report) =>
        Assert.IsType<MagDockLightingReport.Snapshot>(MagDockLightingReport.Decode(report));

    private static byte[] CapturedReply()
    {
        // OpenMouse's real cycling/red MagDock capture, prefixed with Windows report ID zero.
        var report = new byte[65];
        Convert.FromHexString("00AA070200001E010306020200FF00000000").CopyTo(report, 0);
        return report;
    }
}
