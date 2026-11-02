// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SysBox.Abstractions;
using static SysBox.FileIndex.NtfsLiveIndex;
namespace SysBox.FileIndex;

internal static class NtfsPublisher
{
    private sealed record Existing(long Entry, long Object, string FileId, string Path, long? Parent, bool Directory);
    public static IndexProgress Publish(SqliteConnection db, SqliteTransaction tx, string scope, long generation, string root, DateTimeOffset started, HashSet<string>? changed, CancellationToken token)
    {
        using (var scopeRow = Sql(db, tx, "INSERT INTO volumes(scope_id,root,identity_kind) VALUES($s,$r,'ntfs') ON CONFLICT(scope_id) DO UPDATE SET identity_kind='ntfs'", ("$s", scope), ("$r", root)))
            scopeRow.ExecuteNonQuery();
        using (var stage = Sql(db, tx, """
            DROP TABLE IF EXISTS temp.ntfs_changed;DROP TABLE IF EXISTS temp.ntfs_desired;
            CREATE TEMP TABLE ntfs_changed(file_id TEXT PRIMARY KEY);
            CREATE TEMP TABLE ntfs_desired(file_id TEXT NOT NULL,path TEXT PRIMARY KEY COLLATE BINARY,body TEXT NOT NULL);
            """))
            stage.ExecuteNonQuery();
        if (changed is not null)
            foreach (var id in changed)
            {
                using var add = Sql(db, tx, "INSERT INTO ntfs_changed VALUES($i)", ("$i", id));
                add.ExecuteNonQuery();
            }
        using (var flatten = Sql(db, tx, """
            INSERT INTO ntfs_desired SELECT c.file_id,j.value,c.body FROM ntfs_cache c,json_each(c.body,'$.links') j
            WHERE c.scope_id=$s AND c.generation=$g AND json_extract(c.body,'$.state')<>'Missing'
            AND (json_extract(c.body,'$.attributes')&1024)=0
            """ + (changed is null ? "" : " AND c.file_id IN(SELECT file_id FROM ntfs_changed)"), ("$s", scope), ("$g", generation)))
            flatten.ExecuteNonQuery();
        using (var count = Sql(db, tx, "SELECT count(*) FROM ntfs_desired"))
            if (Convert.ToInt64(count.ExecuteScalar()) > 1_000_000)
                throw new SysBoxOperationException(new("NTFS_LIMIT", "硬链接目录项数量超过发布预算。"));
        var old = new List<Existing>();
        if (changed is not null)
        {
            using var q = Sql(db, tx, "SELECT e.entry_id,e.object_id,hex(o.file_id),e.cached_path,e.parent_entry_id,o.is_directory FROM directory_entries e JOIN file_objects o USING(object_id) WHERE e.scope_id=$s AND e.generation=$g AND hex(o.file_id) IN(SELECT file_id FROM ntfs_changed)", ("$s", scope), ("$g", generation));
            using var rows = q.ExecuteReader();
            while (rows.Read())
                old.Add(new(rows.GetInt64(0), rows.GetInt64(1), rows.GetString(2), rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetInt64(4), rows.GetBoolean(5)));
        }
        var affected = new HashSet<long>();
        foreach (var row in old)
        {
            Ancestors(db, tx, row.Parent, affected);
            if (row.Directory)
                affected.Add(row.Entry);
        }
        foreach (var row in old)
        {
            using var vacate = Sql(db, tx, "UPDATE directory_entries SET cached_path=$p WHERE entry_id=$e", ("$p", "@NTFS-STAGING-" + row.Entry), ("$e", row.Entry));
            vacate.ExecuteNonQuery();
        }
        var byId = old.GroupBy(e => e.FileId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var retained = new HashSet<long>();
        using (var query = Sql(db, tx, "SELECT file_id,path,body FROM ntfs_desired ORDER BY length(path),path COLLATE BINARY"))
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var id = rows.GetString(0);
                var relative = NtfsIdentity.Relative(rows.GetString(1));
                var item = JsonSerializer.Deserialize<NtfsObject>(rows.GetString(2), Json)!;
                var directory = (item.Attributes & 0x10) != 0;
                var full = root.TrimEnd('\\') + relative;
                long? parent = null;
                if (relative != @"\")
                {
                    var parentPath = Path.GetDirectoryName(full) ?? throw Rebuild("目录父路径缺失。");
                    using var lookup = Sql(db, tx, "SELECT e.entry_id FROM directory_entries e JOIN file_objects o USING(object_id) WHERE e.scope_id=$s AND e.generation=$g AND e.cached_path=$p AND o.is_directory=1", ("$s", scope), ("$g", generation), ("$p", parentPath));
                    if (lookup.ExecuteScalar() is not long p)
                        throw Rebuild("父目录尚未协调，旧快照已保留。");
                    parent = p;
                }
                var name = relative == @"\" ? root : Path.GetFileName(relative);
                Existing? existing = null;
                if (byId.TryGetValue(id, out var candidates))
                    existing = candidates.FirstOrDefault(e => !retained.Contains(e.Entry) && e.Path == full) ?? candidates.FirstOrDefault(e => !retained.Contains(e.Entry));
                long objectId, entryId;
                if (existing is null)
                {
                    using var insert = Sql(db, tx, "INSERT INTO file_objects(scope_id,generation,file_id,is_directory,attributes,logical_size,allocated_size,last_write_utc,size_state,size_updated_utc,link_count) VALUES($s,$g,$id,$d,$a,$l,$allocated,$w,$state,$now,$links) RETURNING object_id", ("$s", scope), ("$g", generation), ("$id", Convert.FromHexString(id)), ("$d", directory), ("$a", (long)item.Attributes), ("$l", directory ? 0 : item.LogicalBytes ?? 0), ("$allocated", directory ? 0 : item.AllocatedBytes), ("$w", item.LastWriteUtc?.ToString("O")), ("$state", item.State == "Ready" ? "ntfs-standard-info" : "unknown"), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$links", item.LinkCount));
                    objectId = Convert.ToInt64(insert.ExecuteScalar());
                    using var entry = Sql(db, tx, "INSERT INTO directory_entries(scope_id,generation,object_id,parent_entry_id,name,name_folded,cached_path,is_hidden) VALUES($s,$g,$o,$p,$n,$f,$path,$h) RETURNING entry_id", ("$s", scope), ("$g", generation), ("$o", objectId), ("$p", parent), ("$n", name), ("$f", name.ToUpperInvariant()), ("$path", full), ("$h", (item.Attributes & 2) != 0));
                    entryId = Convert.ToInt64(entry.ExecuteScalar());
                }
                else
                {
                    entryId = existing.Entry;
                    objectId = existing.Object;
                    retained.Add(entryId);
                    using var update = Sql(db, tx, """
                        UPDATE file_objects SET is_directory=$d,attributes=$a,logical_size=$l,allocated_size=$allocated,last_write_utc=$w,size_state=$state,size_updated_utc=$now,link_count=$links WHERE object_id=$o;
                        UPDATE directory_entries SET parent_entry_id=$p,name=$n,name_folded=$f,cached_path=$path,is_hidden=$h WHERE entry_id=$e;
                        """, ("$d", directory), ("$a", (long)item.Attributes), ("$l", directory ? 0 : item.LogicalBytes ?? 0), ("$allocated", directory ? 0 : item.AllocatedBytes), ("$w", item.LastWriteUtc?.ToString("O")), ("$state", item.State == "Ready" ? "ntfs-standard-info" : "unknown"), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$links", item.LinkCount), ("$o", objectId), ("$p", parent), ("$n", name), ("$f", name.ToUpperInvariant()), ("$path", full), ("$h", (item.Attributes & 2) != 0), ("$e", entryId));
                    update.ExecuteNonQuery();
                }
                Ancestors(db, tx, parent, affected);
                if (directory)
                    affected.Add(entryId);
            }
        }
        foreach (var row in old.Where(e => !retained.Contains(e.Entry)))
        {
            using var remove = Sql(db, tx, "DELETE FROM directory_entries WHERE entry_id=$e;DELETE FROM file_objects WHERE object_id=$o", ("$e", row.Entry), ("$o", row.Object));
            remove.ExecuteNonQuery();
        }
        using (var limit = Sql(db, tx, "SELECT count(*) FROM directory_entries WHERE scope_id=$s AND generation=$g", ("$s", scope), ("$g", generation)))
            if (Convert.ToInt64(limit.ExecuteScalar()) > 1_000_000)
                throw new SysBoxOperationException(new("NTFS_LIMIT", "目录项预算已到；旧快照保留，请停止或调整索引范围。"));
        var directories = new List<(long Id, int Depth)>();
        foreach (var id in affected)
        {
            using var q = Sql(db, tx, "SELECT e.cached_path FROM directory_entries e JOIN file_objects o USING(object_id) WHERE e.entry_id=$e AND o.is_directory=1", ("$e", id));
            if (q.ExecuteScalar() is string path)
                directories.Add((id, path.TrimEnd('\\').Count(c => c == '\\')));
        }
        foreach (var (id, _) in directories.OrderByDescending(d => d.Depth))
        {
            token.ThrowIfCancellationRequested();
            using var totals = Sql(db, tx, """
                INSERT INTO folder_totals(entry_id,logical_bytes,allocated_bytes,unknown_allocated,files,folders,hardlinked_entries)
                SELECT $e,coalesce(sum(CASE WHEN o.is_directory=1 THEN coalesce(t.logical_bytes,0) ELSE coalesce(o.logical_size,0) END),0),
                  coalesce(sum(CASE WHEN o.is_directory=1 THEN coalesce(t.allocated_bytes,0) ELSE coalesce(o.allocated_size,0) END),0),
                  coalesce(sum(CASE WHEN o.is_directory=1 THEN coalesce(t.unknown_allocated,0) ELSE (o.allocated_size IS NULL) END),0),
                  coalesce(sum(CASE WHEN o.is_directory=1 THEN coalesce(t.files,0) ELSE 1 END),0),
                  coalesce(sum(CASE WHEN o.is_directory=1 THEN coalesce(t.folders,0)+1 ELSE 0 END),0),
                  coalesce(sum(CASE WHEN o.is_directory=1 THEN coalesce(t.hardlinked_entries,0) ELSE (coalesce(o.link_count,0)>1) END),0)
                FROM directory_entries e JOIN file_objects o USING(object_id) LEFT JOIN folder_totals t USING(entry_id) WHERE e.parent_entry_id=$e
                ON CONFLICT(entry_id) DO UPDATE SET logical_bytes=excluded.logical_bytes,allocated_bytes=excluded.allocated_bytes,unknown_allocated=excluded.unknown_allocated,
                  files=excluded.files,folders=excluded.folders,hardlinked_entries=excluded.hardlinked_entries;
                """, ("$e", id));
            totals.ExecuteNonQuery();
        }
        using (var overflow = Sql(db, tx, "SELECT count(*) FROM folder_totals t JOIN directory_entries e USING(entry_id) WHERE e.scope_id=$s AND (typeof(logical_bytes)<>'integer' OR typeof(allocated_bytes)<>'integer' OR logical_bytes<0 OR allocated_bytes<0)", ("$s", scope)))
            if (Convert.ToInt64(overflow.ExecuteScalar()) != 0)
                throw new OverflowException("NTFS totals overflow.");
        long files, folders, bytes, errors, reparse;
        using (var q = Sql(db, tx, "SELECT coalesce(sum(o.is_directory=0),0),coalesce(sum(o.is_directory=1),0),coalesce(sum(CASE WHEN o.is_directory=0 THEN coalesce(o.logical_size,0) ELSE 0 END),0) FROM file_objects o WHERE scope_id=$s AND generation=$g", ("$s", scope), ("$g", generation)))
        using (var r = q.ExecuteReader())
        {
            r.Read();
            files = r.GetInt64(0);
            folders = r.GetInt64(1);
            bytes = r.GetInt64(2);
        }
        using (var q = Sql(db, tx, "SELECT coalesce(sum(json_extract(body,'$.state')='Unavailable'),0),coalesce(sum((json_extract(body,'$.attributes')&1024)<>0),0) FROM ntfs_cache WHERE scope_id=$s AND generation=$g", ("$s", scope), ("$g", generation)))
        using (var r = q.ExecuteReader())
        {
            r.Read();
            errors = r.GetInt64(0);
            reparse = r.GetInt64(1);
        }
        var report = new IndexProgress(root, errors + reparse == 0 ? IndexRunState.Complete : IndexRunState.Partial, files, folders, 0, errors, reparse, 0, bytes, started, DateTimeOffset.UtcNow, "NTFS / USN 共享快照；硬链接按目录项累计，可另看去重分配量。");
        long revision;
        using (var sequence = Sql(db, tx, "SELECT coalesce(max(snapshot_revision),0) FROM volumes"))
            revision = checked(Math.Max(DateTime.UtcNow.Ticks, Convert.ToInt64(sequence.ExecuteScalar()) + 1));
        using var publish = Sql(db, tx, """
            UPDATE volumes SET active_generation=$g,snapshot_revision=$revision,identity_kind='ntfs' WHERE scope_id=$s;
            INSERT INTO index_state(scope_id,report_json,published_report_json) VALUES($s,$r,$r) ON CONFLICT(scope_id) DO UPDATE SET report_json=$r,published_report_json=$r;
            DELETE FROM directory_entries WHERE scope_id=$s AND generation<>$g;DELETE FROM file_objects WHERE scope_id=$s AND generation<>$g;
            """, ("$s", scope), ("$g", generation), ("$revision", revision), ("$r", JsonSerializer.Serialize(report)));
        publish.ExecuteNonQuery();
        return report;
    }
    private static void Ancestors(SqliteConnection db, SqliteTransaction tx, long? entry, HashSet<long> affected)
    {
        var visited = new HashSet<long>();
        for (var depth = 0; entry is { } id; depth++)
        {
            if (depth > 512 || !visited.Add(id))
                throw Rebuild("目录祖先链无效。");
            affected.Add(id);
            using var q = Sql(db, tx, "SELECT parent_entry_id FROM directory_entries WHERE entry_id=$e", ("$e", id));
            entry = q.ExecuteScalar() is long parent ? parent : null;
        }
    }
}
