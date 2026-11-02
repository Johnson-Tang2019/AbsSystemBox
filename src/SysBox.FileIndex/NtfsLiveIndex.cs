// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;
namespace SysBox.FileIndex;

public sealed record NtfsLiveState(string Root, string VolumeId, string RootFileId, long Generation, ulong JournalId, long Checkpoint, bool Tracking, string State, string? Error);
public sealed class NtfsLiveIndex(IndexDatabase database)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static SqliteCommand Sql(SqliteConnection db, SqliteTransaction? tx, string text, params (string, object?)[] values)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = text;
        foreach (var (key, value) in values)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static string Scope(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
    private static string Root(string value)
    {
        var root = Path.GetFullPath(value);
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            throw new ArgumentException("NTFS 高速索引需要明确的盘符根目录。");
        return char.ToUpperInvariant(root[0]) + root[1..];
    }
    internal static void Schema(SqliteConnection db)
    {
        using var cmd = Sql(db, null, """
            CREATE TABLE IF NOT EXISTS ntfs_live(scope_id TEXT PRIMARY KEY,root TEXT NOT NULL,volume_id TEXT NOT NULL,root_file_id TEXT NOT NULL,
                generation INTEGER NOT NULL,journal TEXT NOT NULL,checkpoint INTEGER NOT NULL,tracking INTEGER NOT NULL,state TEXT NOT NULL,error TEXT);
            CREATE TABLE IF NOT EXISTS ntfs_cache(scope_id TEXT NOT NULL,generation INTEGER NOT NULL,file_id TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(scope_id,generation,file_id));
            CREATE TABLE IF NOT EXISTS ntfs_ids(scope_id TEXT NOT NULL,generation INTEGER NOT NULL,file_id TEXT NOT NULL,parent_id TEXT NOT NULL,name TEXT NOT NULL,attributes INTEGER NOT NULL,PRIMARY KEY(scope_id,generation,file_id));
            """);
        cmd.ExecuteNonQuery();
    }
    public async Task<IndexProgress> BuildAsync(NtfsVolumeBinding binding, INtfsMetadataReader reader, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        binding = binding with
        {
            VolumeId = NtfsIdentity.Volume(binding.VolumeId),
            RootFileId = NtfsIdentity.FileId(binding.RootFileId),
            RootPath = Root(binding.RootPath)
        };
        using var lease = await database.AcquireWriterAsync(token);
        return await Task.Run(() => Build(binding, reader, control, progress, token), token);
    }
    private IndexProgress Build(NtfsVolumeBinding binding, INtfsMetadataReader reader, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        database.Initialize();
        using var db = database.Open();
        Schema(db);
        var scope = Scope(binding.RootPath);
        long generation;
        using (var sequence = Sql(db, null, "SELECT max(value) FROM (SELECT coalesce(max(generation),0) AS value FROM ntfs_cache UNION ALL SELECT coalesce(max(snapshot_revision),0) FROM volumes)"))
            generation = checked(Math.Max(DateTime.UtcNow.Ticks, Convert.ToInt64(sequence.ExecuteScalar()) + 1));
        var started = Stopwatch.GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        try
        {
            var journal = reader.QueryJournal();
            NtfsJournalGuard.Validate(journal, journal.JournalId, journal.NextUsn);
            using (var clean = Sql(db, null, """
                DELETE FROM ntfs_cache WHERE scope_id=$s AND generation<>coalesce((SELECT generation FROM ntfs_live WHERE scope_id=$s),0);
                DELETE FROM ntfs_ids WHERE scope_id=$s AND generation<>coalesce((SELECT generation FROM ntfs_live WHERE scope_id=$s),0);
                """, ("$s", scope)))
                clean.ExecuteNonQuery();
            ulong cursor = 0;
            long count = 0;
            while (true)
            {
                Budget(started, token);
                control.WaitAsync(token).GetAwaiter().GetResult();
                var batch = reader.Enumerate(cursor, long.MaxValue, token);
                if (batch.EndOfEnumeration)
                    break;
                if (batch.Continuation <= cursor)
                    throw Rebuild("MFT 游标没有前进。");
                cursor = batch.Continuation;
                using var tx = db.BeginTransaction();
                foreach (var record in batch.Records)
                {
                    PutId(db, tx, scope, generation, record);
                    if (++count > 1_000_000)
                        throw new SysBoxOperationException(new("NTFS_LIMIT", "达到一百万条初始枚举预算。"));
                }
                tx.Commit();
                progress?.Report(new(binding.RootPath, IndexRunState.Running, count, 0, 0, 0, 0, 0, 0, utc, DateTimeOffset.UtcNow, "正在读取 MFT 身份；大小尚未就绪。"));
            }
            string after = "";
            count = 0;
            while (true)
            {
                Budget(started, token);
                control.WaitAsync(token).GetAwaiter().GetResult();
                var ids = new List<string>();
                using (var query = Sql(db, null, "SELECT file_id FROM ntfs_ids WHERE scope_id=$s AND generation=$g AND file_id>$a ORDER BY file_id LIMIT 16", ("$s", scope), ("$g", generation), ("$a", after)))
                using (var rows = query.ExecuteReader())
                    while (rows.Read())
                        ids.Add(rows.GetString(0));
                if (ids.Count == 0)
                    break;
                after = ids[^1];
                using var tx = db.BeginTransaction();
                Reconcile(db, tx, scope, generation, binding.RootFileId, reader, ids, token);
                tx.Commit();
                count += ids.Count;
                progress?.Report(new(binding.RootPath, IndexRunState.Running, count, 0, 0, 0, 0, 0, 0, utc, DateTimeOffset.UtcNow, "正在补齐大小与硬链接名称。"));
            }
            var checkpoint = journal.NextUsn;
            var target = reader.QueryJournal();
            NtfsJournalGuard.Validate(target, journal.JournalId, checkpoint);
            while (checkpoint < target.NextUsn)
            {
                Budget(started, token);
                var batch = ReadBatch(reader, journal.JournalId, checkpoint, token);
                using var tx = db.BeginTransaction();
                ApplyChanges(db, tx, scope, generation, binding.RootFileId, reader, batch, token);
                tx.Commit();
                checkpoint = checked((long)batch.Continuation);
            }
            NtfsJournalGuard.Validate(reader.QueryJournal(), journal.JournalId, checkpoint);
            using (var tx = db.BeginTransaction())
            {
                var report = NtfsPublisher.Publish(db, tx, scope, generation, binding.RootPath, utc, null, token);
                using var mark = Sql(db, tx, """
                    INSERT INTO ntfs_live VALUES($s,$r,$v,$f,$g,$j,$u,1,'Tracking',NULL)
                    ON CONFLICT(scope_id) DO UPDATE SET root=$r,volume_id=$v,root_file_id=$f,generation=$g,journal=$j,checkpoint=$u,tracking=1,state='Tracking',error=NULL;
                    DELETE FROM ntfs_cache WHERE scope_id=$s AND generation<>$g;DELETE FROM ntfs_ids WHERE scope_id=$s AND generation<>$g;
                    """, ("$s", scope), ("$r", binding.RootPath), ("$v", binding.VolumeId), ("$f", binding.RootFileId), ("$g", generation), ("$j", journal.JournalId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$u", checkpoint));
                mark.ExecuteNonQuery();
                tx.Commit();
                return report;
            }
        }
        catch (Exception error) { Failure(db, scope, error); throw; }
    }
    public async Task<IndexProgress?> CatchUpAsync(string root, INtfsMetadataReader reader, CancellationToken token)
    {
        root = Root(root);
        using var lease = await database.AcquireWriterAsync(token);
        return await Task.Run(() =>
        {
            database.Initialize();
            using var db = database.Open();
            Schema(db);
            var scope = Scope(root);
            var state = States(db).SingleOrDefault(s => s.Root == root) ?? throw Rebuild("高速索引尚未建立。");
            if (!state.Tracking || state.State is "RebuildRequired" or "LimitReached")
                return null;
            try
            {
                var journal = reader.QueryJournal();
                NtfsJournalGuard.Validate(journal, state.JournalId, state.Checkpoint);
                var checkpoint = state.Checkpoint;
                var clock = Stopwatch.GetTimestamp();
                IndexProgress? report = null;
                while (checkpoint < journal.NextUsn)
                {
                    if (Stopwatch.GetElapsedTime(clock) > TimeSpan.FromSeconds(30))
                        break;
                    token.ThrowIfCancellationRequested();
                    var batch = ReadBatch(reader, state.JournalId, checkpoint, token);
                    using var tx = db.BeginTransaction();
                    var changed = ApplyChanges(db, tx, scope, state.Generation, state.RootFileId, reader, batch, token);
                    report = NtfsPublisher.Publish(db, tx, scope, state.Generation, state.Root, DateTimeOffset.UtcNow, changed, token);
                    checkpoint = checked((long)batch.Continuation);
                    using var advance = Sql(db, tx, "UPDATE ntfs_live SET checkpoint=$u,state='Tracking',error=NULL WHERE scope_id=$s", ("$s", scope), ("$u", checkpoint));
                    advance.ExecuteNonQuery();
                    tx.Commit();
                }
                // Retry unreadable identities even when the journal has no new records.
                var retry = new List<string>();
                using (var q = Sql(db, null, "SELECT file_id FROM ntfs_cache WHERE scope_id=$s AND generation=$g AND json_extract(body,'$.state')='Unavailable' LIMIT 16", ("$s", scope), ("$g", state.Generation)))
                using (var rows = q.ExecuteReader())
                    while (rows.Read())
                        retry.Add(rows.GetString(0));
                if (retry.Count > 0)
                {
                    using var tx = db.BeginTransaction();
                    var changed = Reconcile(db, tx, scope, state.Generation, state.RootFileId, reader, retry, token);
                    report = NtfsPublisher.Publish(db, tx, scope, state.Generation, state.Root, DateTimeOffset.UtcNow, changed, token);
                    tx.Commit();
                }
                using (var recover = Sql(db, null, "UPDATE ntfs_live SET state='Tracking',error=NULL WHERE scope_id=$s", ("$s", scope)))
                    recover.ExecuteNonQuery();
                return report;
            }
            catch (Exception error) { Failure(db, scope, error); throw; }
        }, token);
    }
    private static NtfsBatch ReadBatch(INtfsMetadataReader reader, ulong journal, long checkpoint, CancellationToken token)
    {
        NtfsJournalGuard.Validate(reader.QueryJournal(), journal, checkpoint);
        var batch = reader.ReadChanges(checkpoint, journal, token);
        if (batch.Continuation > long.MaxValue || (long)batch.Continuation <= checkpoint || batch.Records.Any(r => r.Usn < checkpoint || r.Usn >= (long)batch.Continuation))
            throw Rebuild("USN 批次或游标无效。");
        NtfsJournalGuard.Validate(reader.QueryJournal(), journal, checked((long)batch.Continuation));
        return batch;
    }
    private static HashSet<string> ApplyChanges(SqliteConnection db, SqliteTransaction tx, string scope, long generation, string root, INtfsMetadataReader reader, NtfsBatch batch, CancellationToken token)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in batch.Records)
        {
            PutId(db, tx, scope, generation, record);
            ids.Add(NtfsIdentity.FileId(record.FileId));
        }
        return Reconcile(db, tx, scope, generation, root, reader, ids, token);
    }
    private static void PutId(SqliteConnection db, SqliteTransaction tx, string scope, long generation, UsnRecord record)
    {
        var id = NtfsIdentity.FileId(record.FileId);
        var parent = NtfsIdentity.FileId(record.ParentId);
        if (record.Name.Length is < 1 or > 255 || record.Name.IndexOfAny(['\\', '/', '\0', ':']) >= 0 || record.Name == "..")
            throw Rebuild("NTFS 名称无效。");
        using var put = Sql(db, tx, "INSERT INTO ntfs_ids VALUES($s,$g,$i,$p,$n,$a) ON CONFLICT(scope_id,generation,file_id) DO UPDATE SET parent_id=$p,name=$n,attributes=$a", ("$s", scope), ("$g", generation), ("$i", id), ("$p", parent), ("$n", record.Name), ("$a", (long)record.Attributes));
        put.ExecuteNonQuery();
    }
    private static HashSet<string> Reconcile(SqliteConnection db, SqliteTransaction tx, string scope, long generation, string root, INtfsMetadataReader reader, IEnumerable<string> requested, CancellationToken token)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ids in requested.Distinct().Chunk(16))
        {
            token.ThrowIfCancellationRequested();
            var objects = reader.ReadObjects(ids, token);
            if (objects.Length != ids.Length || objects.Select(o => o.FileId).Distinct().Count() != ids.Length || objects.Any(o => !ids.Contains(o.FileId)))
                throw Rebuild("元数据响应身份不匹配。");
            foreach (var raw in objects)
            {
                var item = raw with
                {
                    FileId = NtfsIdentity.FileId(raw.FileId),
                    Links = raw.Links.Select(NtfsIdentity.Relative).Distinct(StringComparer.Ordinal).ToArray()
                };
                if (item.State is not ("Ready" or "Missing" or "Unavailable") || item.LogicalBytes < 0 || item.AllocatedBytes < 0 || item.Links.Length > 1024 || item.Links.Sum(p => (long)p.Length) > 65536)
                    throw Rebuild("元数据响应无效。");
                if (item.State == "Ready" && (item.Links.Length == 0 || item.LinkCount == 0 || item.LogicalBytes is null || item.AllocatedBytes is null || ((item.Attributes & 0x10) != 0 ? item.Links.Length != 1 : (item.Attributes & 0x400) == 0 && item.Links.Length != item.LinkCount)))
                    throw Rebuild("就绪元数据缺少完整大小或链接名称。");
                var previous = Cached(db, tx, scope, generation, item.FileId);
                if (item.State == "Unavailable")
                {
                    if (previous is not null)
                        item = previous with
                        {
                            State = "Unavailable",
                            Error = raw.Error
                        };
                    else
                    {
                        var fallback = Fallback(db, tx, scope, generation, item.FileId, root);
                        item = item with
                        {
                            Attributes = fallback.Attributes,
                            Links = fallback.Path is null ? [] : [fallback.Path]
                        };
                    }
                }
                if (item.FileId == root && (item.State != "Ready" || !item.Links.Contains(@"\")))
                    throw Rebuild("卷根目录无法确认。");
                if (previous is not null && (previous.Attributes & 0x10) != 0 && previous.Links.Length == 1)
                {
                    var old = previous.Links[0];
                    var replacement = item.State == "Ready" && (item.Attributes & 0x400) == 0 && item.Links.Length == 1 ? item.Links[0] : null;
                    if (item.State == "Missing" || (item.Attributes & 0x400) != 0 || replacement is not null && replacement != old)
                    {
                        var descendants = new List<NtfsObject>();
                        using (var query = Sql(db, tx, "SELECT body FROM ntfs_cache WHERE scope_id=$s AND generation=$g AND file_id<>$i AND EXISTS(SELECT 1 FROM json_each(body,'$.links') WHERE substr(value,1,length($p))=$p)", ("$s", scope), ("$g", generation), ("$i", item.FileId), ("$p", old.TrimEnd('\\') + @"\")))
                        using (var rows = query.ExecuteReader())
                            while (rows.Read())
                                descendants.Add(JsonSerializer.Deserialize<NtfsObject>(rows.GetString(0), Json)!);
                        foreach (var child in descendants)
                        {
                            var prefix = old.TrimEnd('\\') + @"\";
                            var links = child.Links.SelectMany(p => p.StartsWith(prefix, StringComparison.Ordinal) ? replacement is null ? Array.Empty<string>() : [replacement.TrimEnd('\\') + @"\" + p[prefix.Length..]] : [p]).ToArray();
                            Save(db, tx, scope, generation, child with
                            {
                                Links = links,
                                State = links.Length == 0 ? "Missing" : child.State
                            });
                            changed.Add(child.FileId);
                        }
                    }
                }
                Save(db, tx, scope, generation, item);
                changed.Add(item.FileId);
            }
        }
        foreach (var id in changed)
        {
            using var remove = Sql(db, tx, "DELETE FROM ntfs_ids WHERE scope_id=$s AND generation=$g AND file_id=$i AND EXISTS(SELECT 1 FROM ntfs_cache WHERE scope_id=$s AND generation=$g AND file_id=$i AND json_extract(body,'$.state')='Missing'); DELETE FROM ntfs_cache WHERE scope_id=$s AND generation=$g AND file_id=$i AND json_extract(body,'$.state')='Missing';", ("$s", scope), ("$g", generation), ("$i", id));
            remove.ExecuteNonQuery();
        }
        return changed;
    }
    internal static NtfsObject? Cached(SqliteConnection db, SqliteTransaction tx, string scope, long generation, string id)
    {
        using var q = Sql(db, tx, "SELECT body FROM ntfs_cache WHERE scope_id=$s AND generation=$g AND file_id=$i", ("$s", scope), ("$g", generation), ("$i", id));
        return q.ExecuteScalar() is string text ? JsonSerializer.Deserialize<NtfsObject>(text, Json) : null;
    }
    private static void Save(SqliteConnection db, SqliteTransaction tx, string scope, long generation, NtfsObject item)
    {
        using var put = Sql(db, tx, "INSERT INTO ntfs_cache VALUES($s,$g,$i,$b) ON CONFLICT(scope_id,generation,file_id) DO UPDATE SET body=$b", ("$s", scope), ("$g", generation), ("$i", item.FileId), ("$b", JsonSerializer.Serialize(item, Json)));
        put.ExecuteNonQuery();
    }
    private static (string? Path, uint Attributes) Fallback(SqliteConnection db, SqliteTransaction tx, string scope, long generation, string id, string root)
    {
        var names = new List<string>();
        var visited = new HashSet<string>();
        uint attributes = 0;
        for (var depth = 0; depth < 512; depth++)
        {
            if (id == root)
            {
                names.Reverse();
                return (@"\" + string.Join('\\', names), attributes);
            }
            if (!visited.Add(id))
                return (null, attributes);
            using var q = Sql(db, tx, "SELECT parent_id,name,attributes FROM ntfs_ids WHERE scope_id=$s AND generation=$g AND file_id=$i", ("$s", scope), ("$g", generation), ("$i", id));
            using var row = q.ExecuteReader();
            if (!row.Read())
                return (null, attributes);
            if (depth == 0)
                attributes = checked((uint)row.GetInt64(2));
            names.Add(row.GetString(1));
            id = row.GetString(0);
        }
        return (null, attributes);
    }
    public IReadOnlyList<NtfsLiveState> ReadStates()
    {
        if (!File.Exists(Path.Combine(database.DirectoryPath, "metadata.db")))
            return [];
        using var db = database.OpenReadConnection();
        using (var q = Sql(db, null, "SELECT count(*) FROM sqlite_master WHERE name='ntfs_live'"))
            if (Convert.ToInt64(q.ExecuteScalar()) == 0)
                return [];
        return States(db);
    }
    private static List<NtfsLiveState> States(SqliteConnection db)
    {
        using var q = Sql(db, null, "SELECT root,volume_id,root_file_id,generation,journal,checkpoint,tracking,state,error FROM ntfs_live");
        using var r = q.ExecuteReader();
        var result = new List<NtfsLiveState>();
        while (r.Read())
            result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), ulong.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture), r.GetInt64(5), r.GetBoolean(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return result;
    }
    public async Task SetTrackingAsync(string root, bool enabled, CancellationToken token)
    {
        root = Root(root);
        using var lease = await database.AcquireWriterAsync(token);
        database.Initialize();
        using var db = database.Open();
        Schema(db);
        using var q = Sql(db, null, "UPDATE ntfs_live SET tracking=$e,state=CASE WHEN $e=0 THEN 'Stopped' ELSE state END WHERE root=$r", ("$e", enabled), ("$r", root));
        if (q.ExecuteNonQuery() == 0 && enabled)
            throw Rebuild("请先明确建立该卷索引。");
    }
    public async Task SetFailureAsync(string root, Exception error, CancellationToken token)
    {
        using var lease = await database.AcquireWriterAsync(token);
        database.Initialize();
        using var db = database.Open();
        Schema(db);
        Failure(db, Scope(Root(root)), error);
    }
    private static void Failure(SqliteConnection db, string scope, Exception error)
    {
        var rebuild = error is SysBoxOperationException { Error.Code: "REBUILD_REQUIRED" };
        using var q = Sql(db, null, "UPDATE ntfs_live SET state=$state,error=$error WHERE scope_id=$s", ("$s", scope), ("$state", rebuild ? "RebuildRequired" : error is SysBoxOperationException { Error.Code: "NTFS_LIMIT" } ? "LimitReached" : "Interrupted"), ("$error", error is SysBoxOperationException e ? e.Error.Code : error is OperationCanceledException ? "CANCELLED" : "NTFS_UNAVAILABLE"));
        q.ExecuteNonQuery();
    }
    private static void Budget(long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(30))
            throw new SysBoxOperationException(new("NTFS_LIMIT", "初始索引时间预算已到，旧快照保留。"));
    }
    internal static SysBoxOperationException Rebuild(string text) => new(new("REBUILD_REQUIRED", text));
}
