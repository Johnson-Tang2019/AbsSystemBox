// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;

namespace SysBox.FileIndex;

public sealed record NtfsSyncSnapshot(long Generation, ulong JournalId, long Checkpoint, string State, string? Error, IReadOnlyList<UsnRecord> Entries, bool Truncated);

// Transactional metadata staging only. It is deliberately not registered as a production volume scanner.
// MFT enumeration does not enumerate every hard-link name; a synchronized snapshot remains partial.
public sealed class NtfsSyncEngine(IndexDatabase database)
{
    private const int MaximumNodes = 1_000_000, MaximumBatches = 10000;
    private static void Identity(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid volume identity.");
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Initialize(SqliteConnection connection)
    {
        using var command = Command(connection, null, """
            CREATE TABLE IF NOT EXISTS ntfs_sync(volume_id TEXT PRIMARY KEY,active_generation INTEGER NOT NULL DEFAULT 0,
                journal TEXT NOT NULL DEFAULT '0',checkpoint INTEGER NOT NULL DEFAULT 0,pending_generation INTEGER NOT NULL DEFAULT 0,
                pending_journal TEXT,pending_checkpoint INTEGER,status TEXT NOT NULL,error TEXT);
            CREATE TABLE IF NOT EXISTS ntfs_nodes(volume_id TEXT NOT NULL,generation INTEGER NOT NULL,file_id TEXT NOT NULL,
                parent_id TEXT NOT NULL,name TEXT NOT NULL,attributes INTEGER NOT NULL,last_usn INTEGER NOT NULL,version INTEGER NOT NULL,
                rename_pending INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(volume_id,generation,file_id));
            """);
        command.ExecuteNonQuery();
    }
    public async Task<NtfsSyncSnapshot> BuildAsync(string volumeId, INtfsVolumeReader reader, CancellationToken token)
    {
        Identity(volumeId);
        using var lease = await database.AcquireWriterAsync(token).ConfigureAwait(false);
        return await Task.Run(() => Build(volumeId, reader, token), token).ConfigureAwait(false);
    }
    private NtfsSyncSnapshot Build(string id, INtfsVolumeReader reader, CancellationToken token)
    {
        database.Initialize();
        using var connection = database.Open();
        Initialize(connection);
        try
        {
            token.ThrowIfCancellationRequested();
            var journal = reader.QueryJournal();
            NtfsJournalGuard.Validate(journal, journal.JournalId, journal.NextUsn);
            using (var create = Command(connection, null, "INSERT INTO ntfs_sync(volume_id,status) VALUES($v,'Enumerating') ON CONFLICT(volume_id) DO NOTHING", ("$v", id)))
                create.ExecuteNonQuery();
            long generation;
            using (var read = Command(connection, null, "SELECT MAX(active_generation,pending_generation)+1 FROM ntfs_sync WHERE volume_id=$v", ("$v", id)))
                generation = checked(Convert.ToInt64(read.ExecuteScalar()));
            using (var transaction = connection.BeginTransaction())
            {
                using var reset = Command(connection, transaction, """
                    DELETE FROM ntfs_nodes WHERE volume_id=$v AND generation!=(SELECT active_generation FROM ntfs_sync WHERE volume_id=$v);
                    UPDATE ntfs_sync SET pending_generation=$g,pending_journal=$j,pending_checkpoint=$u,status='Enumerating',error=NULL WHERE volume_id=$v;
                    """, ("$v", id), ("$g", generation), ("$j", journal.JournalId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$u", journal.NextUsn));
                reset.ExecuteNonQuery();
                transaction.Commit();
            }
            var started = Stopwatch.GetTimestamp();
            ulong continuation = 0;
            var batches = 0;
            while (true)
            {
                Budget(started, ++batches, token);
                var batch = reader.Enumerate(continuation, journal.NextUsn, token);
                if (batch.EndOfEnumeration)
                    break;
                if (batch.Continuation <= continuation)
                    throw Invalid("MFT 枚举游标没有前进。");
                Apply(connection, id, generation, batch.Records, journal.NextUsn, true, true, token);
                continuation = batch.Continuation;
            }
            var current = reader.QueryJournal();
            NtfsJournalGuard.Validate(current, journal.JournalId, journal.NextUsn);
            var checkpoint = Replay(connection, id, generation, reader, journal.JournalId, journal.NextUsn, current.NextUsn, true, started, token);
            using (var transaction = connection.BeginTransaction())
            {
                using var publish = Command(connection, transaction, """
                    UPDATE ntfs_sync SET active_generation=$g,journal=pending_journal,checkpoint=$u,status='SynchronizedPartial',error=NULL WHERE volume_id=$v;
                    DELETE FROM ntfs_nodes WHERE volume_id=$v AND generation!=$g;
                    """, ("$v", id), ("$g", generation), ("$u", checkpoint));
                publish.ExecuteNonQuery();
                transaction.Commit();
            }
            return Read(connection, id, 1000);
        }
        catch (Exception error) { MarkFailure(connection, id, error); throw; }
    }
    public async Task<NtfsSyncSnapshot> CatchUpAsync(string volumeId, INtfsVolumeReader reader, CancellationToken token)
    {
        Identity(volumeId);
        using var lease = await database.AcquireWriterAsync(token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            database.Initialize();
            using var connection = database.Open();
            Initialize(connection);
            try
            {
                var state = Read(connection, volumeId, 1);
                if (state.Generation == 0 || state.State == "RebuildRequired")
                    throw Invalid("尚无可恢复的 NTFS 快照，需要重新建立索引。");
                var journal = reader.QueryJournal();
                NtfsJournalGuard.Validate(journal, state.JournalId, state.Checkpoint);
                Replay(connection, volumeId, state.Generation, reader, state.JournalId, state.Checkpoint, journal.NextUsn, false, Stopwatch.GetTimestamp(), token);
                return Read(connection, volumeId, 1000);
            }
            catch (Exception error) { MarkFailure(connection, volumeId, error); throw; }
        }, token).ConfigureAwait(false);
    }
    private static long Replay(SqliteConnection connection, string id, long generation, INtfsVolumeReader reader, ulong journalId, long checkpoint, long target, bool pending, long started, CancellationToken token)
    {
        var batches = 0;
        while (checkpoint < target)
        {
            Budget(started, ++batches, token);
            NtfsJournalGuard.Validate(reader.QueryJournal(), journalId, checkpoint);
            var batch = reader.ReadChanges(checkpoint, journalId, token);
            if (batch.EndOfEnumeration || batch.Continuation > long.MaxValue || (long)batch.Continuation <= checkpoint)
                throw Invalid("USN 读取无法前进，需要重建。");
            var next = checked((long)batch.Continuation);
            NtfsJournalGuard.Validate(reader.QueryJournal(), journalId, next);
            if (batch.Records.Any(r => r.Usn < checkpoint || r.Usn >= next))
                throw Invalid("USN 记录不在当前批次范围内。");
            Apply(connection, id, generation, batch.Records, next, pending, false, token);
            checkpoint = next;
        }
        return checkpoint;
    }
    private static void Apply(SqliteConnection connection, string id, long generation, IReadOnlyList<UsnRecord> records, long checkpoint, bool pending, bool baseline, CancellationToken token)
    {
        if (records.Count > 16384)
            throw Invalid("NTFS 批次过大。");
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (record.Version is not (2 or 3) || record.FileId.Length != (record.Version == 2 ? 16 : 32) || record.ParentId.Length != record.FileId.Length ||
                !record.FileId.All(char.IsAsciiHexDigit) || !record.ParentId.All(char.IsAsciiHexDigit) || record.Usn < 0 ||
                record.Name.Length is < 1 or > 255 || record.Name.IndexOfAny(['\0', '/', '\\']) >= 0 || record.Name == ".." ||
                record.Name == "." && (record.FileId != record.ParentId || (record.Attributes & 0x10) == 0))
                throw Invalid("NTFS 记录无效。");
            if (!baseline && (record.Reason & 0x10000) != 0)
                throw Invalid("硬链接集合发生变化，需要重新协调链接后重建。");
            if (!baseline && (record.Reason & 0x200) != 0)
            {
                using var remove = Command(connection, transaction, "DELETE FROM ntfs_nodes WHERE volume_id=$v AND generation=$g AND file_id=$f", ("$v", id), ("$g", generation), ("$f", record.FileId));
                remove.ExecuteNonQuery();
                continue;
            }
            if (!baseline && (record.Reason & 0x1000) != 0)
            {
                using var rename = Command(connection, transaction, "UPDATE ntfs_nodes SET rename_pending=1 WHERE volume_id=$v AND generation=$g AND file_id=$f", ("$v", id), ("$g", generation), ("$f", record.FileId));
                rename.ExecuteNonQuery();
                continue;
            }
            using var put = Command(connection, transaction, """
                INSERT INTO ntfs_nodes(volume_id,generation,file_id,parent_id,name,attributes,last_usn,version,rename_pending)
                VALUES($v,$g,$f,$p,$n,$a,$u,$r,0)
                ON CONFLICT(volume_id,generation,file_id) DO UPDATE SET parent_id=excluded.parent_id,name=excluded.name,
                    attributes=excluded.attributes,last_usn=excluded.last_usn,version=excluded.version,rename_pending=0
                WHERE excluded.last_usn>=ntfs_nodes.last_usn;
                """, ("$v", id), ("$g", generation), ("$f", record.FileId), ("$p", record.ParentId), ("$n", record.Name), ("$a", (long)record.Attributes), ("$u", record.Usn), ("$r", record.Version));
            put.ExecuteNonQuery();
        }
        using (var count = Command(connection, transaction, "SELECT COUNT(*) FROM ntfs_nodes WHERE volume_id=$v AND generation=$g", ("$v", id), ("$g", generation)))
            if (Convert.ToInt64(count.ExecuteScalar()) > MaximumNodes)
                throw new SysBoxOperationException(new("NTFS_LIMIT", "NTFS 实验索引达到 100 万项上限。"));
        using var advance = Command(connection, transaction, pending ? "UPDATE ntfs_sync SET pending_checkpoint=$u,status='CatchingUp' WHERE volume_id=$v" : "UPDATE ntfs_sync SET checkpoint=$u,status='SynchronizedPartial',error=NULL WHERE volume_id=$v", ("$v", id), ("$u", checkpoint));
        advance.ExecuteNonQuery();
        transaction.Commit();
    }
    private static void Budget(long started, int batches, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (batches > MaximumBatches || Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(2))
            throw new SysBoxOperationException(new("NTFS_LIMIT", "NTFS 实验批次或时间预算已用尽，可取消后重试。"));
    }
    private static SysBoxOperationException Invalid(string message) => new(new("REBUILD_REQUIRED", message));
    private static void MarkFailure(SqliteConnection connection, string id, Exception error)
    {
        using var command = Command(connection, null, "UPDATE ntfs_sync SET status=$s,error=$e WHERE volume_id=$v", ("$v", id),
            ("$s", error is SysBoxOperationException { Error.Code: "REBUILD_REQUIRED" } ? "RebuildRequired" : error is OperationCanceledException ? "Cancelled" : "Failed"),
            ("$e", error is SysBoxOperationException operation ? operation.Error.Code : error is OperationCanceledException ? "CANCELLED" : "NTFS_FAILED"));
        command.ExecuteNonQuery();
    }
    public NtfsSyncSnapshot ReadSnapshot(string volumeId, int limit = 1000)
    {
        Identity(volumeId);
        if (limit is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            using var connection = database.OpenReadConnection();
            return Read(connection, volumeId, limit);
        }
        catch (SqliteException) { throw new SysBoxOperationException(new("NTFS_STATE_UNAVAILABLE", "NTFS 实验快照不可用，请重新建立隔离实验索引。")); }
    }
    private static NtfsSyncSnapshot Read(SqliteConnection connection, string id, int limit)
    {
        using var transaction = connection.BeginTransaction();
        using var state = Command(connection, transaction, "SELECT active_generation,journal,checkpoint,status,error FROM ntfs_sync WHERE volume_id=$v", ("$v", id));
        using var row = state.ExecuteReader();
        if (!row.Read())
            throw Invalid("尚未建立 NTFS 快照。");
        var generation = row.GetInt64(0);
        var journal = ulong.Parse(row.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
        var checkpoint = row.GetInt64(2);
        var status = row.GetString(3);
        var error = row.IsDBNull(4) ? null : row.GetString(4);
        row.Close();
        using var entries = Command(connection, transaction, "SELECT file_id,parent_id,last_usn,attributes,name,version FROM ntfs_nodes WHERE volume_id=$v AND generation=$g ORDER BY file_id LIMIT $l", ("$v", id), ("$g", generation), ("$l", limit + 1));
        using var values = entries.ExecuteReader();
        var result = new List<UsnRecord>();
        while (values.Read())
            result.Add(new(values.GetString(0), values.GetString(1), values.GetInt64(2), 0, checked((uint)values.GetInt64(3)), values.GetString(4), checked((ushort)values.GetInt32(5))));
        var truncated = result.Count > limit;
        if (truncated)
            result.RemoveAt(result.Count - 1);
        return new(generation, journal, checkpoint, status, error, result.AsReadOnly(), truncated);
    }
}
