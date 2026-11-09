# Ivory Tasks

A Windows-first task manager with a native WinUI 3 desktop UI, SQLite persistence, Microsoft To Do synchronization, and an official Windows Widgets Board COM provider. The local demo is usable without Microsoft configuration and is isolated from each account's data.

## Installation

Requires Windows 11 22H2 or newer, x64. The published app bundles .NET and Windows App SDK. Keep all files alongside the executable.

- For local use, extract `dist/IvoryTasks-portable-x64.zip` and launch `App/IvoryTasks.exe`, or use `dist/Launch.ps1`. This supports task management, Microsoft sign-in, and synchronization. Package identity is required for startup registration, scheduled notifications, protocol links, and widget discovery.
- Install a **trusted, signed** `dist/IvoryTasks-1.0.0-x64.msix` by double-clicking it, or run `dist/Install.ps1`. The installer checks signature trust and does not alter Windows security settings.
- The build script creates an **unsigned development package**. Sign with your organization's code-signing certificate (subject `CN=IvoryTasks`, matching the manifest), or submit through Microsoft Store for distribution. `scripts/Sign.ps1 -CertificateThumbprint ...` signs using a certificate already in your certificate store. No signing key is shipped.
- Developers with Developer Mode already enabled can register the staging package with `Add-AppxPackage -Register <absolute staging path>/AppxManifest.xml`. Keep that staging folder in place. Do not confuse development registration with a distributable signed installer.

## Development setup

Use Visual Studio 2022, a .NET 8 SDK, Windows SDK 22621 or newer, and the WinUI development tools. This environment was inspected before implementation: SDKs 8.0.202/8.0.206, runtime 8.0.33, Windows SDKs through 26100, Visual Studio 2022 Community, Node 22.18.0/npm 10.9.3. There was no existing scaffold or repository content. Node is not required to build Ivory.

Pinned NuGet dependencies include Windows App SDK 2.6.4, Windows SDK .NET projections 10.0.22621.38, Microsoft.Data.Sqlite 8.0.22, and MSAL.NET 4.77.1. Self-contained publishing explicitly pins runtime 8.0.33 so an older SDK cannot silently bundle 8.0.6. Lockfiles are generated and should be committed. .NET 8 support ends November 10, 2026; migrate to a supported LTS SDK/runtime before production distribution and rerun the test and UI checks when updating SDK packages.

## Build instructions

From this directory in PowerShell:

```powershell
dotnet test tests/Ivory.Tests/Ivory.Tests.csproj
dotnet build src/Ivory.Desktop/Ivory.Desktop.csproj -p:Platform=x64
dotnet build src/Ivory.Widget/Ivory.Widget.csproj -p:Platform=x64
./scripts/PrepareAssets.ps1
./scripts/Build.ps1
```

`Build.ps1` tests, self-contained publishes both executables, validates and packs an MSIX with Windows SDK MakeAppx, copies installation instructions, and prints its SHA-256 hash. Each build uses a fresh staging folder so stale publish files cannot contaminate a new package. Publish is intentionally not trimmed or combined into a single file.

## Microsoft Entra App Registration

1. Open Microsoft Entra admin center → App registrations → New registration.
2. Choose **Accounts in any organizational directory and personal Microsoft accounts** for a general-purpose app (or personal accounts only for personal use).
3. Under Authentication → Add a platform → **Mobile and desktop applications**, register `http://localhost` as the redirect URI. This is the system-browser loopback redirect handled by MSAL.
4. Enable public-client flows if your tenant configuration requires it. Do not add a web platform, implicit grants, or a client secret.
5. Under API permissions, add Microsoft Graph **delegated `Tasks.ReadWrite`**. Remove unused default permissions such as `User.Read` if you do not need them. Your organization may require administrator consent.
6. Copy Application (client) ID into Ivory Settings. Tenant defaults to `common`; `consumers`, `organizations`, or a tenant GUID are supported.

The client ID and tenant are public configuration. `.env.example` documents optional process environment variables; the application does not load `.env` files automatically. Settings provide the normal configuration route.

## Required permissions

Only delegated Graph `Tasks.ReadWrite` is requested for list/task read, create, update, completion, and deletion. MSAL manages the standard identity/offline scopes needed for sign-in and refresh. No application permissions, passwords, or desktop client secrets are used.

## Microsoft To Do connection

Open Settings → enter client ID → **Connect Microsoft To Do**. Sign in and consent in Microsoft's browser UI. Close Settings and select **Sync now**. Each Microsoft home-account identifier maps to a separate hashed database filename. Connecting never uploads demo tasks. Disconnect removes cached MSAL accounts, cancels reminders from that account, and switches to the isolated local demo. It retains account databases for offline recovery.

Sync runs on demand and at a configurable 1–120 minute interval while Ivory is running. A 30-second local refresh reflects widget changes. Enable the optional tray setting to keep running when closing the window; use tray → Quit to exit. Startup is opt-in and disabled in the package manifest.

## Samsung Reminder setup

On a supported Samsung phone, open **Reminder → Menu/More → Settings → Sync with Microsoft To Do** and sign in with the same Microsoft account. Save/move reminders into the Microsoft-synced destination in Reminder. Verify that the task appears in Microsoft To Do first. Then synchronize Ivory.

The supported route is **Samsung Reminder ↔ Microsoft To Do ↔ Microsoft Graph ↔ Ivory**. Ivory never accesses Samsung directly. Samsung controls which reminders and properties cross that bridge. Images, location reminders, Samsung categories, attachments, and some repeat/reminder details may not map. Graph cannot reliably identify Samsung-originated tasks, so imported tasks are labeled Microsoft To Do, not guessed as Samsung. Existing Graph recurrence JSON and HTML note content type are retained. Attachments, checklist items, linked resources, and other unedited fields are not sent in task patches.

Test the bridge with a noncritical task: create it in Samsung's Microsoft-synced list, verify it in To Do and Ivory, complete it in Ivory and sync, then verify in To Do and Samsung. Repeat an edit from the phone. These account/device checks require the owner's consent and phone; automated mocks are not proof that the Samsung bridge works for your account.

## Widget setup

Install/register the MSIX, launch Ivory once, then open Windows Widgets and add **Ivory · My tasks**. The package declares `com.microsoft.windows.widgets` and a real COM activation class implementing `IWidgetProvider`. This is an Adaptive Card widget, not a webpage or floating window.

Small/medium/large sizes show up to 1/3/6 tasks, ordered overdue → today → important → upcoming. Counts cover today, overdue, and important tasks. Completion writes to the same database with revision checking; the main app pushes that change during its next sync. Open task/app uses the registered `ivorytasks:` protocol. Refresh updates the local card and opens/activates Ivory to synchronize. Active cards refresh every 30 seconds. The provider can exit after ten inactive minutes; Windows reactivates it when needed. Widget settings can hide all task content or show important tasks only. After an account change, stale-card completion is rejected.

The optional floating desktop mini-window is not implemented; the official Widgets Board provider is the supplied widget surface.

## Everyday use

- Enter a title in quick add and press Enter. In Today, new tasks default to today's due date; other views leave the date unset.
- Select a task to edit title, notes, list (before remote creation), priority, due date, reminder date/time, and repeat pattern. Today/Tomorrow/Clear shortcuts are available.
- Tasks with an existing remote identity remain in their Microsoft list; cross-list moves are deliberately unavailable because Graph can change task identity during a move.
- Search title/notes/list name. Sort by date, priority, modification time, or title. All tasks means all active tasks; Completed is a separate view.
- Keyboard: Ctrl+N quick add; Ctrl+F search; Ctrl+Shift+S sync; Esc close details; Space on a focused task or its checkbox toggles completion.
- Repeat settings serialize a Graph recurrence; Microsoft To Do owns recurrence advancement after synchronization. Local demo recurrence stores the pattern but does not generate future instances.

## Troubleshooting

- **Cannot sign in:** check client ID, supported account types, loopback redirect, tenant, and delegated consent. Reconnect when the UI reports that Microsoft needs another sign-in.
- **Offline / rate limited:** task changes remain local. GET/PATCH/DELETE retry bounded transient errors and honor short Retry-After values. A longer Retry-After is persisted per account and blocks manual/automatic sync until that deadline. No POST is blindly retried.
- **Conflict:** Settings preserves the local and Microsoft versions. Explicitly choose which to keep. A task whose create outcome is unknown requires checking To Do first; keeping local could create a duplicate. Deleted remote lists preserve local data. Recover local tasks into an available list as appropriate.
- **Uncertain list create:** Settings can associate local tasks with an existing Microsoft list, or retry only after you confirm the list was not created. This avoids unsafe automatic duplicate list creation.
- **Widget absent:** check Windows 11 Widgets/Web Experience Pack availability, package registration, and that the package contains its Assets. Restart the Widgets host or sign out/in after development registration changes. Windows host policy controls provider discovery.
- **Notifications:** install the package. Future reminders are registered with Windows (up to the nearest 64). Completed/deleted/rescheduled task reminders are reconciled while the main app is running. Keep Ivory running for prompt cancellation of changes made by the widget or remote devices. Windows notification policy, sleep, and Do Not Disturb affect delivery. Past reminders do not cause repeated notification bursts.
- **Database error:** the UI surfaces the error without recreating the database. Close Ivory and its widget provider before backing up the database, or use SQLite's online backup tool. Preserve WAL/SHM files when copying a live database.
- Data: `%LOCALAPPDATA%/IvoryTasks/`. Logs are per-process daily JSONL, retained for 14 days. They record event names, exception types, HRESULTs, and HTTP status codes, without task text, account IDs, tokens, or response bodies.

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md). `src/Ivory.Core` contains models, queries, SQLite migration/persistence, and widget-card generation. `src/Ivory.Sync` contains auth, Graph mapping/HTTP, sync, settings, and structured logging. `src/Ivory.Desktop` contains the native UI and Windows integration. `src/Ivory.Widget` is the separate out-of-process COM provider. `tests/Ivory.Tests` exercises persistence, sync, Graph boundaries, and card generation.

## Security considerations

MSAL performs authorization code + PKCE in the system browser. Token cache bytes are encrypted using Windows DPAPI for the current user, written atomically, and protected with process/cross-process serialization. There is no client secret and no credential logging. Graph continuation links are restricted to HTTPS Microsoft Graph v1.0 To Do endpoints before a bearer token is added. Account databases are isolated from demo data and other accounts. Settings contain public app configuration and account linkage, not OAuth tokens. SQLite task content is not encrypted at rest; it inherits the user's Windows profile permissions and disk encryption. Disconnect does not erase local task caches. Do not redistribute private data, caches, logs, or signing keys with builds.

## Validation and release status

See [VALIDATION.md](VALIDATION.md) for actual test/build results and remaining release gates. Successful compilation and mock sync tests do not establish live Microsoft/Samsung interoperability or Widgets Board behavior on every host. A distributable release requires a trusted signature and completion of the live account/device acceptance checks.

Official integration references: [Graph To Do delta](https://learn.microsoft.com/en-us/graph/api/todotask-delta?view=graph-rest-1.0), [MSAL interactive authentication](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/acquiring-tokens-interactively), [C# Windows widget providers](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs), [Windows App SDK self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps), and [Samsung Reminder/Microsoft synchronization](https://www.samsung.com/au/support/mobile-devices/sync-reminders-with-microsoft-account/).
