// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Data.Sqlite;

namespace SysBox.FileIndex;

internal static class FolderTotalsBuilder
{
    public static void Build(SqliteConnection connection, SqliteTransaction transaction, string scope, long generation, CancellationToken token)
    {
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
        try { BuildCore(connection, transaction, scope, generation, token); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9) { token.ThrowIfCancellationRequested(); throw; }
        finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null!, null); }
    }
    private static void BuildCore(SqliteConnection connection, SqliteTransaction transaction, string scope, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Entry IDs are inserted parent-first. Descending IDs give an iterative post-order without loading the tree.
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO folder_totals(entry_id,logical_bytes,allocated_bytes,unknown_allocated,files,folders,hardlinked_entries)
            SELECT e.entry_id,0,0,0,0,0,0 FROM directory_entries e JOIN file_objects o USING(object_id)
            WHERE e.scope_id=$s AND e.generation=$g AND o.is_directory=1;
            """;
        insert.Parameters.AddWithValue("$s", scope); insert.Parameters.AddWithValue("$g", generation); insert.ExecuteNonQuery();
        using var entries = connection.CreateCommand(); entries.Transaction = transaction;
        entries.CommandText = """
            SELECT e.entry_id,e.parent_entry_id,o.is_directory,o.logical_size,o.allocated_size,o.link_count
            FROM directory_entries e JOIN file_objects o USING(object_id)
            WHERE e.scope_id=$s AND e.generation=$g ORDER BY e.entry_id DESC;
            """;
        entries.Parameters.AddWithValue("$s", scope); entries.Parameters.AddWithValue("$g", generation);
        using var reader = entries.ExecuteReader();
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(1)) continue;
            update.Parameters.Clear(); update.Parameters.AddWithValue("$parent", reader.GetInt64(1));
            if (reader.GetBoolean(2))
            {
                update.CommandText = """
                    UPDATE folder_totals SET
                      logical_bytes=logical_bytes+(SELECT logical_bytes FROM folder_totals WHERE entry_id=$child),
                      allocated_bytes=allocated_bytes+(SELECT allocated_bytes FROM folder_totals WHERE entry_id=$child),
                      unknown_allocated=unknown_allocated+(SELECT unknown_allocated FROM folder_totals WHERE entry_id=$child),
                      files=files+(SELECT files FROM folder_totals WHERE entry_id=$child),
                      folders=folders+1+(SELECT folders FROM folder_totals WHERE entry_id=$child),
                      hardlinked_entries=hardlinked_entries+(SELECT hardlinked_entries FROM folder_totals WHERE entry_id=$child)
                    WHERE entry_id=$parent;
                    """;
                update.Parameters.AddWithValue("$child", reader.GetInt64(0));
            }
            else
            {
                update.CommandText = """
                    UPDATE folder_totals SET logical_bytes=logical_bytes+$logical,allocated_bytes=allocated_bytes+$allocated,
                      unknown_allocated=unknown_allocated+$unknown,files=files+1,hardlinked_entries=hardlinked_entries+$linked
                    WHERE entry_id=$parent;
                    """;
                update.Parameters.AddWithValue("$logical", reader.GetInt64(3));
                update.Parameters.AddWithValue("$allocated", reader.IsDBNull(4) ? 0 : reader.GetInt64(4));
                update.Parameters.AddWithValue("$unknown", reader.IsDBNull(4) ? 1 : 0);
                update.Parameters.AddWithValue("$linked", !reader.IsDBNull(5) && reader.GetInt64(5) > 1 ? 1 : 0);
            }
            update.ExecuteNonQuery();
        }
        // SQLite promotes overflowing INTEGER arithmetic to REAL: refuse to publish such totals.
        insert.CommandText = """
            SELECT count(*) FROM folder_totals t JOIN directory_entries e USING(entry_id)
            WHERE e.scope_id=$s AND e.generation=$g AND
              (typeof(t.logical_bytes)<>'integer' OR typeof(t.allocated_bytes)<>'integer');
            """;
        if ((long)insert.ExecuteScalar()! != 0) throw new OverflowException("Folder totals exceed Int64 range.");
    }
}
