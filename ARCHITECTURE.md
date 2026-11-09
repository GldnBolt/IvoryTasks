# Architecture

```text
Samsung Reminder ↔ Microsoft To Do ↔ Microsoft Graph v1.0
                                           ↕
                                   MSAL + SyncEngine
                                           ↕
                         Per-account SQLite (WAL, migration v1)
                             ↙                         ↘
                     WinUI 3 desktop              COM widget provider
                          ↕                              ↕
                Windows notifications/tray       Windows Widgets Board
```

Samsung owns the phone-to-Microsoft bridge. Microsoft Graph is the sole remote API. The app uses a delegated public-client OAuth flow with PKCE. DPAPI protects the persistent MSAL cache. No backend, password handling, secret, or Samsung private API is involved.

## Projects

- **Core:** typed immutable task records, lists, queries, conflict records, SQLite repository, and JSON Adaptive Cards. SQLite uses WAL, foreign keys, a unique compound remote identity, an index on list ID, and explicit schema migration with `PRAGMA user_version`. Unknown future schema versions fail safely. Records preserve nullable fields and Graph recurrence JSON.
- **Sync:** account settings, structured logs, MSAL, `IMicrosoftTodoService`, Graph HTTP adapter, and `SyncEngine`. UI code never sends Graph requests directly.
- **Desktop:** WinUI native controls and restrained warm neutral themes. A single-instance AppLifecycle entry point redirects protocol activation. Task details write through optimistic revision checks. Windows scheduled toasts, optional WinForms NotifyIcon, and package StartupTask provide Windows integration.
- **Widget:** separate COM local server implementing `IWidgetProvider`; package app-extension registration enables discovery. Adaptive Cards carry task IDs, revisions, and a profile fingerprint. Callback arguments are copied before leaving their valid lifetime. Provider state is synchronized, restored from WidgetManager, and refreshed only while active. It shares the profile's SQLite data, not an invented remote cache.

## Data and durability

Each task/list is serialized as typed JSON inside SQLite, with explicit indexed identity columns. Settings/delta checkpoints and durable conflicts have their own tables. Local saves transact the revision read and write. Local deletes become tombstones, allowing offline deletion to reach Microsoft later. Widget edits and desktop edits reject stale revisions. Account IDs are hashed for database filenames. Demo tasks live only in `demo.db`.

The UI retains a recoverable local version throughout sync. Auth and network failures do not recreate the database or clear pending changes. Logs exclude request content and credentials. Atomic settings/token-cache replacement avoids partially written JSON/cache files. Backups must account for SQLite WAL files.

## Synchronization algorithm

1. Acquire a profile-specific cross-process file lock; only one engine syncs a profile at a time.
2. Convert interrupted `Uploading` task states into reviewable conflicts. A create might have succeeded remotely even if its response was lost.
3. Fetch all list pages, match by remote ID, and mark vanished remote lists unavailable without deleting local data.
4. Create local lists with a durable uncertain-create guard. Failed ambiguous requests require user reconciliation; they are never blindly repeated.
5. For every available list, pull all delta pages using the saved server-provided URL. Apply changes idempotently using `(remoteListId, remoteId)` uniqueness. Save the final delta checkpoint only after all pages are applied. Expired checkpoints trigger a full rescan and deletion reconciliation.
6. If local pending changes and the remote baseline both changed, preserve both records in `conflicts`; no last-writer-wins deletion occurs. Remote tombstones conflicting with local edits are also reviewable.
7. Push pending creates, updates, completions, and tombstones. PATCH/DELETE include an ETag when returned by Graph. Precondition/missing-item errors trigger reconciliation. POST requests are not retried automatically. HTTP retry is bounded and limited to safe methods.
8. Save remote identity/baseline after success. An edit made during an upload retains its values and remains pending, with the new remote baseline. Record completion time and expose pending/conflict counts.

Graph does not offer an atomic local/remote transaction or a general idempotency key for task creation. The uncertain-create state deliberately favors user review over duplicate creation. Services without an ETag provide weaker race protection: pull-before-push detects observed concurrent edits, but cannot eliminate a race after the pull. Keep conflict evidence and perform live race tests before relying on synchronization for critical data.

## Windows boundaries

The MSIX carries protocol, startup, COM, and widget app-extension declarations. All startup/tray/background behavior is opt-in. Scheduled notifications require package identity and an actual future reminder. The main app reconciles schedules and syncs while running. The widget can edit cached tasks without network or auth and opens the main app for a remote refresh. Active widget updates poll local SQLite every 30 seconds; no server or busy network polling is required.

The official Widgets host renders Adaptive Cards and decides size/layout, supported actions, theme, lifecycle, and availability. A compact native floating window is optional scope and has not been supplied. Widget actions include supported Execute callbacks and OpenUrl protocol fallbacks.

## Verification boundaries

Automated tests inject Graph/token services and exercise SQLite on disk. They cover data durability, conflicts, offline changes, idempotent pulls/deletes, interrupted POST recovery, completion, concurrent local edits, delta expiry, token-host restrictions, recurrence/date mapping, and card JSON generation. Live OAuth consent, Graph/Samsung propagation, Widgets host activation/actions, notification delivery, and startup behavior require real Windows/account acceptance tests. See `VALIDATION.md` for current evidence.
