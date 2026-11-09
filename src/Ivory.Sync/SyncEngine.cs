using System.Text.Json;
using Ivory.Core;

namespace Ivory.Sync;
public sealed class SyncEngine(TaskStore store, IMicrosoftTodoService remote, string lockPath)
{
    public async Task Run(CancellationToken ct = default)
    {
        // A non-shared file handle serializes synchronization across app processes without thread-affine mutex ownership.
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream guard;
        try { guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another window is synchronizing. Try again shortly."); }
        using (guard)
        {
            if (DateTimeOffset.TryParse(store.Get("retryAfter"), out var retryAt) && retryAt > DateTimeOffset.UtcNow) throw new InvalidOperationException($"Microsoft requested a pause. Synchronization can retry after {retryAt.ToLocalTime():t}. Local changes are saved.");
            try
            {
            AppLog.Write("sync.begin");
            // A crash between a POST and saving its response must never cause an automatic duplicate POST.
            foreach (var t in store.Tasks().Where(x => x.SyncStatus == SyncStatus.Uploading)) store.AddConflict(t, null, "A create was interrupted. Check Microsoft To Do before keeping local: it may already exist there.");
            var remoteLists = await remote.GetTaskLists(ct);
            var localLists = store.Lists();
            foreach (var missing in localLists.Where(x => x.RemoteId != null && !remoteLists.Any(r => r.RemoteId == x.RemoteId)))
            {
                store.SaveList(missing with { RemoteMissing = true });
                foreach (var task in store.Tasks().Where(x => x.ListId == missing.Id && !x.Deleted && x.SyncStatus != SyncStatus.Conflict)) store.AddConflict(task, null, "The Microsoft list was removed or is inaccessible. Local data has been preserved.");
            }
            foreach (var list in remoteLists)
            {
                var local = localLists.FirstOrDefault(x => x.RemoteId == list.RemoteId);
                store.SaveList(list with { Id = local?.Id ?? list.Id });
            }
            foreach (var local in store.Lists().Where(x => x.RemoteId == null && !x.RemoteMissing))
            {
                var key = "list-create:" + local.Id;
                if (store.Get(key) == "uncertain") continue;
                store.Set(key, "uncertain");
                try { store.SaveList(await remote.CreateList(local, ct)); store.Set(key, ""); }
                catch (GraphException e) when (e.StatusCode is >= 400 and < 500 && e.StatusCode != 408) { store.Set(key, ""); throw; }
            }
            foreach (var list in store.Lists().Where(x => x.RemoteId != null && !x.RemoteMissing))
            {
                ct.ThrowIfCancellationRequested();
                await Pull(list, ct);
                foreach (var snapshot in store.Tasks().Where(x => x.ListId == list.Id && x.SyncStatus == SyncStatus.Pending).ToList())
                {
                    var task = store.Find(snapshot.Id)!;
                    if (task.SyncStatus != SyncStatus.Pending) continue;
                    if (task.Deleted)
                    {
                        if (task.RemoteId != null) await remote.DeleteTask(task with { RemoteListId = list.RemoteId }, ct);
                        var current = store.Find(task.Id)!;
                        if (current.Revision == task.Revision) store.Save(task with { SyncStatus = SyncStatus.Synced, LastSyncedAt = DateTimeOffset.UtcNow }, task.Revision);
                        continue;
                    }
                    var creating = task.RemoteId == null;
                    if (creating) { store.Save(task with { SyncStatus = SyncStatus.Uploading }, task.Revision); task = store.Find(task.Id)!; }
                    try
                    {
                        var updated = creating ? await remote.CreateTask(task, list, ct) : await remote.UpdateTask(task, list, ct);
                        var current = store.Find(task.Id)!;
                        // An edit made during a request retains its data and receives the new remote baseline.
                        var result = current.Revision == task.Revision ? updated with { Id = task.Id, CreatedAt = task.CreatedAt } : current with { RemoteId = updated.RemoteId, RemoteListId = updated.RemoteListId, RemoteModifiedAt = updated.RemoteModifiedAt, ETag = updated.ETag, SyncStatus = SyncStatus.Pending, LastSyncedAt = DateTimeOffset.UtcNow };
                        store.Save(result, current.Revision);
                    }
                    catch (GraphException e) when (e.StatusCode is 404 or 412)
                    {
                        if (creating) store.Save(task with { SyncStatus = SyncStatus.Pending }, task.Revision);
                        else { store.Set("delta:" + list.RemoteId, ""); await Pull(list, ct); var current = store.Find(task.Id)!; if (current.SyncStatus != SyncStatus.Conflict) store.AddConflict(current, null, "Remote task changed or disappeared during upload. Review before retrying."); }
                    }
                    catch (GraphException e) when (creating && e.StatusCode is >= 400 and < 500 && e.StatusCode != 408)
                    { store.Save(task with { SyncStatus = SyncStatus.Pending }, task.Revision); throw; }
                    catch when (creating)
                    { store.AddConflict(store.Find(task.Id)!, null, "Create outcome unknown. Check Microsoft To Do before keeping local, which may create a duplicate."); throw; }
                }
            }
            store.Set("lastSync", DateTimeOffset.UtcNow.ToString("O")); AppLog.Write("sync.complete");
            }
            catch (GraphException e) { if (e.RetryAt is {} nextTry) store.Set("retryAfter", nextTry.ToString("O")); throw; }
        }
    }
    async Task Pull(TaskList list, CancellationToken ct)
    {
        var key = "delta:" + list.RemoteId; var link = store.Get(key); if (string.IsNullOrEmpty(link)) link = null;
        var full = link == null; var seen = new HashSet<string>(); string? delta = null;
        do
        {
            DeltaPage page;
            try { page = await remote.GetTasks(list.RemoteId!, link, ct); }
            catch (GraphException e) when (e.StatusCode == 410 && !full) { store.Set(key, ""); await Pull(list, ct); return; }
            foreach (var json in page.Items)
            {
                var id = json.GetProperty("id").GetString()!; seen.Add(id);
                var local = store.FindRemote(list.RemoteId!, id);
                if (json.TryGetProperty("@removed", out _)) { if (local != null) Removed(local); continue; }
                var incoming = GraphMapping.FromGraph(json, list);
                if (local == null) { store.Save(incoming); continue; }
                if (local.SyncStatus == SyncStatus.Conflict) continue;
                var remoteChanged = incoming.RemoteModifiedAt != local.RemoteModifiedAt || (incoming.ETag != null && incoming.ETag != local.ETag);
                if (local.SyncStatus == SyncStatus.Pending)
                {
                    if (remoteChanged) store.AddConflict(local, incoming with { Id = local.Id }, "Both the local and Microsoft versions changed. Choose which to keep; both copies are preserved here.");
                }
                else if (remoteChanged) store.Save(incoming with { Id = local.Id }, local.Revision);
            }
            link = page.NextLink; delta = page.DeltaLink ?? delta;
        } while (link != null);
        if (full) foreach (var missing in store.Tasks().Where(x => x.RemoteListId == list.RemoteId && x.RemoteId != null && !seen.Contains(x.RemoteId) && !x.Deleted)) Removed(missing);
        if (delta == null) throw new InvalidDataException("Microsoft did not return a delta checkpoint. Local changes have been retained.");
        store.Set(key, delta);
    }
    void Removed(TaskItem local)
    {
        if (local.SyncStatus == SyncStatus.Conflict) return;
        if (local.SyncStatus == SyncStatus.Pending && !local.Deleted) store.AddConflict(local, null, "Task was deleted remotely while you edited it locally.");
        else store.Save(local with { Deleted = true, SyncStatus = SyncStatus.Synced, LastSyncedAt = DateTimeOffset.UtcNow }, local.Revision);
    }
}
