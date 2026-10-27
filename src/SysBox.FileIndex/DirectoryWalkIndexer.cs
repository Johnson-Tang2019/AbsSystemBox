// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;

namespace SysBox.FileIndex;

public sealed class DirectoryWalkIndexer(IndexDatabase database) : IFileIndexService
{
    public Task<IReadOnlyList<IndexRoot>> ReadRootsAsync(CancellationToken token) => database.ReadRootsAsync(token);
    public Task ClearAsync(CancellationToken token) => database.ClearAsync(token);
    public Task<IReadOnlyList<FileEntry>> ReadEntriesAsync(string root, int limit, CancellationToken token) => database.ReadEntriesAsync(root, limit, token);

    public async Task<IndexProgress> ScanAsync(string root, IndexOptions options, IIndexControl control,
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (options.MaxEntries is < 1 or > 10_000_000 || options.MaxDepth is < 1 or > 1024 || options.ExcludedDirectories.Count > 256)
            throw new ArgumentOutOfRangeException(nameof(options));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase))
            throw new SysBoxOperationException(new("INDEX_SCOPE", "请先选择一个文件夹，不直接扫描整卷。"));
        using var lease = await database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Scan(root, options, control, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private IndexProgress Scan(string root, IndexOptions options, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        database.Initialize();
        using var connection = database.Open();
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        var generation = DateTime.UtcNow.Ticks;
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO volumes(scope_id,root) VALUES($scope,$root)";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$root", root);
        command.CommandText = "SELECT count(*) FROM volumes WHERE scope_id<>$scope";
        if ((long)command.ExecuteScalar()! >= 256)
            throw new SysBoxOperationException(new("INDEX_LIMIT", "已达到 256 个索引范围上限，请先清除缓存。"));
        command.CommandText = "INSERT OR IGNORE INTO volumes(scope_id,root) VALUES($scope,$root)";
        command.ExecuteNonQuery();
        // Remove only unpublished staging rows left by an interrupted run, keeping the last good generation.
        command.CommandText = """
            DELETE FROM directory_entries WHERE scope_id=$scope AND generation<>(SELECT active_generation FROM volumes WHERE scope_id=$scope);
            DELETE FROM file_objects WHERE scope_id=$scope AND generation<>(SELECT active_generation FROM volumes WHERE scope_id=$scope);
            """;
        command.ExecuteNonQuery();
        var started = DateTimeOffset.UtcNow;
        var report = new IndexProgress(root, IndexRunState.Running, 0, 0, 0, 0, 0, 0, 0, started, started);
        var exclusions = options.ExcludedDirectories.Select(Path.GetFullPath)
            .Append(database.DirectoryPath).Select(Path.TrimEndingDirectorySeparator).ToArray();
        var stack = new Stack<(DirectoryInfo Directory, long Id, int Depth, IEnumerator<FileSystemInfo> Iterator)>();
        SqliteTransaction? transaction = null;
        var batchCount = 0;
        var clock = Stopwatch.StartNew();
        var lastPublish = TimeSpan.FromSeconds(-1);
        bool IsExcluded(string path) => exclusions.Any(x => string.Equals(path, x, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        void Publish(bool force = false)
        {
            if (!force && clock.Elapsed - lastPublish < TimeSpan.FromMilliseconds(150)) return;
            report = report with { UpdatedUtc = DateTimeOffset.UtcNow };
            lastPublish = clock.Elapsed;
            progress?.Report(report);
        }
        void CommitBatch()
        {
            transaction?.Commit(); transaction?.Dispose(); transaction = null; command.Transaction = null; batchCount = 0;
        }
        long Insert(FileSystemInfo info, long? parent)
        {
            transaction ??= connection.BeginTransaction();
            command.Transaction = transaction;
            command.Parameters.Clear();
            var isDirectory = info is DirectoryInfo;
            long? length = isDirectory ? null : ((FileInfo)info).Length;
            var attributes = info.Attributes;
            var modified = info.LastWriteTimeUtc.ToString("O");
            command.CommandText = """
                INSERT INTO file_objects(scope_id,generation,is_directory,attributes,logical_size,last_write_utc)
                VALUES($s,$g,$d,$a,$l,$w) RETURNING object_id;
                """;
            command.Parameters.AddWithValue("$s", scope); command.Parameters.AddWithValue("$g", generation);
            command.Parameters.AddWithValue("$d", isDirectory); command.Parameters.AddWithValue("$a", (int)attributes);
            command.Parameters.AddWithValue("$l", (object?)length ?? DBNull.Value); command.Parameters.AddWithValue("$w", modified);
            var objectId = (long)command.ExecuteScalar()!;
            command.CommandText = """
                INSERT INTO directory_entries(scope_id,generation,object_id,parent_entry_id,name,name_folded,cached_path,is_hidden)
                VALUES($s,$g,$o,$p,$n,$f,$path,$h) RETURNING entry_id;
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$s", scope); command.Parameters.AddWithValue("$g", generation);
            command.Parameters.AddWithValue("$o", objectId); command.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
            command.Parameters.AddWithValue("$n", info.Name); command.Parameters.AddWithValue("$f", info.Name.ToUpperInvariant());
            command.Parameters.AddWithValue("$path", info.FullName); command.Parameters.AddWithValue("$h", attributes.HasFlag(FileAttributes.Hidden));
            var id = (long)command.ExecuteScalar()!;
            report = isDirectory ? report with { Directories = report.Directories + 1 } :
                report with { Files = report.Files + 1, LogicalBytes = checked(report.LogicalBytes + length!.Value) };
            if (++batchCount >= 256) CommitBatch();
            return id;
        }
        IEnumerator<FileSystemInfo> Enumerate(DirectoryInfo directory) => directory.EnumerateFileSystemInfos("*", new EnumerationOptions
        { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).GetEnumerator();
        void ReadError(Exception error)
        {
            report = error is UnauthorizedAccessException ? report with { AccessDenied = report.AccessDenied + 1 } : report with { IoErrors = report.IoErrors + 1 };
        }
        try
        {
            token.ThrowIfCancellationRequested();
            // Reject reparse ancestors as well as children; no junction is intentionally traversed.
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse root.");
            if (IsExcluded(root)) throw new IOException("Excluded root.");
            var directory = new DirectoryInfo(root);
            var rootId = Insert(directory, null);
            stack.Push((directory, rootId, 0, Enumerate(directory)));
            Publish(true);
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (control.IsPaused)
                {
                    CommitBatch(); report = report with { State = IndexRunState.Paused }; Publish(true);
                    control.WaitAsync(token).GetAwaiter().GetResult();
                    report = report with { State = IndexRunState.Running }; Publish(true);
                }
                if (report.Files + report.Directories >= options.MaxEntries)
                { report = report with { State = IndexRunState.LimitReached }; break; }
                var frame = stack.Peek();
                FileSystemInfo info;
                try
                {
                    if (frame.Directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    { report = report with { ReparsePoints = report.ReparsePoints + 1 }; stack.Pop().Iterator.Dispose(); continue; }
                    if (!frame.Iterator.MoveNext()) { stack.Pop().Iterator.Dispose(); continue; }
                    info = frame.Iterator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { ReadError(ex); stack.Pop().Iterator.Dispose(); continue; }
                try
                {
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) report = report with { ReparsePoints = report.ReparsePoints + 1 };
                    else if (IsExcluded(info.FullName)) report = report with { Excluded = report.Excluded + 1 };
                    else
                    {
                        var id = Insert(info, frame.Id);
                        if (info is DirectoryInfo child)
                        {
                            if (frame.Depth + 1 >= options.MaxDepth) report = report with { IoErrors = report.IoErrors + 1 };
                            else stack.Push((child, id, frame.Depth + 1, Enumerate(child)));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReadError(ex); }
                Publish();
            }
            if (report.State == IndexRunState.Running)
                report = report with { State = report.AccessDenied + report.IoErrors + report.ReparsePoints + report.Excluded > 0 ? IndexRunState.Partial : IndexRunState.Complete };
            token.ThrowIfCancellationRequested();
            CommitBatch();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { transaction?.Rollback(); report = report with { State = IndexRunState.Cancelled }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or OverflowException)
        { transaction?.Rollback(); report = report with { State = IndexRunState.Failed, Error = "目录不可读、已断开或索引写入失败；原有可用索引保留。" }; }
        finally
        {
            transaction?.Dispose(); command.Transaction = null;
            while (stack.TryPop(out var frame)) frame.Iterator.Dispose();
        }
        report = report with { UpdatedUtc = DateTimeOffset.UtcNow };
        using var final = connection.BeginTransaction();
        command.Transaction = final; command.Parameters.Clear();
        command.Parameters.AddWithValue("$s", scope); command.Parameters.AddWithValue("$g", generation);
        command.Parameters.AddWithValue("$r", JsonSerializer.Serialize(report));
        var publish = report.State is IndexRunState.Complete or IndexRunState.Partial;
        command.CommandText = "INSERT INTO index_state(scope_id,report_json) VALUES($s,$r) ON CONFLICT(scope_id) DO UPDATE SET report_json=$r";
        command.ExecuteNonQuery();
        if (publish)
        {
            command.CommandText = "UPDATE volumes SET active_generation=$g WHERE scope_id=$s; UPDATE index_state SET published_report_json=$r WHERE scope_id=$s";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            DELETE FROM directory_entries WHERE scope_id=$s AND generation<>(SELECT active_generation FROM volumes WHERE scope_id=$s);
            DELETE FROM file_objects WHERE scope_id=$s AND generation<>(SELECT active_generation FROM volumes WHERE scope_id=$s);
            """;
        command.ExecuteNonQuery(); final.Commit(); Publish(true);
        return report;
    }
}
