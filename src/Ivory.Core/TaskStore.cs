using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Ivory.Core;

public sealed class TaskStore
{
    readonly string connectionString;
    public TaskStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 1) throw new InvalidOperationException("This database needs a newer version of Ivory Tasks.");
        if (version == 0)
        {
            using var tx = c.BeginTransaction();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE lists(id TEXT PRIMARY KEY, remote_id TEXT UNIQUE, payload TEXT NOT NULL);
                CREATE TABLE tasks(id TEXT PRIMARY KEY, list_id TEXT NOT NULL REFERENCES lists(id), remote_id TEXT, remote_list_id TEXT, payload TEXT NOT NULL);
                CREATE UNIQUE INDEX remote_identity ON tasks(remote_list_id, remote_id) WHERE remote_id IS NOT NULL;
                CREATE INDEX task_list ON tasks(list_id);
                CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE conflicts(id TEXT PRIMARY KEY, task_id TEXT NOT NULL UNIQUE, payload TEXT NOT NULL);
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery(); tx.Commit();
        }
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON;"; cmd.ExecuteNonQuery(); return c; }
    static string Json<T>(T value) => JsonSerializer.Serialize(value);
    public List<T> Read<T>(string sql, params (string, object?)[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var result = new List<T>();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<T>(r.GetString(0))!);
        return result;
    }
    public List<TaskItem> Tasks() => Read<TaskItem>("SELECT payload FROM tasks");
    public List<TaskList> Lists() => Read<TaskList>("SELECT payload FROM lists ORDER BY rowid");
    public List<Conflict> Conflicts() => Read<Conflict>("SELECT payload FROM conflicts");
    public TaskItem? Find(Guid id) => Read<TaskItem>("SELECT payload FROM tasks WHERE id=$id", ("$id", id.ToString())).FirstOrDefault();
    public TaskItem? FindRemote(string list, string id) => Read<TaskItem>("SELECT payload FROM tasks WHERE remote_list_id=$list AND remote_id=$id", ("$list", list), ("$id", id)).FirstOrDefault();
    public void SaveList(TaskList list) => Exec("INSERT INTO lists VALUES($id,$remote,$payload) ON CONFLICT(id) DO UPDATE SET remote_id=$remote,payload=$payload", ("$id", list.Id.ToString()), ("$remote", list.RemoteId), ("$payload", Json(list)));
    public void Save(TaskItem item, long? expectedRevision = null)
    {
        if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 255) throw new ArgumentException("Enter a title of 1–255 characters.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload FROM tasks WHERE id=$id"; cmd.Parameters.AddWithValue("$id", item.Id.ToString());
        var old = cmd.ExecuteScalar() as string;
        var revision = old == null ? 0 : JsonSerializer.Deserialize<TaskItem>(old)!.Revision;
        if (expectedRevision != null && expectedRevision != revision) throw new InvalidOperationException("This task changed in another window. Reopen it before saving.");
        cmd.CommandText = "INSERT INTO tasks VALUES($id,$list,$remote,$rl,$payload) ON CONFLICT(id) DO UPDATE SET list_id=$list,remote_id=$remote,remote_list_id=$rl,payload=$payload";
        cmd.Parameters.AddWithValue("$list", item.ListId.ToString()); cmd.Parameters.AddWithValue("$remote", (object?)item.RemoteId ?? DBNull.Value); cmd.Parameters.AddWithValue("$rl", (object?)item.RemoteListId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", Json(item with { Revision = revision + 1 }));
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    public TaskItem Edit(TaskItem item)
    {
        if (item.SyncStatus is SyncStatus.Conflict or SyncStatus.Uploading) throw new InvalidOperationException("Resolve this task's synchronization state first.");
        var edited = item with { UpdatedAt = DateTimeOffset.UtcNow, SyncStatus = item.SyncStatus == SyncStatus.Local ? SyncStatus.Local : SyncStatus.Pending };
        Save(edited, item.Revision); return Find(item.Id)!;
    }
    public void Complete(TaskItem item, bool complete) => Edit(item with { Completed = complete, CompletedAt = complete ? DateTimeOffset.UtcNow : null });
    public void Delete(TaskItem item) => Edit(item with { Deleted = true });
    public void AddConflict(TaskItem local, TaskItem? remote, string reason)
    {
        // Write recovery evidence before changing state; never discard either version.
        var conflict = new Conflict(Guid.NewGuid(), local.Id, reason, local, remote, DateTimeOffset.UtcNow);
        Exec("INSERT INTO conflicts VALUES($id,$task,$payload) ON CONFLICT(task_id) DO UPDATE SET payload=$payload", ("$id", conflict.Id.ToString()), ("$task", local.Id.ToString()), ("$payload", Json(conflict)));
        Save(local with { SyncStatus = SyncStatus.Conflict }, local.Revision);
    }
    public void Resolve(Conflict conflict, bool keepLocal)
    {
        var current = Find(conflict.TaskId) ?? throw new InvalidOperationException("Task no longer exists.");
        var item = keepLocal ? conflict.Local with { SyncStatus = SyncStatus.Pending, ETag = conflict.Remote?.ETag, RemoteModifiedAt = conflict.Remote?.RemoteModifiedAt } : conflict.Remote;
        if (item == null) item = current with { Deleted = true, SyncStatus = SyncStatus.Synced };
        // For a remote deletion, keeping local makes a new task; the deleted identity is never reused.
        if (keepLocal && conflict.Remote == null) item = item with { RemoteId = null, ETag = null };
        Save(item with { Id = current.Id, ListId = current.ListId }, current.Revision);
        Exec("DELETE FROM conflicts WHERE task_id=$id", ("$id", current.Id.ToString()));
    }
    public string? Get(string key)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() as string;
    }
    public void Set(string key, string? value) => Exec("INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value", ("$key", key), ("$value", value ?? ""));
    void Exec(string sql, params (string, object?)[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void SeedDemo()
    {
        if (Get("seeded") == "yes") return;
        var personal = new TaskList(Guid.NewGuid(), "Personal"); var university = new TaskList(Guid.NewGuid(), "University"); var work = new TaskList(Guid.NewGuid(), "Work");
        foreach (var list in new[] { personal, university, work }) SaveList(list);
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var (title, list, days, priority) in new[] { ("Study SQL", university, 0, Priority.High), ("Finish university project", university, 1, Priority.High), ("Prepare presentation", work, 2, Priority.Normal), ("Go to the gym", personal, 0, Priority.Normal), ("Submit assignment", university, -1, Priority.High), ("Review work tasks", work, 0, Priority.Low) })
            Save(new TaskItem { Title = title, ListId = list.Id, DueDate = today.AddDays(days), Priority = priority });
        Set("seeded", "yes");
    }
}
