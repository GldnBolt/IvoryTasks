using System.Net;
using System.Text;
using System.Text.Json;
using Ivory.Core;
using Ivory.Sync;
using Xunit;
namespace Ivory.Tests;
public sealed class GraphAndWidgetTests
{
    sealed class Tokens : ITokenProvider { public int Calls; public Task<string> GetToken(CancellationToken ct) { Calls++; return Task.FromResult("test-token"); } }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request)); }
    [Fact] public async Task TokenIsRequestedAndLimitedToGraph()
    {
        var tokens = new Tokens(); var calls = 0;
        using var http = new HttpClient(new Handler(request => { calls++; Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); return new(HttpStatusCode.OK) { Content = new StringContent("{\"value\":[],\"@odata.deltaLink\":\"checkpoint\"}", Encoding.UTF8, "application/json") }; }));
        var service = new MicrosoftTodoService(tokens, http); await service.GetTasks("list", null, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetTasks("list", "https://example.com/steal-token", default)); Assert.Equal(1, calls); Assert.Equal(1, tokens.Calls);
    }
    [Fact] public async Task UnauthorizedResponseIsTypedAndDoesNotExposeBody()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("sensitive-response") }));
        var e = await Assert.ThrowsAsync<GraphException>(() => new MicrosoftTodoService(new Tokens(), http).GetTaskLists(default)); Assert.Equal(401, e.StatusCode); Assert.DoesNotContain("sensitive", e.Message);
    }
    [Fact] public async Task DeleteNotFoundIsIdempotent()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        await new MicrosoftTodoService(new Tokens(), http).DeleteTask(new TaskItem { RemoteId = "missing", RemoteListId = "list" }, default);
    }
    [Fact] public void WidgetEscapesTitlesExcludesCompletedAndSupportsActions()
    {
        var today = new DateOnly(2026, 11, 8); var title = "Quote \" and <tag>";
        var card = WidgetCard.Create([new TaskItem { Title = title, DueDate = today }, new TaskItem { Title = "Hidden", Completed = true }], today, 3, "demo", true, false, true);
        using var json = JsonDocument.Parse(card); Assert.Equal("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("Hidden", card); Assert.Contains("complete", card); Assert.Contains("revision", card); Assert.Contains("LOCAL DEMO", card);
    }
    [Fact] public void DisabledWidgetDoesNotLeakTaskText()
    {
        var card = WidgetCard.Create([new TaskItem { Title = "Private task" }], new DateOnly(2026, 11, 8), 3, "demo", false, false, false); Assert.DoesNotContain("Private task", card); Assert.DoesNotContain("complete", card);
    }
    sealed class FailedAuth : ITokenProvider { public Task<string> GetToken(CancellationToken ct) => throw new InvalidOperationException("Sign in again"); }
    [Fact] public async Task AuthenticationFailureNeverSendsRequest()
    {
        var calls = 0; using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftTodoService(new FailedAuth(), http).GetTaskLists(default)); Assert.Equal(0, calls);
    }
    [Fact] public async Task LongRetryAfterDefersRatherThanHammeringService()
    {
        var calls = 0; using var http = new HttpClient(new Handler(_ => { calls++; var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10)); return r; }));
        var e = await Assert.ThrowsAsync<GraphException>(() => new MicrosoftTodoService(new Tokens(), http).GetTaskLists(default)); Assert.Equal(429, e.StatusCode); Assert.Equal(1, calls); Assert.True(e.RetryAt > DateTimeOffset.UtcNow.AddMinutes(9));
    }
    [Fact] public void WidgetRespectsImportantFilterAndLimit()
    {
        var today = new DateOnly(2026, 11, 8);
        var card = WidgetCard.Create([new TaskItem { Title = "Low priority", Priority = Priority.Low }, new TaskItem { Title = "Urgent overdue", DueDate = today.AddDays(-1), Priority = Priority.High }, new TaskItem { Title = "Later important", DueDate = today.AddDays(1), Priority = Priority.High }], today, 1, "demo", true, true, false);
        Assert.Contains("Urgent overdue", card); Assert.DoesNotContain("Low priority", card); Assert.DoesNotContain("Later important", card);
    }
}
