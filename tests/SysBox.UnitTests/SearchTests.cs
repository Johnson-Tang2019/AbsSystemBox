// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;
using SysBox.FileIndex;
using SysBox.Presentation;
using SysBox.Search;
using Xunit.Abstractions;

namespace SysBox.UnitTests;

public sealed class SearchTests : IDisposable
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), "SysBox-SearchTests-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly IndexDatabase database;
    private readonly DirectoryWalkIndexer indexer;
    private readonly IndexedSearchService search;
    public SearchTests()
    {
        root = Directory.CreateDirectory(Path.Combine(sandbox, "source")).FullName;
        database = new(Path.Combine(sandbox, "cache")); indexer = new(database); search = new(database);
    }
    private Task<IndexProgress> Scan() => indexer.ScanAsync(root, new(), new IndexControl(), null, default);

    [Theory]
    [InlineData("size:>1GB", "size", ">", "1073741824")]
    [InlineData("*.pt", "ext", "=", "PT")]
    [InlineData("ext:ONNX", "ext", "=", "ONNX")]
    [InlineData("\"model final\"", "name", "contains", "MODEL FINAL")]
    [InlineData("\"name:literal\"", "name", "contains", "NAME:LITERAL")]
    [InlineData("type:folder", "type", "=", "folder")]
    public void ParserBuildsTypedTerms(string input, string field, string op, string value)
    {
        var term = Assert.Single(SearchQueryParser.Parse(input).Terms);
        Assert.Equal(new SearchTerm(field, op, value), term);
    }

    [Theory]
    [InlineData("unknown:value")]
    [InlineData("size:1GB")]
    [InlineData("size:>999999999999999999999999TB")]
    [InlineData("size:>-1B")]
    [InlineData("modified:>2026-99-01")]
    [InlineData("modified:>2026-01-01;drop")]
    [InlineData("type:all")]
    [InlineData("ext:pt;DROP")]
    [InlineData("path:relative")]
    [InlineData("*.pt*")]
    [InlineData("model?")]
    [InlineData("\"unclosed")]
    [InlineData("\"\"")]
    public void InvalidDslIsExplicit(string input) => Assert.Equal("QUERY_INVALID", Assert.Throws<SysBoxOperationException>(() => SearchQueryParser.Parse(input)).Error.Code);

    [Fact]
    public void ParserEnforcesInputAndTermBudgets()
    {
        Assert.Throws<SysBoxOperationException>(() => SearchQueryParser.Parse(new string('a', 2049)));
        Assert.Throws<SysBoxOperationException>(() => SearchQueryParser.Parse(string.Join(' ', Enumerable.Repeat("a", 33))));
    }

    [Fact]
    public async Task ChineseSpacesFiltersQuotesAndInjectionRemainLiteral()
    {
        var child = Directory.CreateDirectory(Path.Combine(root, "folder space")).FullName;
        File.WriteAllBytes(Path.Combine(child, "中文 模型.pt"), new byte[20]);
        File.WriteAllBytes(Path.Combine(root, "中文 模型.pt"), new byte[5]);
        File.WriteAllText(Path.Combine(root, "100_percent%.txt"), "a");
        File.WriteAllText(Path.Combine(root, "o'brien.txt"), "a");
        await Scan();
        var page = await search.QueryAsync(new("中文"), default); Assert.Equal(2, page.Hits.Count); Assert.Contains("短查询", page.Notice);
        Assert.Single((await search.QueryAsync(new("\"中文 模型\" ext:pt size:>10B type:file"), default)).Hits);
        Assert.Single((await search.QueryAsync(new($"ext:pt path:\"{child}\""), default)).Hits);
        Assert.Single((await search.QueryAsync(new("100_percent%"), default)).Hits);
        Assert.Single((await search.QueryAsync(new("o'brien"), default)).Hits);
        Assert.Empty((await search.QueryAsync(new("\"' OR 1=1 --\""), default)).Hits);
        Assert.Single((await search.QueryAsync(new("type:folder modified:>=2000-01-01"), default)).Hits);
        page = await search.QueryAsync(new("ext:pt", SearchSort.SizeDescending), default);
        Assert.Equal((ulong)20, page.Hits[0].Entry.LogicalSizeBytes);
        Assert.Equal(DataCompleteness.Complete, page.Completeness);
    }

    [Fact]
    public async Task BoundedPagingDetectsGenerationChangeAndDeduplicatesOverlappingScopes()
    {
        var child = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        for (var i = 0; i < 230; i++) File.WriteAllText(Path.Combine(child, $"model-{i:D4}.pt"), "x");
        await Scan(); await indexer.ScanAsync(child, new(), new IndexControl(), null, default);
        var page1 = await search.QueryAsync(new("ext:pt", SearchSort.Path), default);
        Assert.Equal(100, page1.Hits.Count); Assert.True(page1.HasMore);
        var page2 = await search.QueryAsync(new("ext:pt", SearchSort.Path, 100, Snapshot: page1.Snapshot), default);
        Assert.Equal(100, page2.Hits.Count); Assert.Empty(page1.Hits.Select(h => h.Entry.Path).Intersect(page2.Hits.Select(h => h.Entry.Path)));
        var page3 = await search.QueryAsync(new("ext:pt", SearchSort.Path, 200, Snapshot: page1.Snapshot), default);
        Assert.Equal(30, page3.Hits.Count); Assert.False(page3.HasMore);
        await Scan();
        var error = await Assert.ThrowsAsync<SysBoxOperationException>(() => search.QueryAsync(new("ext:pt", Offset: 100, Snapshot: page1.Snapshot), default));
        Assert.Equal("INDEX_CHANGED", error.Error.Code);
    }

    [Fact]
    public async Task EmptyIndexCancellationHiddenAndPartialHaveDistinctStates()
    {
        var error = await Assert.ThrowsAsync<SysBoxOperationException>(() => search.QueryAsync(new("file"), default));
        Assert.Equal("INDEX_NOT_READY", error.Error.Code);
        File.WriteAllText(Path.Combine(root, "file.txt"), "data");
        Directory.CreateDirectory(Path.Combine(root, "excluded"));
        await indexer.ScanAsync(root, new() { ExcludedDirectories = [Path.Combine(root, "excluded")] }, new IndexControl(), null, default);
        var page = await search.QueryAsync(new("file"), default); Assert.Equal(DataCompleteness.Partial, page.Completeness);
        Assert.Empty((await search.QueryAsync(new("absent"), default)).Hits);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.QueryAsync(new("file"), cancellation.Token));
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(Path.Combine(root, "file.txt"), FileAttributes.Hidden); await Scan();
            Assert.Empty((await search.QueryAsync(new("file"), default)).Hits);
            Assert.Single((await search.QueryAsync(new("file", IncludeHidden: true), default)).Hits);
        }
    }

    [Fact]
    public async Task DebounceLateResultsAndPageLeavingAreIsolated()
    {
        var client = new DelayedSearch(); var viewModel = new SearchViewModel(client); viewModel.Activate();
        viewModel.Query = "old";
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var oldTask = viewModel.CurrentSearch;
        viewModel.Query = "new"; viewModel.Query = "latest";
        client.Finish.TrySetResult(new([], false, "old", DataCompleteness.Complete, "old", 1));
        await oldTask; await viewModel.CurrentSearch;
        Assert.Equal(["old", "latest"], client.Queries);
        Assert.DoesNotContain("old", viewModel.Status);
        viewModel.Query = "after-leaving"; viewModel.Deactivate(); await viewModel.CurrentSearch;
        Assert.Empty(viewModel.Results); Assert.False(viewModel.IsBusy);
    }
    private sealed class DelayedSearch : IIndexedSearchService
    {
        public List<string> Queries { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SearchPageResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SearchPageResult> QueryAsync(SearchPageRequest request, CancellationToken cancellationToken)
        {
            Queries.Add(request.Query);
            if (request.Query == "old") { Started.TrySetResult(); return Finish.Task; }
            return Task.FromResult(new SearchPageResult([], false, request.Query, DataCompleteness.Complete, request.Query, 1));
        }
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); }
}

public sealed class SearchIndexExperiment(ITestOutputHelper output)
{
    [Fact]
    public void CompareBtreeAndFtsTrigramOnTenThousandSyntheticNames()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE names(name TEXT); CREATE INDEX ix_names ON names(name); CREATE VIRTUAL TABLE names_fts USING fts5(name,tokenize='trigram')";
        command.ExecuteNonQuery();
        using (var transaction = connection.BeginTransaction())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO names VALUES($n)"; command.Parameters.Add("$n", SqliteType.Text);
            for (var i = 0; i < 10_000; i++) { command.Parameters[0].Value = $"MODEL-{i:D5}-中文数据.PT"; command.ExecuteNonQuery(); }
            command.CommandText = "INSERT INTO names_fts SELECT name FROM names"; command.Parameters.Clear(); command.ExecuteNonQuery(); transaction.Commit();
        }
        command.Transaction = null;
        double Measure(string sql, long expected)
        {
            command.CommandText = sql; var clock = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++) Assert.Equal(expected, (long)command.ExecuteScalar()!);
            return clock.Elapsed.TotalMilliseconds / 20;
        }
        var prefix = Measure("SELECT count(*) FROM names WHERE name>='MODEL-000' AND name<'MODEL-001'", 100);
        var substring = Measure("SELECT count(*) FROM names WHERE instr(name,'000')>0", 109);
        var trigram = Measure("SELECT count(*) FROM names_fts WHERE names_fts MATCH '\"000\"'", 109);
        var shortMatch = Measure("SELECT count(*) FROM names_fts WHERE names_fts MATCH '\"中文\"'", 0);
        command.CommandText = "SELECT sqlite_version()";
        output.WriteLine(JsonSerializer.Serialize(new { sqliteVersion = command.ExecuteScalar(), rows = 10000, repetitions = 20,
            btreePrefixMs = prefix, substringScanMs = substring, trigramMs = trigram, twoCharacterFtsMatches = 0, shortMatchMs = shortMatch }));
    }
}
