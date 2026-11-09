# Validation and release gates

This is a built development application, not a fully verified production release. The user elected to keep the portable build and unsigned MSIX, and has no Microsoft Entra client registration yet. Do not label the remaining account/device checks as passed.

## Automated evidence

`dotnet test tests/Ivory.Tests/Ivory.Tests.csproj -c Release`: **30 passed, 0 failed** on this Windows machine.

Coverage includes:

- Create/edit/complete/uncomplete/delete, on-disk persistence after reopening, schema initialization/reopening, future-schema protection, foreign-key validation, title validation, and stale revision rejection.
- Date boundaries, title/notes/list queries, remote-identity uniqueness, and preservation/resolution of both conflict versions.
- Remote pulls, local pushes, completion in both directions, local/remote deletion, offline pending changes, duplicate prevention, interrupted creation with no repeated POST, concurrent edit during upload, removed lists, expired delta checkpoints, and interrupted multi-page delta replay.
- Injected token acquisition, auth failure preventing HTTP calls, unauthorized error sanitization, Graph-host restrictions, idempotent remote delete, and deferral of long Retry-After responses.
- Graph due date/recurrence mapping and Adaptive Card generation, actions, escaping, completed-task exclusion, private-content hiding, and priority/size limits.

The tests use fake Graph and token services. No Microsoft account or Samsung phone was accessed.

## Build and packaging evidence

- WinUI desktop and COM provider Debug builds: **succeeded with zero warnings/errors**.
- Both executables: **published self-contained for Windows x64**. Final runtime is explicitly pinned to **8.0.33**, avoiding the installed SDK's older default runtime.
- Windows SDK MakeAppx: **manifest validation and MSIX package creation succeeded**.
- Package is intentionally **unsigned**. No signing keys or credentials are shipped.
- Actual unpackaged app launch was observed with the native Windows window and accessible controls. Dark theme, task lists, task rows, search filtering, and All tasks rendering were observed. A local test task persisted across process restart. Broader UI acceptance is still required.
- First startup exposed a WinUI resource initialization issue; restoring the XAML resource build pipeline fixed it.
- Automated sync testing exposed an expired-delta restart bug; it was fixed and now has regression coverage.
- Development package registration was attempted and **blocked by Windows with 0x80073CFF** (no developer license/sideloading policy). Windows security settings were not changed. The user chose to defer registration.

## Outstanding acceptance checks

| Area | State / required next check |
|---|---|
| Microsoft browser login and consent | Requires a public-client Entra client ID and user sign-in |
| Live Graph list/task CRUD and delta behavior | Requires authenticated account; mock coverage passes |
| Samsung → To Do → desktop and completion back to phone | Requires supported Samsung phone/account; setup is documented |
| Widget discovery and COM activation | Source and manifest built; host test blocked by package registration |
| Widget sizes, rendering, completion/open/refresh | JSON tests pass; actual Widgets Board acceptance pending |
| Notifications | Windows scheduling implemented; actual delivery/cancellation pending package installation |
| Tray menu / background lifecycle | Implemented; full interactive acceptance pending |
| Startup opt-in | Implemented, manifest disabled by default; live registration check pending |
| Light/System theme, narrow layout, keyboard flow, scaling | Implemented; complete interactive/accessibility review pending |
| Trusted installer | Requires trusted signing certificate or Microsoft Store signing |

## Live acceptance procedure

1. Install a trusted signed package or register in an authorized Developer Mode environment. Launch from Start.
2. In the isolated demo, create and edit a task, set priority/due/reminder/notes, create a custom list, search, sort, complete/uncomplete, delete, restart, and verify persistence. Test 100%/150%/200% text scaling and a narrow window. Check Light, Dark, and System themes and all documented shortcuts.
3. Test tray Open/Quick Add/Today/Sync/Settings/Quit, window-close background behavior, and explicit startup enable/disable. Confirm startup is never silently enabled.
4. Add a reminder for a future time; verify exactly one Windows toast, then reschedule/complete/delete and verify cancellation. Test with the app closed after scheduling and with Windows Do Not Disturb.
5. Configure the Entra registration described in README. Connect and consent. Sync real lists and tasks. Verify create/edit/complete/uncomplete/delete in Microsoft To Do.
6. Edit while offline, restart offline, reconnect and sync. Modify the same task remotely and locally, verify the conflict review, and resolve each direction. Disconnect/reconnect and verify account data isolation.
7. Enable Samsung's Microsoft To Do bridge; create a task in its Microsoft-synced destination. Verify phone → To Do → Ivory and completion back to phone. Exercise due date, reminder, and recurrence mappings; record Samsung-specific losses.
8. Pin each widget size. Verify counts/current tasks, completion persistence, opening the selected task, refresh launching/activating the app, changing accounts, disabled-content mode, and important-only mode. Restart the widget host and verify state restoration.

## Known scope limits

- No optional floating desktop mini-window.
- No direct Samsung API or guessed Samsung-origin badges.
- Local/demo recurrence stores a pattern; Microsoft To Do advances recurring tasks after sync.
- No remote cross-list task moves, checklist/attachment editor, background cloud sync while the app is fully quit, or encrypted SQLite task content.
- Conflict review is intentionally conservative; ambiguous creates need user reconciliation to avoid silent duplicates.
- Pull-before-push and conditional ETags reduce races; Graph does not provide a distributed transaction. Live concurrency checks remain necessary.
- Up to the nearest 64 future reminders are scheduled. While the main app is quit, later remote/widget edits cannot immediately reconcile an already scheduled toast.
- The package preview image is a static store preview illustration; the actual widget content comes from live local SQLite through `IWidgetProvider`.

## Next improvements

Complete the live acceptance matrix, trusted signing, and migration from the near-end-of-support .NET 8 runtime to a supported LTS release first. Then consider a virtualized task list for very large datasets, richer conflict diffs, export/backup controls, per-field merge baselines, WAM broker integration, and the optional floating mini-window.
