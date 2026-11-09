using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ivory.Core;

namespace Ivory.Sync;
public sealed class GraphException(int statusCode, DateTimeOffset? retryAt = null) : Exception($"Microsoft Graph returned HTTP {statusCode}.") { public int StatusCode { get; } = statusCode; public DateTimeOffset? RetryAt { get; } = retryAt; }
public sealed record DeltaPage(IReadOnlyList<JsonElement> Items, string? NextLink, string? DeltaLink);
public interface IMicrosoftTodoService
{
    Task<IReadOnlyList<TaskList>> GetTaskLists(CancellationToken ct);
    Task<TaskList> CreateList(TaskList list, CancellationToken ct);
    Task<DeltaPage> GetTasks(string listId, string? link, CancellationToken ct);
    Task<TaskItem> CreateTask(TaskItem task, TaskList list, CancellationToken ct);
    Task<TaskItem> UpdateTask(TaskItem task, TaskList list, CancellationToken ct);
    Task DeleteTask(TaskItem task, CancellationToken ct);
}
public sealed class MicrosoftTodoService(ITokenProvider tokens, HttpClient http) : IMicrosoftTodoService
{
    const string Root = "https://graph.microsoft.com/v1.0/";
    static string E(string id) => Uri.EscapeDataString(id);
    async Task<JsonElement?> Send(HttpMethod method, string path, object? body, string? etag, CancellationToken ct)
    {
        var uri = Uri.TryCreate(path, UriKind.Absolute, out var absolute) ? absolute : new Uri(Root + path);
        // Pagination and delta links are remote input. Never send bearer tokens to arbitrary hosts.
        if (uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || uri.Port != 443 || !uri.AbsolutePath.StartsWith("/v1.0/me/todo/", StringComparison.Ordinal)) throw new InvalidDataException("Rejected an unexpected Microsoft Graph link.");
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetToken(ct));
            if (etag != null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            if (body != null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, ct);
            var code = (int)response.StatusCode;
            // POSTs are not retried: an interrupted create may already have reached the server.
            if (method != HttpMethod.Post && attempt < 3 && (code == 429 || code is 502 or 503 or 504))
            {
                var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                if (retry > TimeSpan.FromSeconds(30)) throw new GraphException(code, DateTimeOffset.UtcNow + retry);
                await Task.Delay(retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1), ct); continue;
            }
            if (method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) {
                AppLog.Write("graph.error", detail: code.ToString());
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                throw new GraphException(code, delay > TimeSpan.Zero ? DateTimeOffset.UtcNow + delay : null);
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); return json.RootElement.Clone();
        }
    }
    public async Task<IReadOnlyList<TaskList>> GetTaskLists(CancellationToken ct)
    {
        var result = new List<TaskList>(); string? url = "me/todo/lists?$top=100";
        do {
            var page = (await Send(HttpMethod.Get, url, null, null, ct))!.Value;
            result.AddRange(page.GetProperty("value").EnumerateArray().Select(x => new TaskList(Guid.NewGuid(), x.GetProperty("displayName").GetString() ?? "Unnamed list", x.GetProperty("id").GetString())));
            url = page.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        } while (url != null);
        return result;
    }
    public async Task<TaskList> CreateList(TaskList list, CancellationToken ct)
    {
        var e = (await Send(HttpMethod.Post, "me/todo/lists", new { displayName = list.Name }, null, ct))!.Value;
        return list with { RemoteId = e.GetProperty("id").GetString() };
    }
    public async Task<DeltaPage> GetTasks(string listId, string? link, CancellationToken ct)
    {
        var e = (await Send(HttpMethod.Get, link ?? $"me/todo/lists/{E(listId)}/tasks/delta", null, null, ct))!.Value;
        return new DeltaPage(e.GetProperty("value").EnumerateArray().Select(x => x.Clone()).ToArray(), e.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null, e.TryGetProperty("@odata.deltaLink", out var d) ? d.GetString() : null);
    }
    public async Task<TaskItem> CreateTask(TaskItem task, TaskList list, CancellationToken ct) => GraphMapping.FromGraph((await Send(HttpMethod.Post, $"me/todo/lists/{E(list.RemoteId!)}/tasks", GraphMapping.ToGraph(task), null, ct))!.Value, list);
    public async Task<TaskItem> UpdateTask(TaskItem task, TaskList list, CancellationToken ct) => GraphMapping.FromGraph((await Send(HttpMethod.Patch, $"me/todo/lists/{E(list.RemoteId!)}/tasks/{E(task.RemoteId!)}", GraphMapping.ToGraph(task), task.ETag, ct))!.Value, list);
    public async Task DeleteTask(TaskItem task, CancellationToken ct) => await Send(HttpMethod.Delete, $"me/todo/lists/{E(task.RemoteListId!)}/tasks/{E(task.RemoteId!)}", null, task.ETag, ct);
}
