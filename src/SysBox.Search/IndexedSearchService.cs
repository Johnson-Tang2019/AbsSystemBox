// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;
using SysBox.FileIndex;

namespace SysBox.Search;

public sealed class IndexedSearchService(IndexDatabase database) : IIndexedSearchService, IFileSearchService
{
    public Task<SearchPageResult> QueryAsync(SearchPageRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 200 || request.Offset is < 0 or > 100_000 || !Enum.IsDefined(request.Sort))
            throw new SysBoxOperationException(new("QUERY_INVALID", "分页范围或排序无效，每页最多 200 条，深分页最多 100000 条。"));
        var ast = SearchQueryParser.Parse(request.Query);
        return Task.Run(() => Query(request, ast, token), token);
    }

    private SearchPageResult Query(SearchPageRequest request, SearchQuery ast, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(database.DirectoryPath, "metadata.db")))
            throw new SysBoxOperationException(new("INDEX_NOT_READY", "尚未建立索引，请先选择文件夹并完成扫描。"));
        var clock = Stopwatch.StartNew();
        try
        {
            using var connection = database.OpenReadConnection();
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ => token.IsCancellationRequested || clock.Elapsed > TimeSpan.FromSeconds(2) ? 1 : 0, null);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT v.scope_id,v.active_generation,s.published_report_json,s.report_json FROM volumes v JOIN index_state s USING(scope_id) ORDER BY v.scope_id";
            var identity = new StringBuilder(); var reports = new List<IndexProgress>(); var activeCount = 0; var pending = false;
            using (var state = command.ExecuteReader())
            {
                while (state.Read())
                {
                    token.ThrowIfCancellationRequested();
                    identity.Append(state.GetString(0)).Append(':').Append(state.GetInt64(1)).Append(';');
                    if (state.GetInt64(1) > 0 && !state.IsDBNull(2))
                    { activeCount++; reports.Add(JsonSerializer.Deserialize<IndexProgress>(state.GetString(2))!); }
                    var latest = JsonSerializer.Deserialize<IndexProgress>(state.GetString(3));
                    pending |= latest?.State is not (IndexRunState.Complete or IndexRunState.Partial);
                }
            }
            if (activeCount == 0) throw new SysBoxOperationException(new("INDEX_NOT_READY", "没有可用索引，请先完成一次目录扫描。"));
            var snapshot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
            if (request.Offset > 0 && (request.Snapshot is null || request.Snapshot != snapshot))
                throw new SysBoxOperationException(new("INDEX_CHANGED", "索引已经更新，请从第一页重新搜索。"));
            var completeness = !pending && reports.All(p => p.State == IndexRunState.Complete) ? DataCompleteness.Complete : DataCompleteness.Partial;
            var clauses = new List<string>(); var index = 0;
            string Param(object value) { var name = "$p" + index++; command.Parameters.AddWithValue(name, value); return name; }
            foreach (var term in ast.Terms)
            {
                switch (term.Field)
                {
                    case "name": clauses.Add($"instr(e.name_folded,{Param(term.Value)})>0"); break;
                    case "ext": clauses.Add($"o.is_directory=0 AND substr(e.name_folded,-length({Param('.' + term.Value)}))={Param('.' + term.Value)}"); break;
                    case "path":
                        var exact = Param(term.Value); var prefix = Param(term.Value + Path.DirectorySeparatorChar);
                        // Paths are folded using the same invariant .NET rule as names, including Chinese.
                        clauses.Add($"(fold_path(e.cached_path)={exact} OR instr(fold_path(e.cached_path),{prefix})=1)"); break;
                    case "type": clauses.Add($"o.is_directory={Param(term.Value == "folder" ? 1 : 0)}"); break;
                    case "size": clauses.Add($"o.logical_size {term.Operator} {Param(long.Parse(term.Value, CultureInfo.InvariantCulture))}"); break;
                    case "modified": clauses.Add($"o.last_write_utc {term.Operator} {Param(term.Value)}"); break;
                }
            }
            connection.CreateFunction<string, string>("fold_path", value => value.ToUpperInvariant(), isDeterministic: true);
            if (!request.IncludeHidden) clauses.Add("e.is_hidden=0");
            // Hide the scope root itself and collapse overlapping scopes by exact path.
            clauses.Add("e.parent_entry_id IS NOT NULL");
            var rankingTerm = ast.Terms.FirstOrDefault(t => t.Field == "name")?.Value ?? "";
            var rank = Param(rankingTerm);
            var order = request.Sort switch
            {
                SearchSort.SizeDescending => "(logical_size IS NULL),logical_size DESC,",
                SearchSort.ModifiedDescending => "(last_write_utc IS NULL),last_write_utc DESC,",
                SearchSort.Relevance => "rank,", _ => ""
            };
            command.CommandText = $"""
                WITH candidates AS (
                    SELECT e.*,o.is_directory,o.logical_size,o.last_write_utc,s.published_report_json,
                      CASE WHEN e.name_folded={rank} THEN 0 WHEN instr(e.name_folded,{rank})=1 THEN 1 ELSE 2 END AS rank,
                      ROW_NUMBER() OVER(PARTITION BY e.cached_path COLLATE BINARY ORDER BY e.generation DESC,e.entry_id) AS duplicate
                    FROM directory_entries e JOIN file_objects o USING(object_id)
                    JOIN volumes v ON v.scope_id=e.scope_id AND v.active_generation=e.generation
                    JOIN index_state s ON s.scope_id=e.scope_id
                    WHERE {string.Join(" AND ", clauses)})
                SELECT scope_id,cached_path,name,is_directory,logical_size,last_write_utc,published_report_json
                FROM candidates WHERE duplicate=1 ORDER BY {order} cached_path COLLATE BINARY LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$limit", request.Limit + 1); command.Parameters.AddWithValue("$offset", request.Offset);
            using var reader = command.ExecuteReader(); var hits = new List<FileSearchHit>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var report = JsonSerializer.Deserialize<IndexProgress>(reader.GetString(6))!;
                hits.Add(new(new(reader.GetString(0), null, null, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : checked((ulong)reader.GetInt64(4)), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)),
                    report.UpdatedUtc, report.State == IndexRunState.Complete ? DataCompleteness.Complete : DataCompleteness.Partial));
            }
            var more = hits.Count > request.Limit; if (more) hits.RemoveAt(hits.Count - 1);
            var shortQuery = ast.Terms.Any(t => t.Field == "name" && t.Value.EnumerateRunes().Count() < 3);
            var notice = completeness == DataCompleteness.Partial ? "部分索引可用，存在跳过项或上次扫描未完成。" : "基于最近一次手动扫描；文件可能已经移动。";
            if (shortQuery) notice += " 短查询使用有时限的扫描，建议增加关键词或筛选条件。";
            return new(hits.AsReadOnly(), more, snapshot, completeness, notice, clock.Elapsed.TotalMilliseconds);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9)
        {
            token.ThrowIfCancellationRequested();
            throw new SysBoxOperationException(new("QUERY_BUDGET", "查询超过两秒预算，请缩小路径或增加筛选条件。"));
        }
    }

    public async IAsyncEnumerable<FileSearchHit> SearchAsync(FileSearchRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Legacy contract allows 1000 hits; fetch bounded pages without retaining all pages.
        var remaining = request.Limit; var offset = 0; string? snapshot = null;
        while (remaining > 0)
        {
            var page = await QueryAsync(new(request.Text, Offset: offset, Limit: Math.Min(remaining, 200), Snapshot: snapshot, IncludeHidden: request.IncludeHidden), cancellationToken);
            foreach (var hit in page.Hits) { cancellationToken.ThrowIfCancellationRequested(); yield return hit; }
            if (!page.HasMore) yield break;
            remaining -= page.Hits.Count; offset += page.Hits.Count; snapshot = page.Snapshot;
        }
    }
}
