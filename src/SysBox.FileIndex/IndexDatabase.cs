// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;

namespace SysBox.FileIndex;

// Directory-walk identities are scope/path based, never advertised as physical NTFS IDs.
public sealed class IndexDatabase(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private readonly SemaphoreSlim writer = new(1, 1);

    internal async Task<IDisposable> AcquireWriterAsync(CancellationToken token)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var stream = new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new WriterLease(stream, writer);
        }
        catch { writer.Release(); throw; }
    }

    private sealed class WriterLease(FileStream stream, SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() { stream.Dispose(); semaphore.Release(); }
    }

    // Only trusted data modules use this connection. No SQL is accepted from UI/Agent callers.
    public SqliteConnection OpenReadConnection() => Open(true);
    internal SqliteConnection Open(bool readOnly = false)
    {
        Directory.CreateDirectory(DirectoryPath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "metadata.db"), Pooling = false,
            ForeignKeys = true, DefaultTimeout = 2, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        try
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var current = Convert.ToInt32(version.ExecuteScalar());
            if (current != 1) throw new SysBoxOperationException(new("INDEX_SCHEMA", "索引版本不受支持，请先备份缓存。"));
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal void Initialize()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "metadata.db"), Pooling = false,
            ForeignKeys = true, DefaultTimeout = 2
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1) throw new SysBoxOperationException(new("INDEX_SCHEMA", "索引版本较新，当前程序无法读取。"));
        if (version == 1) return;
        command.CommandText = "PRAGMA journal_mode=WAL";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase))
            throw new IOException("WAL unavailable.");
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE volumes(scope_id TEXT PRIMARY KEY, root TEXT NOT NULL UNIQUE COLLATE BINARY,
                identity_kind TEXT NOT NULL DEFAULT 'directory-path', active_generation INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE file_objects(object_id INTEGER PRIMARY KEY, scope_id TEXT NOT NULL REFERENCES volumes(scope_id),
                generation INTEGER NOT NULL, file_id BLOB, is_directory INTEGER NOT NULL, attributes INTEGER NOT NULL,
                logical_size INTEGER, allocated_size INTEGER, last_write_utc TEXT);
            CREATE TABLE directory_entries(entry_id INTEGER PRIMARY KEY, scope_id TEXT NOT NULL REFERENCES volumes(scope_id),
                generation INTEGER NOT NULL, object_id INTEGER NOT NULL UNIQUE REFERENCES file_objects(object_id),
                parent_entry_id INTEGER REFERENCES directory_entries(entry_id) DEFERRABLE INITIALLY DEFERRED,
                name TEXT NOT NULL, name_folded TEXT NOT NULL, cached_path TEXT NOT NULL COLLATE BINARY,
                is_hidden INTEGER NOT NULL, UNIQUE(scope_id,generation,cached_path));
            CREATE INDEX ix_entries_parent ON directory_entries(parent_entry_id,name_folded);
            CREATE INDEX ix_entries_name ON directory_entries(name_folded);
            CREATE INDEX ix_objects_scope ON file_objects(scope_id,generation);
            CREATE TABLE index_state(scope_id TEXT PRIMARY KEY REFERENCES volumes(scope_id),
                report_json TEXT NOT NULL, published_report_json TEXT);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Task<IReadOnlyList<IndexRoot>> ReadRootsAsync(CancellationToken token) => Task.Run<IReadOnlyList<IndexRoot>>(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(DirectoryPath, "metadata.db"))) return [];
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v.root,v.active_generation,s.report_json FROM volumes v JOIN index_state s USING(scope_id) ORDER BY v.root LIMIT 256";
        using var reader = command.ExecuteReader();
        var roots = new List<IndexRoot>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            roots.Add(new(reader.GetString(0), reader.GetInt64(1), JsonSerializer.Deserialize<IndexProgress>(reader.GetString(2))
                ?? throw new IOException("Invalid index state.")));
        }
        return roots;
    }, token);

    public Task<IReadOnlyList<FileEntry>> ReadEntriesAsync(string root, int limit, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(DirectoryPath, "metadata.db"))) return [];
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e.scope_id,e.cached_path,e.name,o.is_directory,o.logical_size,o.last_write_utc
                FROM directory_entries e JOIN file_objects o USING(object_id)
                JOIN volumes v ON v.scope_id=e.scope_id AND v.active_generation=e.generation
                WHERE v.root=$r ORDER BY e.cached_path COLLATE BINARY LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$r", root); command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var entries = new List<FileEntry>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                entries.Add(new(reader.GetString(0), null, null, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : checked((ulong)reader.GetInt64(4)),
                    reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
            }
            return entries;
        }, token);
    }

    public async Task ClearAsync(CancellationToken token)
    {
        using var lease = await AcquireWriterAsync(token).ConfigureAwait(false);
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            Initialize();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM directory_entries; DELETE FROM file_objects; DELETE FROM index_state; DELETE FROM volumes;";
            command.ExecuteNonQuery();
            transaction.Commit();
            command.Transaction = null;
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
            command.CommandText = "VACUUM";
            command.ExecuteNonQuery();
        }, token).ConfigureAwait(false);
    }
}
