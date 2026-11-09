using Ivory.Core;
using Xunit;
namespace Ivory.Tests;
public class StoreTests
{
    static string PathForTest() => Path.Combine(Path.GetTempPath(), "IvoryTests", Guid.NewGuid() + ".db");
    [Fact] public void CrudSurvivesRestartAndMigration()
    {
        var path = PathForTest(); var db = new TaskStore(path); var list = new TaskList(Guid.NewGuid(), "Personal"); db.SaveList(list);
        var task = new TaskItem { Title = "Read", ListId = list.Id, DueDate = new DateOnly(2026, 11, 8), Notes = "Chapter 3" }; db.Save(task);
        db = new TaskStore(path); task = db.Find(task.Id)!; Assert.Equal("Read", task.Title);
        db.Edit(task with { Title = "Read book" }); task = db.Find(task.Id)!; db.Complete(task, true);
        Assert.NotNull(db.Find(task.Id)!.CompletedAt); db.Complete(db.Find(task.Id)!, false); Assert.Null(db.Find(task.Id)!.CompletedAt);
        db.Delete(db.Find(task.Id)!); Assert.True(new TaskStore(path).Find(task.Id)!.Deleted);
    }
    [Fact] public void ConcurrentEditIsRejected()
    {
        var db = new TaskStore(PathForTest()); db.SeedDemo(); var task = db.Tasks()[0]; db.Edit(task with { Title = "New" });
        Assert.Throws<InvalidOperationException>(() => db.Edit(task with { Title = "Stale" }));
    }
    [Fact] public void RemoteIdentityIsUnique()
    {
        var db = new TaskStore(PathForTest()); db.SeedDemo(); var t = db.Tasks()[0]; db.Save(t with { RemoteId = "1", RemoteListId = "a" });
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Save(t with { Id = Guid.NewGuid(), RemoteId = "1", RemoteListId = "a" }));
    }
    [Fact] public void QueryIncludesListAndNotesAndDateBoundaries()
    {
        var db = new TaskStore(PathForTest()); db.SeedDemo(); var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal(3, TaskQuery.Apply(db.Tasks(), TaskFilter.Today, today).Count());
        Assert.Single(TaskQuery.Apply(db.Tasks(), TaskFilter.Overdue, today));
        Assert.Equal(3, TaskQuery.Apply(db.Tasks(), TaskFilter.All, today, "university", lists: db.Lists()).Count());
    }
    [Fact] public void ConflictPreservesBothVersions()
    {
        var db = new TaskStore(PathForTest()); db.SeedDemo(); var t = db.Tasks()[0]; var remote = t with { Title = "Remote edit" };
        db.AddConflict(t, remote, "Both edited"); Assert.Equal(t.Title, db.Conflicts()[0].Local.Title); Assert.Equal("Remote edit", db.Conflicts()[0].Remote!.Title);
        db.Resolve(db.Conflicts()[0], false); Assert.Equal("Remote edit", db.Find(t.Id)!.Title); Assert.Empty(db.Conflicts());
    }
    [Fact] public void FutureSchemaIsRejectedWithoutChangingData()
    {
        var path = PathForTest(); var db = new TaskStore(path); db.SeedDemo();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path)) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=2"; cmd.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => new TaskStore(path)); Assert.Equal(6, db.Tasks().Count);
    }
    [Fact] public void MissingListCannotCreateOrphanTask()
    {
        var db = new TaskStore(PathForTest()); Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Save(new TaskItem { Title = "Orphan", ListId = Guid.NewGuid() })); Assert.Empty(db.Tasks());
    }
    [Fact] public void BlankTitleDoesNotOverwriteExistingTask()
    {
        var db = new TaskStore(PathForTest()); db.SeedDemo(); var task = db.Tasks()[0]; Assert.Throws<ArgumentException>(() => db.Edit(task with { Title = "  " })); Assert.Equal(task.Title, db.Find(task.Id)!.Title);
    }
}
