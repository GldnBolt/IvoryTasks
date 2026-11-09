using System.Text.Json;
using Ivory.Core;
using Ivory.Sync;
using Xunit;
namespace Ivory.Tests;
public sealed class SyncTests
{
    static readonly DateTimeOffset Epoch = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    static (TaskStore Store, FakeRemote Remote, SyncEngine Sync, TaskList List) Setup()
    {
        var path = Path.Combine(Path.GetTempPath(), "IvoryTests", Guid.NewGuid() + ".db");
        var store = new TaskStore(path); var list = new TaskList(Guid.NewGuid(), "Personal", "list-1"); store.SaveList(list);
        var remote = new FakeRemote(list); return (store, remote, new SyncEngine(store, remote, path + ".lock"), list);
    }
    sealed class FakeRemote(TaskList list) : IMicrosoftTodoService
    {
        public List<JsonElement> Changes = [];
        public int Creates, Updates, Deletes;
        public List<string?> Links = [];
        public bool FailCreate;
        public bool FailPull;
        public bool Expired;
        public bool ListRemoved;
        public Func<string?, DeltaPage>? Page;
        public Func<TaskItem, TaskItem>? DuringUpdate;
        public Task<IReadOnlyList<TaskList>> GetTaskLists(CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskList>>(ListRemoved ? [] : [list]);
        public Task<TaskList> CreateList(TaskList value, CancellationToken ct) => Task.FromResult(value with { RemoteId = "new-list" });
        public Task<DeltaPage> GetTasks(string id, string? link, CancellationToken ct)
        {
            Links.Add(link); if (FailPull) throw new HttpRequestException("Offline");
            if (Expired && link != null) { Expired = false; throw new GraphException(410); }
            if (Page != null) return Task.FromResult(Page(link));
            return Task.FromResult(new DeltaPage(Changes, null, "https://graph.microsoft.com/v1.0/me/todo/lists/list-1/tasks/delta?$deltatoken=checkpoint"));
        }
        public Task<TaskItem> CreateTask(TaskItem task, TaskList list, CancellationToken ct)
        {
            Creates++; if (FailCreate) throw new HttpRequestException("Connection dropped after POST");
            return Task.FromResult(task with { RemoteId = "new-task-" + Creates, RemoteListId = list.RemoteId, SyncStatus = SyncStatus.Synced, RemoteModifiedAt = Epoch, ETag = "v1" });
        }
        public Task<TaskItem> UpdateTask(TaskItem task, TaskList list, CancellationToken ct)
        {
            Updates++; DuringUpdate?.Invoke(task);
            return Task.FromResult(task with { SyncStatus = SyncStatus.Synced, RemoteModifiedAt = Epoch.AddMinutes(1), ETag = "v2" });
        }
        public Task DeleteTask(TaskItem task, CancellationToken ct) { Deletes++; return Task.CompletedTask; }
    }
    static JsonElement RemoteTask(string title = "Remote", string status = "notStarted", string version = "v1", string time = "2026-11-08T12:00:00Z") => JsonSerializer.SerializeToElement(new Dictionary<string, object?> {
        ["id"] = "remote-1", ["title"] = title, ["status"] = status, ["importance"] = "high", ["lastModifiedDateTime"] = time, ["@odata.etag"] = version, ["body"] = new { content = "Notes", contentType = "text" }, ["dueDateTime"] = new { dateTime = "2026-11-09T00:00:00", timeZone = "UTC" }
    });
    [Fact] public async Task PullIsIdempotentAndUsesCheckpoint()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); await sync.Run();
        Assert.Single(db.Tasks()); Assert.Null(remote.Links[0]); Assert.Contains("deltatoken", remote.Links[1]); Assert.Equal(0, remote.Creates);
    }
    [Fact] public async Task PendingLocalCreateUploadsOnce()
    {
        var (db, remote, sync, list) = Setup(); var task = new TaskItem { Title = "Local", ListId = list.Id, SyncStatus = SyncStatus.Pending }; db.Save(task);
        await sync.Run(); Assert.Equal(1, remote.Creates); Assert.Equal("new-task-1", db.Find(task.Id)!.RemoteId); Assert.Equal(SyncStatus.Synced, db.Find(task.Id)!.SyncStatus);
    }
    [Fact] public async Task CompletionPushAndPull()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); db.Complete(db.Tasks()[0], true); await sync.Run();
        Assert.Equal(1, remote.Updates); Assert.True(db.Tasks()[0].Completed);
        remote.Changes.Clear(); remote.Changes.Add(RemoteTask(status: "notStarted", version: "v3", time: "2026-11-08T13:00:00Z")); await sync.Run(); Assert.False(db.Tasks()[0].Completed);
    }
    [Fact] public async Task RemoteDeletionRetainsTombstone()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); remote.Changes.Clear();
        remote.Changes.Add(JsonDocument.Parse("{\"id\":\"remote-1\",\"@removed\":{\"reason\":\"deleted\"}}").RootElement.Clone());
        await sync.Run(); Assert.True(db.Tasks()[0].Deleted); Assert.Equal(0, remote.Deletes);
    }
    [Fact] public async Task LocalDeletionPushesOnce()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); db.Delete(db.Tasks()[0]); await sync.Run(); await sync.Run(); Assert.Equal(1, remote.Deletes);
    }
    [Fact] public async Task BothEditedProducesReviewableConflict()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); db.Edit(db.Tasks()[0] with { Title = "Local edit" });
        remote.Changes[0] = RemoteTask("Phone edit", version: "v2", time: "2026-11-08T13:00:00Z"); await sync.Run();
        Assert.Single(db.Conflicts()); Assert.Equal("Local edit", db.Conflicts()[0].Local.Title); Assert.Equal("Phone edit", db.Conflicts()[0].Remote!.Title); Assert.Equal(0, remote.Updates);
    }
    [Fact] public async Task InterruptedCreateNeverAutomaticallyRepeatsPost()
    {
        var (db, remote, sync, list) = Setup(); db.Save(new TaskItem { Title = "Uncertain", ListId = list.Id, SyncStatus = SyncStatus.Pending }); remote.FailCreate = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.Run()); remote.FailCreate = false; await sync.Run();
        Assert.Equal(1, remote.Creates); Assert.Single(db.Conflicts());
    }
    [Fact] public async Task OfflineChangesRemainPending()
    {
        var (db, remote, sync, list) = Setup(); db.Save(new TaskItem { Title = "Offline", ListId = list.Id, SyncStatus = SyncStatus.Pending }); remote.FailPull = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.Run()); Assert.Equal(SyncStatus.Pending, db.Tasks()[0].SyncStatus);
        remote.FailPull = false; await sync.Run(); Assert.Equal(1, remote.Creates);
    }
    [Fact] public async Task EditDuringUploadIsPreservedAndStillPending()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); db.Edit(db.Tasks()[0] with { Title = "First" });
        remote.DuringUpdate = task => db.Edit(db.Find(task.Id)! with { Title = "Second" }); await sync.Run();
        Assert.Equal("Second", db.Tasks()[0].Title); Assert.Equal(SyncStatus.Pending, db.Tasks()[0].SyncStatus); Assert.Equal("v2", db.Tasks()[0].ETag);
    }
    [Fact] public async Task ExpiredDeltaPerformsSafeFullRescan()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); remote.Expired = true; await sync.Run(); Assert.Single(db.Tasks()); Assert.Null(remote.Links.Last());
    }
    [Fact] public void GraphMappingPreservesRecurrenceAndDate()
    {
        var (_, _, _, list) = Setup(); var task = GraphMapping.FromGraph(RemoteTask(), list) with { Recurrence = GraphMapping.CreateRecurrence(2, new DateTime(2026, 11, 9)) };
        Assert.Equal(new DateOnly(2026, 11, 9), task.DueDate); var json = JsonSerializer.SerializeToElement(GraphMapping.ToGraph(task)); Assert.Equal("weekly", json.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString());
    }
    [Fact] public async Task RemoteDeleteDuringLocalEditRequiresChoice()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); db.Edit(db.Tasks()[0] with { Title = "Keep my notes" });
        remote.Changes[0] = JsonDocument.Parse("{\"id\":\"remote-1\",\"@removed\":{\"reason\":\"deleted\"}}").RootElement.Clone(); await sync.Run();
        Assert.Single(db.Conflicts()); Assert.False(db.Tasks()[0].Deleted); Assert.Equal(0, remote.Creates);
        db.Resolve(db.Conflicts()[0], true); Assert.Null(db.Tasks()[0].RemoteId); Assert.Equal(SyncStatus.Pending, db.Tasks()[0].SyncStatus);
    }
    [Fact] public async Task MissingRemoteListPreservesLocalTasks()
    {
        var (db, remote, sync, _) = Setup(); remote.Changes.Add(RemoteTask()); await sync.Run(); remote.ListRemoved = true; await sync.Run();
        Assert.True(db.Lists()[0].RemoteMissing); Assert.False(db.Tasks()[0].Deleted); Assert.Single(db.Conflicts());
    }
    [Fact] public async Task InterruptedDeltaDoesNotAdvanceCheckpointAndCanReplay()
    {
        var (db, remote, sync, list) = Setup();
        remote.Page = link => link == null ? new DeltaPage([RemoteTask()], "next-page", null) : throw new HttpRequestException("Offline on page two");
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.Run()); Assert.Single(db.Tasks()); Assert.Null(db.Get("delta:" + list.RemoteId));
        remote.Page = link => link == null ? new DeltaPage([RemoteTask()], "next-page", null) : new DeltaPage([], null, "final-checkpoint");
        await sync.Run(); Assert.Single(db.Tasks()); Assert.Equal("final-checkpoint", db.Get("delta:" + list.RemoteId));
    }
}
