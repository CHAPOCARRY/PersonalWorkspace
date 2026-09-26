# PersonalWorkspace architecture

The foundation sections below record the approved Phase 0 design. **Phase 1: Local Profiles**, **Phase 2: Task Core**, **Phase 3: Tags and Spaces**, **Phase 4: Calendar and Events**, and **Phase 5: Subtasks and Task Dependencies** extend it and supersede historical statements about empty workspaces, placeholders, and multiple application instances.

## Scope

PersonalWorkspace is an offline Windows x64 desktop application built with C#, .NET 10, WinUI 3, MVVM, dependency injection, and SQLite. There are no cloud dependencies, accounts, subscriptions, remote databases, analytics clients, or telemetry services in the application. NuGet access is needed for development restore, not application use. The .NET CLI's own telemetry can be disabled with `DOTNET_CLI_TELEMETRY_OPTOUT=1`.

Tasks, Calendar, Trackers, Journal, Pages, Lists, Today, Archived, Trash, and Settings are navigation placeholders. SPACES is a disabled section. Search and Add are disabled controls. Phase 0 introduced no profile models, tables, or workflows; these are added below in Phase 1.

## Projects and dependency direction

```text
PersonalWorkspace.sln
src/
  PersonalWorkspace.App/       -> Core, Data, Services
    Infrastructure/           Composition root and native window persistence
    Resources/                Design tokens and theme resources
    ViewModels/               Shell presentation state and commands
    Views/                    Reusable placeholder view
  PersonalWorkspace.Core/     -> no project/package dependencies
  PersonalWorkspace.Data/     -> Core
    Migrations/               Ordered, explicit schema changes
  PersonalWorkspace.Services/ -> Core
tests/
  PersonalWorkspace.Tests/    -> Data, Services (Core transitively)
docs/
  ARCHITECTURE.md
```

Core owns interfaces and small shared records. It references neither WinUI nor SQLite. Data implements persistence contracts. Services supplies paths and navigation without depending on Data or UI. App is the composition boundary and contains Windows-specific presentation infrastructure. All dependencies flow inward; no circular references exist.

## Startup and lifetime

`App.OnLaunched` establishes the local directories and structured logger, builds the validated DI container, initializes the database, loads shell/window preferences, then resolves and activates the main window. Container resolution is limited to this composition boundary; injected classes never retrieve dependencies through a service locator.

The app is unpackaged and bundles the Windows App SDK runtime. Development output uses the installed .NET runtime; self-contained publish also includes .NET. Windows x64 is the only configured target. The stable Windows App SDK 1.8 line is deliberately pinned, rather than following floating/pre-release versions.

The top bar is 48 logical pixels. The native NavigationView pane uses 240/64 logical pixels and the same placeholder component for each destination. Code-behind only bridges UI events, synchronizes selection, and attaches native window behavior. MVVM Toolkit supplies observable properties and commands. Sidebar changes are persisted before changing the displayed state; failed writes surface a friendly inline message.

## Navigation

`NavigationRoute` carries a destination and optional entity ID. `INavigationService` retains the current route and raises a change notification. The shell maps current placeholder routes to title/description. Future phases can add a destination/view registry and entity-specific view models without changing this contract. Navigation history, deep links, unknown-route error pages, and entity loading are intentionally deferred.

## Paths and storage

`ApplicationPaths` obtains Windows Local Application Data with `Environment.SpecialFolder.LocalApplicationData`. It creates `PersonalWorkspace/app.db`, `Logs/`, `Backups/`, and `Profiles/`; no username or repository path is embedded. Tests inject a unique temporary parent directory.

In Phase 0, only `app.db` existed. `SchemaMigrations` records version, name, and UTC application time. Migration 1 creates `AppSettings` with a unique text primary key, JSON value, and UTC update timestamp. Phase 1 keeps this shipped migration unchanged and adds profile/workspace storage below.

The migration catalog is the sole source of application schema SQL. The runner sorts positive unique versions, obtains an immediate SQLite write transaction, creates/reads the ledger, executes pending migrations, records them, then commits. Failure rolls back the entire pending batch and is logged. Already-applied versions are skipped. A database containing unknown versions is rejected to avoid opening a newer schema with older code. Add migrations; do not edit already-shipped ones. Down migrations are not supported.

Connections enable foreign keys, use a ten-second busy timeout, and are short-lived and unpooled. SQLite commands accept cancellation tokens, though Microsoft.Data.Sqlite performs underlying disk operations synchronously. Phase 0 work is small; larger future workloads must consider background execution and contention explicitly.

The settings implementation uses parameterized SQL and a single atomic upsert. Missing, JSON-null, malformed, or incompatible values return the supplied default; malformed/incompatible values also generate a warning without logging the JSON. Infrastructure errors propagate after logging. There is no silent reset of a corrupt database.

## Window and theme behavior

Normal window width/height and maximized state are persisted on orderly close. Stored dimensions are validated, clamped to the current display work area, and centered. Coordinates are deliberately not persisted, avoiding restoration onto disconnected monitors. The native controller tracks restored size separately from maximized size. Forced process termination need not save current window size.

One explicit light theme is implemented. Semantic brushes live in a theme dictionary; spacing, typography, layout and common styles are centralized in `Resources/DesignTokens.xaml`. WinUI's native font defaults provide Windows typography. A dark theme and theme selector remain future work. Controls retain native keyboard/focus behavior, icon buttons have automation names, and errors have a live-region hint.

## Logging and failure handling

Serilog writes structured JSON lines into the local Logs folder. Startup, orderly shutdown, database initialization, migration attempts, and infrastructure failures are logged. Files rotate daily and at 5 MB, with 14 files retained. No remote sink is registered. Setting values and entity content are not intentionally logged.

Startup failures display a plain-language window with the log location. Unexpected unhandled UI/process failures are logged and remain fatal; the application does not pretend corrupted state is safe by marking every exception handled. If the local filesystem cannot create the logger, startup also reports the exception to diagnostic Trace and shows a friendly error window; a file log cannot be guaranteed on an unwritable disk. Preference failures are logged; a window-save failure does not prevent exit. Logs are flushed on orderly shutdown.

## Direct NuGet dependencies

| Package | Purpose |
| --- | --- |
| Microsoft.WindowsAppSDK | WinUI 3 controls and native window APIs; bundled runtime |
| Microsoft.Windows.SDK.BuildTools | Windows XAML/resource build tooling |
| CommunityToolkit.Mvvm | Observable properties and asynchronous commands |
| Microsoft.Extensions.DependencyInjection | Constructor injection and central registration |
| Microsoft.Data.Sqlite | SQLite driver and native SQLite dependency |
| SQLitePCLRaw.bundle_e_sqlite3 | Explicit patched native bundle override for the driver's older transitive version |
| Microsoft.Extensions.Logging.Abstractions | Persistence diagnostics without a concrete logging dependency |
| Serilog.Extensions.Logging | Connect Microsoft logging abstractions to Serilog |
| Serilog.Sinks.File | Rotating local structured logs |
| Microsoft.NET.Test.Sdk | Automated test host |
| xunit | Infrastructure assertions and test discovery |
| xunit.runner.visualstudio | Integration with dotnet test and IDEs |

## Verification

Run `dotnet build PersonalWorkspace.sln`, then `dotnet test PersonalWorkspace.sln --no-build`. Infrastructure tests cover creation, the initial schema, idempotency, ordered migrations, rollback, settings writes/reads/upserts/defaults, malformed values, foreign keys, cancellation, entity route identity, and window-size validation. Tests operate only on uniquely named temporary directories.

Manual desktop verification:

1. Launch with `dotnet run --project src/PersonalWorkspace.App --no-build` and confirm all ten navigation entries show the corresponding placeholder. Check top-bar Settings also selects the footer entry.
2. Toggle the sidebar, close, and relaunch. Confirm the saved state and 240/64-pixel layout, icon tooltips, keyboard navigation, and visible SPACES section when expanded.
3. Resize/maximize, close, and relaunch. Verify safe centering and size restoration on different monitor configurations and display scaling settings.
4. Verify Search/Add and the Spaces placeholder cannot perform actions.
5. Inspect `%LOCALAPPDATA%\PersonalWorkspace` for the global database, profile directories (Phase 1), an empty Backups folder, and structured startup/migration/shutdown logs. A second launch must not apply migration 1 again.
6. Test offline launch, narrow windows, high DPI, and screen-reader labels. Visual and accessibility checks require an interactive Windows session.

## Phase 0 limitations

Only x64/unpackaged delivery and one light theme are configured. There is no installer, window-coordinate restoration, navigation history, profile storage, backup behavior, or business functionality. Window geometry and view-model/WinUI bindings require manual integration checks; automated tests intentionally target non-UI infrastructure. Multiple instances can access SQLite safely at the transaction level but do not live-synchronize shell preferences. Schema upgrade backups and single-instance activation can be decided in future phases.

## Phase 1: Local Profiles

### Scope and dependencies

Profiles are freely switchable, isolated local workspaces, with no login, password, authentication, cloud account, or encryption. All business destinations remain Phase 0 placeholders. No business tables or attachment workflows have been introduced.

Dependency direction is unchanged. Core adds the immutable `Profile` record and contracts for profile management, current context, repositories, workspace initialization, and profile files. Services orchestrates lifecycle operations through those contracts; Data supplies SQLite implementations. App adds `ProfilesViewModel` and `ProfileManagementView`, retaining the shell and design tokens. Services now directly references the already-used `Microsoft.Extensions.Logging.Abstractions` package; there are no new package identities or versions.

### Storage layout and schema

```text
%LOCALAPPDATA%/PersonalWorkspace/
  app.db
  application.lock
  Logs/
  Backups/                    (reserved, no backup functionality)
  Profiles/
    {lowercase-guid-D}/
      workspace.db
      attachments/            (directory only)
```

`IApplicationPaths` centralizes all profile paths. GUIDs use the lowercase `D` representation in metadata, settings JSON, and folder names. `FolderName` must equal `Id`; paths derive from the typed ID, never the display name. Rename updates only the name. Profile filesystem deletion checks containment and refuses symbolic links/junctions before recursion.

Global migration **2, Create local profiles**, adds only `Profiles`: `Id`, `Name`, `FolderName`, `CreatedAtUtc`, nullable `LastOpenedAtUtc`, and `SortOrder`. Migration 1's name and SQL are unchanged. The global database contains only application settings, the profile catalog, and its migration ledger. Future workspace entities must be stored in each profile's database, never `app.db`.

Names are trimmed, normalized to Unicode NFC, and compared with `StringComparer.OrdinalIgnoreCase`. Validation rejects blank and duplicate names. A unique SQLite `PROFILE_NAME` collation index also enforces case-insensitive uniqueness across writes; the connection factory registers this collation. External tools that edit the profile catalog must register equivalent collation semantics. `SortOrder` increases on creation and is reserved for future ordering UI.

### Workspace migrations and connection lifetime

The existing migration logic was extracted into `MigrationRunner`; both global and workspace initializers use it. Version validation, ordering, unknown-version rejection, immediate transactions, ledger writes, rollback, and diagnostics retain Phase 0 behavior.

A new workspace currently has only an empty `SchemaMigrations` ledger, established transactionally. Its migration catalog is empty: no fake business migration or table is needed. On opening an existing profile, the initializer uses SQLite read/write mode without creation and checks that the ledger exists. A missing, corrupt, or incompatible workspace fails visibly instead of being replaced with an empty database. Foreign keys remain enabled. Connections are short-lived, unpooled, and disposed before publishing a new current context; selecting a profile does not hold a SQLite connection open.

### Current context and serialization

`ICurrentProfile` exposes the immutable current profile, nullable workspace database path, and a change event. Only the lifecycle service mutates the state holder. ViewModels receive display information and issue commands; they execute no SQL and open no databases. Future Data services can capture the active context for an operation and open short-lived connections to that workspace.

`ProfileService` serializes its operations with an asynchronous semaphore. The desktop application holds an exclusive `application.lock` file handle for its lifetime, preventing another process from using a workspace while it is being deleted. The file can remain on disk after exit; ownership is determined by the handle, which Windows also releases after a crash. A second process shows a friendly already-open message. Activation forwarding is not implemented. This deliberately replaces Phase 0's permissive multi-instance behavior for profile-lifecycle safety.

### Startup and lifecycle

After global initialization, startup retries recorded pending deletions, loads profiles, and reads `LastOpenedProfileId` through the existing settings service. A valid stored ID is reopened automatically. An absent, malformed, or unregistered ID falls back by most recent opening time, then sort order, creation time, and ID. The corrected selection is persisted. With no profiles, the selection is cleared and the UI asks for a name; no default profile is generated.

Creation validates the name, generates an ID, creates the directory and attachments folder, and initializes the workspace. A single global transaction inserts metadata and persists selection. Only then is the current context published. An ordinary initialization/metadata failure attempts to remove newly created files and leaves the previous selection intact.

Switching initializes the target before committing `LastOpenedAtUtc` and `LastOpenedProfileId` together. A failed switch keeps the prior context and setting. Navigation is independent of profile context, so high-level destinations remain selected across switches. Rename updates metadata and any current display state without moving files.

The sidebar's native flyout button shows the current name, available profiles with a check mark, New Profile, and Manage profiles. Management reuses existing theme resources and provides name editing, last-opened information, Open, Rename, and Delete. With no active profile after an opening failure, the same panel exposes the existing profiles and the error so another can be opened. No raw database errors or stack traces are shown.

### Deletion transaction and recovery

The UI requires an explicit confirmation naming the profile and explaining that its workspace and attachments are permanently removed; Cancel is the default action.

1. Resolve the surviving selection. If another workspace must be selected, initialize it before changing anything. If this fails, the requested deletion does not proceed.
2. In one global transaction, remove profile metadata, correct the last-opened setting, update the surviving profile timestamp when switching, and write `profiles.pendingDeletion.{guid} = true` into `AppSettings` using the existing settings writer.
3. Publish the surviving context (or null). No profile-specific connection is retained.
4. Delete exactly the validated GUID folder and descendants. On success remove the pending marker. Cancellation is honored before commit; mandatory cleanup after commit uses no cancellation token.

If the global transaction fails, files and current context remain untouched. If filesystem deletion partially fails or the process stops after commit, metadata is already gone: the app never advertises a partly deleted workspace as a valid profile. The marker survives and startup retries cleanup idempotently, including the case where the folder is already absent. Failed retries are logged and shown as a friendly warning while surviving profiles remain usable. A marker referring to live metadata is treated as an integrity error and is never used to delete that live profile. Pending files are not Trash and cannot be restored through the app.

### Failure behavior and limitations

Infrastructure events log profile IDs, not profile names or attachment content. Validation failures have friendly messages. Workspace failures do not trigger automatic destructive repair. A valid stored profile whose workspace is unreadable opens the management/recovery panel; users may select another profile. Selection falls back automatically only for absent/invalid metadata IDs, not silently for corruption.

SQLite and the filesystem cannot share an atomic transaction. Creation compensation handles ordinary failures, but process termination between directory creation and metadata commit can leave an unregistered GUID folder. It is never opened automatically or deleted by scanning unknown folders; manual inspection is required. Deletion has durable recovery markers and retries instead. Locked files or links/junctions can require manual attention before pending deletion succeeds.

Manual ordering, import/export, authentication, encryption, backups, attachment use, activation forwarding, dark theme, and all Phase 2 business functionality remain out of scope. Future long-running workspace operations must coordinate resource lifetime with profile switching; there are none in Phase 1. The app remains x64/unpackaged.

### Phase 1 verification

Run the full solution build and tests as above. Tests cover the Phase 0 upgrade, migration idempotency, retained settings, creation/paths/ledger, Unicode and case-insensitive validation, rename identity, switch timestamps/settings, startup restoration/fallback, physical database isolation, deletion/current-profile selection, failed initialization/SQL/filesystem operations, and durable deletion retry. Test data uses isolated temporary directories.

Manual acceptance flow:

1. On an unused data directory, launch and create `Tiago`; confirm the shell and sidebar name appear and its GUID folder contains `workspace.db` and `attachments`.
2. Use the selector to create `Work`. Confirm distinct files, then restart: Work should reopen automatically.
3. Switch to Tiago, select a placeholder area such as Calendar, then switch profiles: the area should stay selected. Test the selector with the sidebar collapsed and with keyboard navigation.
4. Rename Tiago to Personal through Manage profiles. Verify its GUID folder does not move. Try blank and differently cased duplicate names.
5. Cancel a deletion and verify no changes. Then confirm deletion of Work; verify only its files/metadata disappear. Restart and confirm Personal reopens.
6. Delete the final test profile and verify the first-profile form returns. Recheck Phase 0 sidebar/window persistence and high-DPI layouts.

The implementation was also exercised through Windows UI Automation for first-run creation, second-profile creation, last-profile restoration after restart, switching, rename, cancellation of deletion, current-profile deletion with fallback, and final-profile deletion. The temporary UI test profiles were removed through the application's confirmation dialogs.

## Phase 2: Task Core

### Domain and workspace schema

`WorkspaceItem` is the shared GUID identity and metadata record: item type, title, UTC creation/update timestamps, and nullable UTC archive/delete timestamps. `TaskItem` composes that identity with description, status, priority, and optional `DateOnly` scheduled date. The C# name avoids ambiguity with `System.Threading.Tasks.Task`. Only the Task item type is defined; future item types can extend the enum without adding unrelated fields now.

The first **workspace migration, version 1: Create task core**, adds `WorkspaceItems` and `Tasks`. It does not alter global migrations or the shared ledger runner. Phase 1 workspaces upgrade through their existing empty ledger when opened. New profiles receive the same migration. Global `app.db` remains settings and profile metadata only.

`Tasks.ItemId` is both primary key and a foreign key to `WorkspaceItems.Id`, with `ON DELETE CASCADE`. There can be only one task row for an item. All application creation/update/deletion paths maintain both rows transactionally. CHECK constraints reject invalid status/priority values and blank SQL titles. Scheduled dates use invariant `yyyy-MM-dd` text; domain validation uses `DateOnly`, so scheduling never stores an artificial midnight UTC timestamp.

The composite index `(ItemType, DeletedAtUtc, ArchivedAtUtc)` supports active/archived/trash queries. A partial ScheduledDate index covers dated tasks and Today. There is no separate status/priority index because Phase 2 has no query filtering on those fields; adding unused indexes would add write overhead without a current benefit.

### Rules and transaction boundaries

`TaskService` owns title trimming/validation, enum validation, status/scheduling actions, timestamp updates, duplication, and lifecycle rules. Duplicate titles are allowed. New tasks default to ToDo, priority None, and no scheduled date. ToDo, Doing, and Blocked are incomplete; Done alone means completed. There is no separate completion flag. Moving away from Done reopens the task.

`SqliteTaskRepository` uses parameterized SQL and short-lived, unpooled connections with foreign keys enabled. A write transaction surrounds both inserts, both updates, or a permanent-delete cascade. Updates read the existing record inside the immediate transaction before applying the service mutation. A failure rolls back the entire operation; task insert failures cannot strand an item row. Permanent deletion is allowed only from Trash and requires the UI's explicit confirmation naming the task.

Reads never modify timestamps. Meaningful changes advance UpdatedAtUtc; no-op saves/status changes retain it. The timestamp advances by at least one tick if the local clock has not advanced, while remaining UTC. Creation initializes both timestamps. Duplication creates a new identity and fresh timestamps, copies title/description/priority/scheduled date, resets status to ToDo, and clears archive/delete state.

Archive and Trash are independent of status. Active/Today exclude both archived and deleted items. Archived includes archived items only when not deleted. Trash includes deleted items regardless of prior archive state. Restoring from archive clears ArchivedAtUtc. Restoring from Trash clears both lifecycle timestamps so the task returns to the active Library, retaining status and scheduled date. Editing a deleted task requires restoration first. There is no retention timer or task audit history.

### Workspace resolution and profile isolation

Task service calls resolve the current injected profile and workspace path only after entering the shared `IWorkspaceOperationGate`. The profile service also enters this gate around lifecycle operations, ensuring switching, initialization, and profile deletion wait for in-flight task operations to release their SQLite connections. This is the only necessary extension to Phase 1 lifecycle coordination; its storage/deletion strategy is unchanged.

Existing-task UI actions carry a `TaskReference` containing the originating profile ID and task ID; creation captures the editor's profile ID. The service verifies the expected ID against the current context before any workspace access. Thus a queued or stale action fails safely rather than writing into the newly selected profile. No current profile means no workspace access. The repository receives an operation-scoped context rather than resolving mutable global state halfway through a transaction.

`TaskWorkspaceViewModel` listens to current-profile changes, clears rows/draft/detail/filter immediately, and reloads the current high-level area. A task-detail/quick-create route returns to Tasks when switching profiles. A revision counter discards delayed results from old contexts or old navigation requests. There is no cross-profile task cache. Profile renames leave the same workspace context intact.

### Navigation and presentation

Tasks, Today, Archived, and Trash share the task view and records. `Task/{guid}` uses the existing route's optional entity ID for dedicated detail; `Tasks/new` opens the small quick-create form. The Library has a culture-aware, case-insensitive in-memory title filter only; this does not affect other areas or implement global search.

Today queries the current local calendar date via an injected `TimeProvider`, not UTC. Completed tasks remain visible on their scheduled date. The shell checks for local date rollover once a minute while Today is visible and reloads when the date changes. No events, widgets, carry-over, or recurrence logic is added.

Quick create exposes title, priority, and optional date. Detail additionally edits status and plain multiline description and displays local, culture-formatted creation/modification metadata. Saves are explicit. Navigation/profile switching discards unsaved drafts. CalendarDatePicker uses a small UI-only nullable-date bridge: the native control otherwise displayed its minimum date for a null reflection binding. The bridge preserves an empty selection and maps only the chosen calendar date to the domain.

Row actions use native flyouts; permanent delete uses a ContentDialog with Cancel as default. Existing design tokens provide layout, typography, and colors, with a few centralized task-layout tokens. Top bar, sidebar, and profile selector retain their structure; Add now opens task creation. Calendar, Trackers, Journal, Pages, Lists, Spaces, and global Search remain placeholders.

Infrastructure failures are logged with profile IDs and shown as friendly messages. Titles/descriptions are not intentionally logged; normal edits do not emit information-level activity logs. Failed writes leave the editor available for correction/retry. The app delays orderly close while task/profile operations are busy.

### Verification and limitations

Tests cover legacy workspace upgrades and idempotency; global/workspace schema separation; atomic insert/update/cascade rollback; defaults/validation/duplicate titles; reads and meaningful/no-op timestamps; status transitions; scheduling/unscheduling across a DST date; Today when local date differs from UTC; archive/trash/restore; duplication; profile isolation/stale-write rejection; no-profile behavior; switching during an in-flight write; and clearing/ignoring stale presentation results. UI-independent task view-model sources are linked into the existing test project with the already-used MVVM Toolkit dependency. No new package identity/version was introduced.

Manual acceptance:

1. In Personal, create `Comprar leite` without a date: Library yes, Today no. Open detail, schedule today, save, and mark Done from Today. Verify Library shows the same Done task.
2. Create `Jogar futebol` without a date and complete it. Duplicate it: the copy must have a new identity and ToDo status.
3. Create `Preparar projeto`, priority High, scheduled tomorrow. Verify it is absent from Today; archive it, restore it from Archived, and verify its properties remain intact.
4. Move a task to Trash, restore it, then test permanent deletion with both Cancel and confirmation. Inspect that only the confirmed task is removed.
5. Switch to Testing and create a different task. Switch back: only Personal's tasks return. Restart and verify profile selection and tasks persist.
6. Verify detail editing, clear-date behavior, keyboard focus, narrow windows, and Windows scaling. Calendar must remain a placeholder.

Phase 2 intentionally has no advanced filtering, paging, sorting UI, unsaved-change prompt, rich text, attachment handling, subtasks, dependencies, recurrence, carry-over, events, calendar UI, boards, notifications, or later-phase entities. Library queries currently load the selected collection in memory; rows use a virtualized native ListView. Very large workspaces will need bounded queries in a future phase. SQLite disk calls remain synchronous internally despite asynchronous contracts; Phase 2 adds no background sync or scheduler.

## Phase 3: Tags and Spaces

### Models, migration, and relationships

Tags and Spaces are independent, flat organization entities inside each profile's `workspace.db`. A Tag has a GUID, name, optional color, and UTC creation/update timestamps. A Space additionally has description, icon text, sort order, and nullable UTC archive timestamp. Tags classify items; Spaces organize them. Tags are not children of Spaces, and neither entity owns a Task.

Workspace migration **2: Create tags and spaces** adds `Tags`, `Spaces`, `ItemTags`, and `ItemSpaces`. Workspace migration 1, both global migrations, and the shared ledger runner remain unchanged. Existing Phase 2 workspaces upgrade when opened; new profiles apply both migrations. No organization data is stored in `app.db`.

Each relationship has a composite primary key `(ItemId, TagId)` or `(ItemId, SpaceId)` and foreign keys with delete cascades. Item IDs reference `WorkspaceItems`, allowing future item types to reuse the same organization infrastructure without task-specific ownership. Reverse indexes support entity-to-item lookups and cascade deletion. A Task assigned to Personal and Training remains one `WorkspaceItems` row and one `Tasks` row. Removing an assignment never moves or clones it. Physically deleting an item cascades its links but preserves the Tags and Spaces.

Names are required, trimmed, Unicode NFC-normalized, and unique under `StringComparer.OrdinalIgnoreCase` within each entity kind and profile. Archived Spaces retain their names. `WORKSPACE_NAME`, registered on every application SQLite connection, enforces the same case-insensitive uniqueness. External SQLite editors must register an equivalent collation. Tag display prefixes `#`; stored names are not required to contain it. Optional colors use a closed None/Blue/Green/Amber/Red palette, persisted as NULL or integer 1–4. Presentation maps these to existing semantic design brushes, used only for small indicators/icons. Space sort order is assigned on creation and preserved on edit; there is no drag ordering.

### Services and transaction boundaries

Core defines organization records, `WorkspaceItemReference`, snapshot/filter semantics, and repository/service contracts. `OrganizationService` validates input, resolves current profile under the existing shared workspace operation gate, rejects stale profile IDs before database access, and delegates to `SqliteOrganizationRepository`. Profile lifecycle implementation and file handling are unchanged. The repository uses short-lived, unpooled connections, parameterized values, and fixed enum-selected SQL identifiers. Catalog plus relationship reads share one transaction. Writes use immediate transactions; assignments require an existing non-deleted item and, when adding a Space, an active Space. Repeated assignment is idempotent and database uniqueness remains enforced.

Organization changes deliberately **do not update WorkspaceItem.UpdatedAtUtc** or any other Task field. That timestamp continues to describe task content/lifecycle edits; organization metadata has its own timestamps. Assignments, removals, Tag deletion, and Space archive/restore leave task identity, title, description, status, priority, date, and timestamps intact. Meaningful Tag/Space edits advance their own UTC timestamp monotonically; no-op saves/archive/restore leave it unchanged.

Archiving a Space retains its assignments and leaves its Tasks active. It disappears from the normal sidebar and Library Space filter but remains editable and restorable in Manage spaces. Existing archived assignments appear in task detail and may be removed; restore a Space before adding new assignments. There is no Space Trash or permanent Space deletion in Phase 3.

Deleting a Tag removes that Tag and its assignments transactionally, never the items. The UI always confirms the Tag's name and assignment count with Cancel as default. Task archive/trash semantics are unchanged; normal Space views query the active Task collection, so archived/deleted Tasks cannot leak through their retained links.

### Presentation and profile switching

The existing task view serves `Space/{guid}` as well as the Library, Today, Archived, Trash, and detail. The sidebar lists active Spaces, includes `+ New Space`, and preserves native collapsed-pane navigation. Space task actions call the same task commands as the Library; detail's Back returns to the originating Space. An archived/missing Space link produces a friendly unavailable message.

The Library combines optional Tag and active Space selections with its existing title filter using AND semantics. Space views have a fixed Space selection plus optional Tag/title filters. These filters do not affect Today, Archived, or Trash. Today retains its local-date query, completed-task behavior, timer refresh, and layout.

Task detail has native Tags/Spaces expanders listing available entities with Assign/Remove actions. Multiple assignments are supported. These actions save immediately and preserve unsaved task edits; task content still uses its existing explicit Save. Quick create stays unchanged: save the Task, then assign it in detail. The lightweight Tags and spaces management view supports create/rename/color editing, Space description/icon editing and archive/restore, and confirmed Tag deletion. Native controls and existing design tokens are reused; code-behind only bridges control events, dialogs, and sidebar presentation.

Task and organization view models clear catalog, rows, filters, choices, and drafts on profile changes; Space/detail routes return to Tasks. Revision counters reject delayed reads from a prior profile or navigation request. Actions retain their originating profile ID, and the shared gate makes profile switching/deletion wait for in-flight writes. No cross-profile catalog is retained. Window close waits for organization work as well as task/profile operations.

### Verification and limitations

Automated coverage includes a populated Phase 2 upgrade with the original ledger entry preserved, migration idempotency, global/workspace separation, Unicode and case-insensitive names, palette validation, rename/no-op timestamps, many-to-many uniqueness/removal, database foreign keys, transactional cascade rollback, Tag deletion, Space archive/restore, shared task status across Space views, combined filters, lifecycle exclusions, profile isolation, stale actions, delayed reads, and switching during an organization write. UI-independent organization view models are source-linked into the existing test project. No NuGet packages or versions were added.

The native UI was exercised using temporary profiles: create Personal/Training Spaces and health/strength/urgent Tags; create `20 push-ups`, assign both Spaces and two Tags; mark Done through Training and verify Personal/Library; remove only Training; create `Prepare report` with urgent and filter the Library; archive/restore Personal without changing Tasks; cancel then confirm Tag deletion; switch profiles and restart to verify isolation/persistence. Temporary profiles were removed via named confirmation dialogs and the original profile selection restored.

For manual acceptance, repeat that flow, then inspect keyboard navigation, a collapsed sidebar, and Windows display scaling. Also check Today, Archived, and Trash after assigning and changing task lifecycle state. No later-phase destinations should become functional.

Phase 3 defers permanent Space deletion, inline entity creation during assignment, nested organization, manual ordering, and later WorkspaceItem types. Task duplication retains Phase 2 behavior and creates an unassigned copy. There is no global search, advanced color editor, audit/history, or unsaved-change prompt. Catalogs and relationships are loaded into an in-memory snapshot, consistent with the current unpaged Task Library; very large workspaces will need bounded database filtering and assignment pickers in a later phase. No Phase 4 functionality is included.

## Phase 4: Calendar and Events

### Event identity and schema

Events are independent of Tasks and reuse `WorkspaceItem` identity with `ItemType.Event = 2`. They have no completion status or checkbox. `EventItem` adds AllDay, StartDate, nullable StartTime, EndDate, and nullable EndTime; WorkspaceItem retains title, UTC audit timestamps, and archive/trash metadata. Tasks continue to use their existing status and date-only ScheduledDate.

Workspace migration **3: Create calendar events** adds `Events` with ItemId as both primary key and a cascading foreign key to WorkspaceItems. Separate StartDate and EndDate indexes support overlap queries. CHECK constraints enforce date order, all-day/time consistency, and a positive same-day timed interval. Domain validation uses DateOnly/TimeOnly to reject invalid calendar values. Workspace migrations 1 and 2, global migrations, and the ledger runner are unchanged. Events are stored only in the active profile's workspace.db, never app.db.

### Local date/time semantics

Event dates are invariant `yyyy-MM-dd` strings; timed values are invariant `HH:mm:ss.fffffff` strings without timezone offsets. They represent floating local Windows calendar dates and wall-clock times. They are not converted into midnight UTC or attached to a fixed timezone. Consequently, travel/timezone changes retain their written local times; DST gaps/overlaps are not mapped to an instant. This is deliberately a local planner, not a timezone-aware meeting scheduler. CreatedAtUtc, UpdatedAtUtc, ArchivedAtUtc, and DeletedAtUtc remain UTC.

All-day Events include their final date and clear both time values on save. Timed Events require both times; the end must follow the start on the same date. Timed multi-day Events are supported naturally. Timed end instants are exclusive: an Event ending at midnight does not occupy the following day. `OccursOn` and range queries use the same boundary. `IsPast(localNow)` derives history from the timed end or, for all-day Events, the passing of the final local date. Clock passage never writes status or audit metadata.

`EventService` owns validation, creation, editing, moving, and lifecycle rules. Duplicate titles are allowed. Meaningful changes advance UpdatedAtUtc monotonically; reads and no-op updates preserve it. Moving shifts both dates by the same number of calendar days, preserving local times and wall-clock duration. Calendar moves do not change identity or create copies. Invalid moves beyond supported DateOnly boundaries produce friendly errors.

### Persistence, lifecycle, and isolation

`SqliteEventRepository` uses parameterized statements and short-lived, unpooled connections. Event and WorkspaceItem inserts/updates share immediate transactions; failures roll back both. Archive hides an Event from normal Calendar/Today; Trash does the same. Restoring from Trash clears archive and deletion metadata, returning the Event to normal views. Permanent deletion requires Trash and a named native confirmation dialog; it cascades Event and generic organization links without deleting Tags, Spaces, or Tasks.

Event services enter the existing workspace operation gate and check the originating profile before capturing its database path. Profile lifecycle/storage behavior is unchanged. Calendar/editor reads use revision counters; switching profiles clears entries, unscheduled rows, event lists, and drafts, and an Event editor returns to Calendar. Delayed old results cannot populate the new profile. Closing the window waits for Calendar/Event work too.

Generic ItemTags/ItemSpaces already accept Event WorkspaceItem IDs and cascade correctly. This compatibility is covered by tests. Event assignment UI is deferred; no Event-specific organization tables or duplicated assignment infrastructure were added. Space views remain Task-focused in this phase.

### Bounded Calendar queries and presentation

Calendar is a view over existing Task and Event records. `ITaskService`/`ITaskRepository` now expose scheduled date-range and unscheduled queries. Scheduled queries filter active Tasks by the visible dates in SQLite using the existing ScheduledDate index. The unscheduled panel queries only the latest 100 active unscheduled Tasks, with that bound explained in the UI. It does not load historical scheduled Tasks or implement another Library.

Event overlap queries filter `StartDate <= visibleEnd` and `EndDate >= visibleStart`, with the exclusive-midnight adjustment, and exclude archived/deleted rows in SQL. A Month loads its visible 42-day grid, a Week its seven days, and a Day one date. Week boundaries follow the current Windows culture's first day of week. No all-history Event query is used for Calendar/Today; Archived/Trash use their own lifecycle collections.

`CalendarViewModel` supplies dates, grouped entries, navigation, editor state, and commands. CalendarView code-behind renders native controls, bridges nullable date/time pickers, and handles input gestures; it performs no SQL or domain mutations. Month cells show up to three compact entries plus a +N more action. Multi-day Events repeat on each occupied date. Clicking a date opens Day view; the selected date prefills new Tasks and Events. Previous/Next and Today use the selected view's period. Labels and pickers respect current culture; task marks and explicit Task labels distinguish Tasks from Event time/all-day labels without relying on color.

Week uses seven agenda columns, with separate Tasks, All day, and time-ordered Events sections. Day uses those same sections for one date. Tasks never receive an invented time. The Month grid fits the normal window and scrolls at narrower sizes. Long entries wrap to two lines with tooltips; a day can always be opened to inspect all entries.

Native drag handles initiate a WinUI drag after a small pointer movement. Day cells accept only the local Calendar view's captured typed entry, and the domain rechecks its profile. Scheduled and unscheduled Task drops call the existing ScheduleAsync; Event drops call MoveAsync. Dragging a middle day of a multi-day Event shifts the complete Event by the drop-date delta. Cancelled drags do not write. Rendering is deferred during a drag so the source is not replaced by the minute refresh. A Schedule for selected date action also supports unscheduled Tasks; existing Task/Event editors provide a keyboard-accessible date-editing path. There is no resizing, cross-app import, or automatic navigation while dragging.

Today retains its existing Task view and adds a bounded Events section. Archived and Trash similarly keep Task behavior and add Event rows with appropriate actions. A minute timer refreshes visible Calendar/Today Event content and derived past labels; Event editor drafts are not refreshed away. Task changes from other views appear when returning to Calendar.

### Verification and limitations

Automated tests cover Phase 3 upgrades with original ledger entries and organization data retained, migration idempotency/global separation, timed/all-day/multi-day creation, validation, duplicate titles, update/no-op timestamps, local DST wall values, interval overlaps and exclusive-midnight ends, derived past state, transactional rollback, archive/trash/restore/delete cascades, bounded scheduled/unscheduled Task queries, rescheduling without duplication, local Today behavior, culture-aware ranges, selected-date creation, profile isolation, stale reads, and switching during an Event write. Calendar presentation sources are linked into the existing test project. No packages were added.

Manual acceptance steps: create timed, all-day, and multi-day Events; inspect Month/Week/Day; navigate Previous/Next/Today and open a date; create a Task for that selected date; schedule an unscheduled Task; drag a Task and an Event to another date and verify their details; check today's Events; archive/trash/restore an Event and cancel/confirm permanent deletion; switch profiles and restart. Native UI smoke checks exercised Event creation and all three views, period navigation, unscheduled-task scheduling, real mouse drags of Tasks and Events, Library/Today consistency, lifecycle actions, profile switching, and restart persistence. The temporary profiles were removed through named confirmations and the original selection restored. Keyboard traversal, different display scaling settings, and touch input should also be checked interactively.

Phase 4 intentionally has no recurrence rules, TaskOccurrence, carry-over, task times, reminders, notifications, resizing, or later item types. Event organization UI, timezone-aware instants, a proportional hourly timeline, and unscheduled-task paging are deferred. Week/Day are chronological agendas rather than hourly-positioned grids. Organization snapshots and existing Task Library behavior otherwise remain as documented in Phase 3. No Phase 5 functionality is included.

## Phase 5: Subtasks and Task Dependencies

### Task identity and migration

A subtask is an ordinary `TaskItem` with nullable `ParentTaskId`. It retains its WorkspaceItem GUID, all normal Task properties and organization assignments. There is no separate subtask table or entity, inherited priority/date/organization, or duplicated Calendar record. Creation under a parent asks only for a title and uses ToDo, priority None, an empty description, and no scheduled date or assignments. Children can themselves have children without a domain depth limit.

Workspace migration **4: Create subtasks and dependencies** adds `Tasks.ParentTaskId`, referencing `Tasks.ItemId` with `ON DELETE SET NULL`, and a partial parent index. It adds `TaskDependencies(TaskId, DependsOnTaskId, CreatedAtUtc)`, a composite primary key, a self-dependency CHECK, cascading foreign keys to both Tasks, and a reverse dependency index. All three shipped workspace migrations and both global migrations remain unchanged. Relationships exist only in each profile's workspace.db. Event schema and semantics are unchanged.

### Progress, completion, and reopening

`TaskGraph` is an operation-scoped batch of Tasks and dependency records. Leaf progress is derived in memory with explicit stacks, without recursive call-stack growth, N+1 reads, or persisted percentages. A structural parent contributes no additional unit: a branch with two leaves contributes two. Done is the only completed leaf state. Percentages round to the nearest integer, away from zero at the midpoint (2/3 = 67%). Only Tasks with children display progress.

Archived and trashed descendants retain their relationships and remain required leaves. Hiding a Task never silently satisfies a completion requirement. Their detail rows explain their lifecycle state; a trashed child must be restored before editing. A parent cannot transition manually to Done while a required child or dependency remains incomplete. Error messages name up to three immediate blockers.

After a Task or relationship mutation, parents are evaluated in prerequisite order. A parent becomes Done only when all immediate children and its explicit dependencies are Done; this guarantees all descendants have completed, including intermediate parents' dependencies. Completion propagates upward. A previously Done parent whose requirements become incomplete reopens to ToDo, including when an incomplete child is added. Other incomplete parent states remain intact. A completed parent is reopened by reopening a child, not by leaving all its children complete and manually choosing an incomplete status. Automatic persisted status changes advance UpdatedAtUtc monotonically; progress reads write nothing.

Leaf Tasks retain explicit status control. Dependencies do not automatically complete or reopen leaf dependents. Reopening a blocker after a leaf dependent is already Done does not undo that historical completion; the guard applies to the next transition into Done. Adding an incomplete dependency to a Done Task requires reopening it first. Parents independently recalculate their own requirements when blockers change, so a parent can remain incomplete at 100% leaf progress while an explicit dependency is unfinished.

### Dependency rules and graph integrity

TaskId depends on DependsOnTaskId. Dependency blocking is derived and does not persist Status=Blocked: Doing and a dependency warning can coexist. Title, description, priority, scheduled date, organization and incomplete status changes remain available while blocked. Only completion is guarded. The picker filters titles within the current workspace and excludes self, existing assignments, and all cycle/deadlock candidates; the service repeats validation independently of the UI.

For completion validation, a parent has prerequisite edges to its immediate children and a dependent Task has prerequisite edges to its blockers. A topological check rejects any cycle in this combined graph, on both dependency addition and reparenting. This rejects self-parenting, direct/deep hierarchy cycles, direct/deep dependency cycles, a child depending on any ancestor, and indirect mixed cycles through unrelated Tasks. A parent depending on its own descendant is allowed: that requirement is redundant but does not form a cycle. No general-purpose graph framework was introduced.

### Transactions, lifecycle, and profile isolation

TaskService owns integrity rules. SqliteTaskRepository reads the Task/relationship batch inside an immediate write transaction, invokes the domain mutation, and persists only differences before committing. Child status changes, ancestor status propagation, parent links and dependency changes therefore succeed or roll back together. No SQL executes in ViewModels. Ordinary independent Task creation and duplication retain their existing atomic insert path. Duplication makes an unassigned top-level copy and copies neither children nor dependencies.

Archive and Trash affect only the selected Task. Children remain individually discoverable in Library, Today, Calendar and Space views whenever their own properties qualify. ParentTaskId survives archive, Trash and restore. Permanent deletion requires Trash and the existing named confirmation; immediate children become top-level, their descendants remain attached to them, and no surviving Task is deleted. The service detaches children and updates their timestamps in the same transaction; ON DELETE SET NULL also protects referential integrity at the database boundary. Dependency rows involving a permanently deleted Task cascade away, without deleting the other Task. Archived or trashed incomplete blockers continue blocking until completed after restoration, explicitly unlinked, or permanently deleted.

All operations resolve the expected Profile ID under the existing shared workspace operation gate. Foreign parent/dependency IDs must exist as Tasks in that workspace. Profile switching waits for writes. Presentation clears graph, hierarchy, dependency candidates, progress, filters and drafts on profile change; revision checks discard delayed reads. There is no cross-profile relationship cache.

### Presentation and existing views

Task Detail adds indented nested subtasks, leaf progress, add/open/complete/reopen controls, parent navigation, a separate Blocked by section, dependency opening/removal, and a title-filtered add picker. Relationship actions save immediately and preserve unsaved Task fields; parent status changes refresh the displayed persisted status. Normal Task edits retain explicit Save. Native controls and existing design tokens are reused.

Library rows are ordered as a hierarchy with indentation and immediate-parent context, plus subtle progress. Filters still return matching children independently; their context remains visible when a parent is filtered out, archived or in Trash. Indentation is visually capped at 16 levels to keep deep rows usable; the model, parent context and detail navigation are not depth-limited. Today and Space views reuse the same rows and Task mutations. Calendar continues querying only its visible scheduled range, without artificial dates or Event changes; a Task mutation may load the relationship batch to recalculate ancestors.

### Verification and limitations

Automated tests cover the populated Phase 4 upgrade and idempotency, unchanged migration hashes, creation/defaults/independent assignments, nested leaf counts and rounding, thousands of hierarchy levels, parent completion and reopening with timestamps, manual guards, self/direct/deep/mixed cycles, safe reparenting, lifecycle discovery and restoration, surviving children on permanent deletion, dependency guards and lifecycle, foreign-key behavior, rollback, duplication, profile isolation, Calendar/Today scheduling and presentation filtering/draft preservation. All earlier Profile, Task, organization, Calendar and Event tests remain part of the suite.

Native acceptance: create Prepare trip with Buy flights, Reserve hotel (Check Booking and Check Airbnb), and Pack bags; check 0/4, 2/4 (50%), hotel completion and final trip completion; reopen Check Booking and verify both ancestors reopen; attempt premature parent completion. Create Review document and Publish document, assign the dependency, verify the guard, complete Review and then explicitly complete Publish. Schedule a child through Calendar and complete it from Today; give parent and child different Spaces; archive/trash/restore the parent, cancel then confirm permanent deletion and verify surviving children. Switch profiles and restart to verify isolation and persistence. Cycle rejection is also covered directly in service tests because invalid candidates are intentionally absent from the picker.

Phase 5 keeps hierarchy rows expanded; there is no collapse state, hierarchy drag/reorder, or reparenting UI (the service supports validated reparenting). Large Task workspaces still use batched in-memory hierarchy/relationship snapshots rather than paging. There is no separate completion history or bulk descendant lifecycle action. Alternate DPI, touch and full keyboard traversal require additional interactive verification. No numerical Task kinds, recurrence, carry-over, templates, boards, notifications or Phase 6 features were added.

## Phase 6: Numerical and Value-based Tasks

### Definition, result, and precision

`TaskValueType` defines Checkbox, Number, Percentage, Currency, Duration and CustomUnit. A Task with no `TaskValue` is Checkbox, preserving all existing defaults and requiring no target. For other types, `TaskValue.Target` is the Task's base target and `Actual` is its optional current one-off result. Null Actual means no result recorded; it is never persisted as zero. Editing Actual replaces that result and creates no history.

Targets must be positive and Actual nonnegative when supplied. Number, Currency and CustomUnit use .NET decimal semantics; Actual can exceed Target and the full surplus remains stored. Percentage additionally requires both values at most 100 and defaults a new editor target to 100. Currency codes are trimmed, normalized to uppercase and validated as three ASCII letters. New Currency editors obtain an editable Windows-region currency default where available. Changing the code performs no conversion. Custom units belong to the Task, are trimmed, require 1–32 characters, and reject control characters; there is no global unit catalog.

Duration uses whole total seconds in the domain's decimal fields, validated before persistence, and SQLite INTEGER storage. Its maximum is the whole-second TimeSpan range, 922337203685 seconds. Input requires minutes:seconds or hours:minutes:seconds, with seconds below 60 and the middle minutes below 60 for three-part input. Display uses 30:00, 13:04 or 1:15:30. A bare 30 is rejected instead of being silently interpreted as seconds.

Value progress is Actual / Target using decimal arithmetic. Thus Percentage Actual 60 against Target 80 displays 75% of target, and Number Actual 25 against Target 20 retains a ratio of 1.25 and displays 125%. Only the visual bar is capped at 100%; conversion to double occurs at the presentation boundary for that capped bar. Textual percentages round to the nearest integer, away from zero at the midpoint. Phase 5 leaf-based subtask progress remains independent and is labelled separately; no combined percentage is calculated.

### Workspace migration and persistence

Workspace migration **5: Create task values** adds a normalized optional one-to-one `TaskValues` row keyed by `ItemId`, with a cascading foreign key to `Tasks.ItemId`. Migrations 1–4 and global migrations remain unchanged. Existing Tasks need no backfill: an absent value row means Checkbox and leaves their identity, status and relationships intact.

The table contains ValueType, Target, Actual, TargetSeconds, ActualSeconds, CurrencyCode and Unit. Decimal values are persisted as invariant `G29` TEXT and read directly into decimal; SQLite REAL and numeric affinity never participate. Duration uses only the two INTEGER seconds columns. CHECK constraints enforce the type-specific column shape, integer duration range, currency shape and unit length. Service validation enforces decimal bounds and value rules. The primary key supports the one-to-one lookup without an additional index. Task reads use a LEFT JOIN, including the existing bounded Calendar range queries, with no per-row queries.

A value write replaces all applicable columns or deletes the row when converting to Checkbox. It participates in the existing immediate Task graph transaction, so Actual, Task status, ancestor propagation and UpdatedAtUtc changes commit or roll back together. Meaningful value definition/result changes advance the existing monotonic UTC timestamp; an unchanged result with no status change is a no-op. Ordinary creation also inserts its value atomically. No SQL or migration behavior is introduced in presentation classes.

### Completion, parents, and dependencies

A value-based Task can transition to Done only when its own Actual reaches Target, all required children/descendants are complete, and all explicit dependencies are complete. Manual completion validates all those requirements and gives a friendly error. Explicit value recording or a full Task update automatically completes an eligible numerical Task, including when a target is lowered. Lowering or clearing Actual below Target, or raising Target above Actual, reopens a Done Task to ToDo and recalculates its ancestors in the same transaction. Other incomplete statuses remain intact. To reopen a reached value Task manually, lower or clear Actual; keeping a reached result and merely toggling status does not override automatic completion.

Parent recalculation still follows the combined hierarchy/dependency prerequisite order, and now also checks each parent's own value requirement. Completed children alone cannot complete an unreached numerical parent, and a reached parent cannot bypass unfinished children. Archived or trashed required descendants retain the Phase 5 semantics. Value progress and leaf progress can each show 100% while another completion requirement remains unmet.

Completing a blocker alone does **not** auto-complete a numerical leaf dependent. A later explicit Record actual (even the same value), full Task update, or manual completion can complete it once all guards pass. Parents continue to recalculate independently when blockers change. The Phase 5 historical leaf rule is retained: reopening a blocker does not itself undo a completed leaf dependent; a later explicit value/full update reevaluates all its completion conditions. Calendar scheduling and lifecycle actions do not act as explicit value updates.

### Editing, lifecycle, and profile isolation

Quick Create still defaults to Checkbox and adds a Task type choice; non-Checkbox types request only their target and applicable currency/unit. Task Detail adds value inputs, a progress preview and Record actual. That action saves only Actual and preserves unsaved ordinary Task fields; changed type/target/unit/currency must first be saved as a definition. Full Save includes the definition and Actual. Changing editor type clears previous target, Actual and unit/currency inputs before applying a new type's defaults, so Number to Duration never reinterprets an old number and hidden stale values cannot reappear. Decimal input/display respects the current Windows culture; input excludes thousands separators to avoid ambiguity.

Library, Today and Space views reuse ordinary Task rows with subtle value progress alongside existing hierarchy and organization context. Today opens the same Task for recording Actual. Scheduled numerical Tasks and subtasks use the existing Calendar behavior; there are no extra Calendar records, Task times or Event changes.

Archive, Trash and restore preserve both definition and Actual. Permanent deletion cascades the associated value row while preserving existing child/dependency deletion rules. Duplicate copies type, target and applicable unit/currency, but clears Actual and starts ToDo; the existing top-level, unassigned duplication behavior remains.

Values exist only in the active profile's workspace.db. All operations keep the expected-profile check and shared workspace gate. Profile changes clear the value editor along with existing drafts and graph state. Native acceptance exposed a WinUI selection-container probe against the initial singleton organization filter collections; those collections now use arrays whose non-generic IList.IndexOf safely rejects foreign containers, preventing a profile switch from leaving the editor disabled.

### Verification and Phase 6 limits

Automated coverage includes a populated Phase 5 upgrade, idempotency and unchanged migration hashes; all value validations, exact decimal and integer persistence, null/below/equal/surplus Actual, progress and timestamps; manual completion, parent/descendant/dependency interactions and reopening; definition-only duplication, safe type changes, lifecycle cascade and rollback on failed inserts/value updates/ancestor writes; profile isolation, stale writes, Today/Calendar/organization reuse, culture-aware presentation and native filter-container compatibility. Earlier regression suites remain included.

Native Windows UI Automation acceptance exercised Number 20 with Actual 18, 20, 18 and 25 (including the capped bar); Duration 30:00/13:04; Currency 1500 EUR with 625 and 625,25; CustomUnit 300 pages/125; Percentage 80/60; numerical parent/child completion and reopening; dependency guards and explicit retry; duplication and type conversion; scheduling a numerical subtask in Today/Calendar; archive/trash/restore and permanent deletion; profile switching and restart persistence. Temporary acceptance profiles were deleted through named app confirmations and the original profile restored.

Phase 6 stores a single result, with .NET decimal precision/range and whole-second durations. Extremely disparate values whose calculated percentage exceeds decimal range are rejected with a validation message. Currency validation checks code syntax rather than maintaining an ISO registry. There is no value-entry history, conversion, aggregate unit calculation or inline Today value editor. Alternate DPI, touch and full keyboard traversal still need additional interactive verification.

**Recurrence, TaskOccurrence and carry-over remain deferred.** Future occurrence-specific effective targets and actuals can live separately from the base Task definition. Phase 6 creates no hidden occurrences, recurrence rules, surplus/deficit propagation, retroactive recalculation, Tracker history or Phase 7 functionality.
