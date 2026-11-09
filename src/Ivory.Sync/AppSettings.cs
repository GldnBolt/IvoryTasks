using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Ivory.Core;

namespace Ivory.Sync;
public sealed class AppSettings
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IvoryTasks");
    public string ClientId { get; set; } = Environment.GetEnvironmentVariable("MICROSOFT_CLIENT_ID") ?? "";
    public string TenantId { get; set; } = Environment.GetEnvironmentVariable("TENANT_ID") ?? "common";
    public string Profile { get; set; } = "demo";
    public string Theme { get; set; } = "System";
    public int SyncMinutes { get; set; } = 5;
    public bool NotificationsEnabled { get; set; } = true;
    public bool TrayEnabled { get; set; }
    public bool WidgetEnabled { get; set; } = true;
    public bool WidgetImportantOnly { get; set; }
    public string DatabasePath => Path.Combine(DataRoot, SafeProfile(Profile) + ".db");
    static string SafeProfile(string profile) => profile == "demo" ? "demo" : "account-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)))[..32];
    public static AppSettings Load()
    {
        var path = Path.Combine(DataRoot, "settings.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException e) { AppLog.Write("settings.invalid", e); throw new InvalidDataException("Settings could not be read. Back up settings.json and repair it; task databases have not been changed.", e); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataRoot); var path = Path.Combine(DataRoot, "settings.json"); var temp = path + "." + Guid.NewGuid() + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true);
    }
}
public static class AppLog
{
    static readonly object Gate = new();
    public static void Write(string operation, Exception? error = null, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.Combine(AppSettings.DataRoot, "logs"); Directory.CreateDirectory(dir);
                // Never include exception messages, request bodies, URLs, task text, or token material.
                var line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, operation, error = error?.GetType().Name, code = error?.HResult, detail });
                File.AppendAllText(Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + $"-{Environment.ProcessId}.jsonl"), line + Environment.NewLine);
                foreach (var old in Directory.GetFiles(dir, "*.jsonl").Where(x => File.GetLastWriteTimeUtc(x) < DateTime.UtcNow.AddDays(-14))) File.Delete(old);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
