// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;
using SysBox.FileIndex;

namespace SysBox.Storage;

public sealed class IndexedStorageBrowser(IndexDatabase database, IFileIndexService indexer) : IStorageBrowser, IStorageAnalyzer
{
    public Task<IndexProgress> AnalyzeAsync(string path, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token)
        => indexer.ScanAsync(path, new(), control, progress, token);

    public Task<StorageView> ReadAsync(string path, StorageSort sort, int offset, int limit, CancellationToken token,
        string? scopeRoot = null, long? expectedGeneration = null)
    {
        if (offset is < 0 or > 100_000 || limit is < 1 or > 200 || !Enum.IsDefined(sort)) throw Invalid("目录分页参数无效。");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return Task.Run(() => Read(path, sort, offset, limit, token, scopeRoot, expectedGeneration), token);
    }
    private StorageView Read(string path, StorageSort sort, int offset, int limit, CancellationToken token, string? selectedScope, long? expectedGeneration)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(database.DirectoryPath, "metadata.db"))) throw NotReady();
        var clock = Stopwatch.StartNew();
        try
        {
            using var connection = database.OpenReadConnection();
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ => token.IsCancellationRequested || clock.Elapsed > TimeSpan.FromSeconds(3) ? 1 : 0, null);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version";
            if ((long)command.ExecuteScalar()! < 2) throw NotReady();
            command.CommandText = """
                SELECT v.scope_id,v.root,v.active_generation,s.published_report_json
                FROM volumes v JOIN index_state s USING(scope_id) WHERE v.active_generation>0;
                """;
            string? scope = null, scopeRoot = null; long generation = 0; IndexProgress? report = null;
            using (var roots = command.ExecuteReader())
            {
                while (roots.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var candidate = roots.GetString(1);
                    if (selectedScope is not null && candidate != selectedScope) continue;
                    if ((path.Equals(candidate, StringComparison.Ordinal) || path.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.Ordinal)) &&
                        (scopeRoot is null || candidate.Length > scopeRoot.Length))
                    {
                        scope = roots.GetString(0); scopeRoot = candidate; generation = roots.GetInt64(2);
                        report = JsonSerializer.Deserialize<IndexProgress>(roots.GetString(3));
                    }
                }
            }
            if (scope is null || report is null) throw NotReady();
            if (expectedGeneration.HasValue && generation != expectedGeneration.Value)
                throw new SysBoxOperationException(new("STORAGE_CHANGED", "目录快照已更新，请点击读取已有快照后重新浏览。"));
            var completeness = report.State == IndexRunState.Complete ? DataCompleteness.Complete : DataCompleteness.Partial;
            const string projection = """
                SELECT e.entry_id,e.cached_path,e.name,o.is_directory,
                  CASE WHEN o.is_directory=1 THEN t.logical_bytes ELSE o.logical_size END,
                  CASE WHEN o.is_directory=1 THEN t.allocated_bytes ELSE coalesce(o.allocated_size,0) END,
                  CASE WHEN o.is_directory=1 THEN t.unknown_allocated ELSE (o.allocated_size IS NULL) END,
                  CASE WHEN o.is_directory=1 THEN t.files ELSE 1 END,
                  CASE WHEN o.is_directory=1 THEN t.folders ELSE 0 END,
                  CASE WHEN o.is_directory=1 THEN t.hardlinked_entries ELSE (coalesce(o.link_count,0)>1) END
                FROM directory_entries e JOIN file_objects o USING(object_id)
                LEFT JOIN folder_totals t USING(entry_id)
                """;
            command.CommandText = projection + " WHERE e.scope_id=$s AND e.generation=$g AND e.cached_path=$path";
            command.Parameters.AddWithValue("$s", scope); command.Parameters.AddWithValue("$g", generation); command.Parameters.AddWithValue("$path", path);
            StorageItem summary; long parentId;
            StorageItem ReadItem(SqliteDataReader row)
            {
                if (row.IsDBNull(4)) throw NotReady();
                return new(row.GetString(1), row.GetString(2), row.GetBoolean(3), row.GetInt64(4), row.GetInt64(5), row.GetInt64(6),
                    row.GetInt64(7), row.GetInt64(8), row.GetInt64(9), completeness, report.UpdatedUtc);
            }
            using (var parent = command.ExecuteReader())
            {
                if (!parent.Read()) throw NotReady();
                summary = ReadItem(parent); parentId = parent.GetInt64(0);
                if (!summary.IsDirectory) throw Invalid("请选择文件夹进行空间分析。");
            }
            var order = sort == StorageSort.FileCount ? "8 DESC,5 DESC" : "5 DESC,8 DESC";
            command.CommandText = projection + $" WHERE e.parent_entry_id=$parent AND e.scope_id=$s AND e.generation=$g ORDER BY {order},e.name COLLATE BINARY LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$parent", parentId); command.Parameters.AddWithValue("$limit", limit + 1); command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader(); var children = new List<StorageItem>();
            while (reader.Read()) { token.ThrowIfCancellationRequested(); children.Add(ReadItem(reader)); }
            var more = children.Count > limit; if (more) children.RemoveAt(children.Count - 1);
            var notice = "按目录项累计；硬链接可能重复计数。已知分配量不等同于磁盘实际占用。";
            if (completeness == DataCompleteness.Partial) notice += $" 部分覆盖：权限拒绝 {report.AccessDenied}，读取错误 {report.IoErrors}，跳过链接 {report.ReparsePoints}，排除 {report.Excluded}。";
            if (summary.UnknownAllocatedFiles > 0) notice += $" {summary.UnknownAllocatedFiles} 个文件的分配量未知；请重新分析以补齐可读元数据。";
            return new(summary, children.AsReadOnly(), more, generation, scope, scopeRoot!,
                report.AccessDenied + report.IoErrors + report.ReparsePoints + report.Excluded, notice);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9)
        {
            token.ThrowIfCancellationRequested(); throw new SysBoxOperationException(new("STORAGE_BUDGET", "目录查询超时，请缩小范围后重试。"));
        }
    }
    async Task<Result<FolderSummary>> IStorageAnalyzer.AnalyzeAsync(string path, CancellationToken token)
    {
        // The legacy port reads the shared snapshot. New scans require the explicit browser operation.
        try
        {
            var view = await ReadAsync(path, StorageSort.LogicalBytes, 0, 1, token).ConfigureAwait(false);
            var summary = view.Summary;
            return Result<FolderSummary>.Success(new(view.ScopeId, null, summary.Path, checked((ulong)summary.LogicalBytes),
                summary.FileCount, summary.FolderCount, view.ScopeSkippedEntries, summary.Completeness, summary.TimestampUtc));
        }
        catch (SysBoxOperationException ex) { return Result<FolderSummary>.Failure(ex.Error); }
    }
    private static SysBoxOperationException NotReady() => new(new("STORAGE_NOT_READY", "该目录尚无大小快照，请先选择目录并分析或重新扫描。"));
    private static SysBoxOperationException Invalid(string message) => new(new("INVALID_ARGUMENT", message));
}
