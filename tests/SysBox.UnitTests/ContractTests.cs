// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Memory;
using SysBox.Search;
using SysBox.Storage;

namespace SysBox.UnitTests;

public sealed class ContractTests
{
    [Fact]
    public void FailedResultCannotBeReadAsSuccessfulData()
    {
        var error = new SysBoxError(ErrorCodes.AccessDenied, "Permission denied.", retryable: false);
        var result = Result<int>.Failure(error);
        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void SuccessfulResultPreservesValueWithoutError()
    {
        var result = Result<int>.Success(42);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void NullSuccessAndNullErrorAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Result<string>.Success(null!));
        Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void SearchRejectsUnboundedOrInvalidLimits(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSearchRequest("*.onnx", limit));

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void SearchAcceptsBoundaryLimits(int limit) =>
        Assert.Equal(limit, new FileSearchRequest("*.onnx", limit).Limit);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void SearchRejectsEmptyText(string text) =>
        Assert.Throws<ArgumentException>(() => new FileSearchRequest(text));

    [Fact]
    public void ErrorRequiresMachineCodeAndHumanMessage()
    {
        Assert.Throws<ArgumentException>(() => new SysBoxError(" ", "Failed."));
        Assert.Throws<ArgumentException>(() => new SysBoxError("FAILED", " "));
    }

    [Fact]
    public void FileIdentitySurvivesJsonWithoutIntegerPrecisionLoss()
    {
        var file = new FileEntry("volume-guid", "18446744073709551615", null,
            @"D:\样本\模型.onnx", "模型.onnx", false, null, null);
        var json = JsonSerializer.Serialize(file);
        var restored = JsonSerializer.Deserialize<FileEntry>(json);
        Assert.Equal(file, restored);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("FileId").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("LogicalSizeBytes").ValueKind);
    }

    [Fact]
    public async Task UnimplementedServicesDoNotReturnFakeEmptyResults()
    {
        var memory = await new UnavailableMemoryMonitor().ReadAsync(CancellationToken.None);
        Assert.Equal(ErrorCodes.NotImplemented, memory.Error?.Code);
        var storage = await new UnavailableStorageAnalyzer().AnalyzeAsync(@"D:\sample", CancellationToken.None);
        Assert.Equal(ErrorCodes.NotImplemented, storage.Error?.Code);
        var error = Assert.Throws<SysBoxOperationException>(() =>
            new UnavailableFileSearchService().SearchAsync(new("test"), CancellationToken.None));
        Assert.Equal(ErrorCodes.NotImplemented, error.Error.Code);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToNotImplemented()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UnavailableMemoryMonitor().ReadAsync(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UnavailableStorageAnalyzer().AnalyzeAsync(@"D:\sample", source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new UnavailableFileSearchService().SearchAsync(new("test"), source.Token));
    }
}
