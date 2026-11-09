using Microsoft.Identity.Client;
using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace Ivory.Sync;
public interface ITokenProvider { Task<string> GetToken(CancellationToken ct = default); }
[SupportedOSPlatform("windows")]
public sealed class AuthService : ITokenProvider
{
    static readonly string[] Scopes = ["Tasks.ReadWrite"];
    readonly IPublicClientApplication client;
    readonly string cachePath;
    static readonly SemaphoreSlim CacheGate = new(1, 1);
    FileStream? cacheLock;
    public AuthService(string clientId, string tenant)
    {
        if (!Guid.TryParse(clientId, out _)) throw new ArgumentException("Enter the Application (client) ID from your Entra app registration.");
        if (tenant != "common" && tenant != "consumers" && tenant != "organizations" && !Guid.TryParse(tenant, out _)) throw new ArgumentException("Tenant must be common, consumers, organizations, or a tenant GUID.");
        Directory.CreateDirectory(AppSettings.DataRoot);
        cachePath = Path.Combine(AppSettings.DataRoot, "msal-" + clientId + ".bin");
        client = PublicClientApplicationBuilder.Create(clientId).WithAuthority($"https://login.microsoftonline.com/{tenant}").WithRedirectUri("http://localhost").Build();
        client.UserTokenCache.SetBeforeAccess(args => {
            CacheGate.Wait();
            try {
                cacheLock = new FileStream(cachePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (File.Exists(cachePath)) args.TokenCache.DeserializeMsalV3(ProtectedData.Unprotect(File.ReadAllBytes(cachePath), null, DataProtectionScope.CurrentUser));
            }
            catch { cacheLock?.Dispose(); cacheLock = null; CacheGate.Release(); throw; }
        });
        client.UserTokenCache.SetAfterAccess(args => {
            try {
                if (args.HasStateChanged) {
                    var temp = cachePath + ".tmp";
                    File.WriteAllBytes(temp, ProtectedData.Protect(args.TokenCache.SerializeMsalV3(), null, DataProtectionScope.CurrentUser));
                    File.Move(temp, cachePath, true);
                }
            } finally { cacheLock?.Dispose(); cacheLock = null; CacheGate.Release(); }
        });
    }
    public async Task<string> Connect(CancellationToken ct = default)
    {
        AppLog.Write("auth.begin");
        var result = await client.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync(ct);
        AppLog.Write("auth.connected"); return result.Account.HomeAccountId.Identifier;
    }
    public async Task<string> GetToken(CancellationToken ct = default)
    {
        var profile = AppSettings.Load().Profile;
        var account = (await client.GetAccountsAsync()).FirstOrDefault(x => x.HomeAccountId.Identifier == profile);
        if (account == null) throw new InvalidOperationException("Connect Microsoft To Do in Settings to continue syncing.");
        try { return (await client.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct)).AccessToken; }
        catch (MsalUiRequiredException) { throw new InvalidOperationException("Microsoft needs you to sign in again. Open Settings and select Connect Microsoft To Do. Your local changes are safe."); }
    }
    public async Task Disconnect()
    {
        foreach (var account in await client.GetAccountsAsync()) await client.RemoveAsync(account);
        AppLog.Write("auth.disconnected");
    }
}
