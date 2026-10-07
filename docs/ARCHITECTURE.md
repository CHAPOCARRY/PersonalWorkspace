# PersonalWorkspace architecture

The foundation sections below record the approved Phase 0 design. Phases 1–9 below extend it and supersede historical statements about empty workspaces, placeholders, multiple application instances, numerical Tasks, recurrence, carry-over and Trackers. Each phase section records its own scope; Phase 9 adds independent measurement Trackers without changing Task execution or carry semantics.

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

## Phase 7: Task Recurrence and Task Occurrences

### Definition and execution

A recurring series has one existing `TaskItem` definition and many `TaskOccurrence` rows. Title, description, priority, organization, hierarchy, dependencies and optional base value Target remain on the definition. No Task is duplicated when a date is materialized. Non-recurring Tasks retain the Phase 6 model, including their own ScheduledDate, Status and Actual; migration creates no occurrence backfill.

For an enabled series, the definition has Status ToDo, ScheduledDate null and definition Actual null. Its lifecycle still comes from WorkspaceItem archive/deletion timestamps. The definition never becomes Done because an occurrence completes, and definition-level status/date/Actual writes are rejected. Each occurrence instead owns its local date, ToDo/Doing/Blocked/Done status and optional Actual. Enabling recurrence requires an explicit StartDate; an existing Done Task must be reopened and a one-off Actual explicitly cleared first. Enabling replaces the previous one-off schedule. Duplicate creates an ordinary unfinished Task with the same value configuration, without recurrence or execution history.

`TaskItem.IsRecurring` and `HasOccurrences` are derived by indexed existence queries, not stored Task columns. The latter includes retained suppressed rows. Once any occurrence exists, changes to value type, Target, unit and currency are rejected and those controls are disabled. This conservative Phase 7 restriction prevents old Actual values from being reinterpreted or historical progress from changing without versioned targets. Create a separate definition for a different value configuration; titles and other ordinary metadata remain editable.

### Rules and local dates

`RecurrenceRule` is a structured domain record, stored as normalized fields rather than opaque display text. It contains Pattern, mandatory StartDate, Interval (1–999), selected weekday bits, monthly day or ordinal/weekday, and optional inclusive EndDate. A null EndDate means no end. Start is a lower bound, not a forced occurrence: M/W/F starting Tuesday first yields Wednesday.

Daily intervals count calendar days from StartDate. Weekly intervals use the Monday of the week containing StartDate as their anchor, with Sunday represented by bit zero in the weekday mask. Every two weeks on Monday/Thursday therefore keeps a fixed alternate-week pattern, even if the first partial week starts Tuesday. Monthly intervals count months from StartDate's month. Missing days, including day 31 in short months and February 29 in non-leap years, are skipped. Ordinal weekdays support first through fifth and last; an absent fifth weekday is skipped rather than moved. There is no yearly pattern or occurrence-count end condition.

StartDate, segment boundaries, SlotDate and OccurrenceDate are `DateOnly`, persisted as invariant `yyyy-MM-dd` text. OccurrenceDate determines Calendar and Today placement. Today and reconciliation protection use the current Windows local date through TimeProvider; neither uses UTC day boundaries. Editor dates and numbers follow current Windows culture. Only technical CreatedAtUtc/UpdatedAtUtc timestamps are UTC.

### Identity, migration and transactions

Workspace migration **6: Create task recurrence and occurrences** adds `TaskRecurrenceRules` and `TaskOccurrences`. Existing workspace migrations 1–5 and global migrations are unchanged. The upgrade preserves all prior Task/value/relationship data and adds no rows until recurrence is explicitly enabled or materialized.

Each rule row is a `RecurrenceSegment` with its own GUID, TaskId, immutable rule fields, inclusive FromDate, exclusive UntilDate, Enabled and CreatedAtUtc. Each occurrence has a stable GUID, TaskId, SegmentId, immutable original SlotDate, movable OccurrenceDate, Status, nullable Actual, IsSkipped, IsOverride, IsSuppressed and UTC audit timestamps. No title, target or organization is duplicated. Occurrence Actual uses invariant decimal `G29` TEXT; Duration Actual is validated whole seconds represented in that same exact TEXT column. Existing one-off Duration INTEGER storage remains unchanged.

`UNIQUE(TaskId, SlotDate)` prevents duplicate executions for the same original slot across all segments. A composite foreign key `(SegmentId, TaskId)` prevents an occurrence from referring to another Task's segment. Both tables cascade when the Task is permanently deleted. Date indexes cover display-date and original-slot range queries; the rule index covers Task/enabled/boundary lookup. Every materialization, split, override, skip and recurrence removal uses one immediate SQLite transaction through the existing workspace operation gate. Occurrence IDs, original slots and creation timestamps are updated in place and never replaced by regeneration. Definition persistence ignores changes to the derived recurrence flags, so opening Calendar does not rewrite Task metadata or timestamps.

### Bounded materialization

Today ensures only its local day, Calendar only its visible date range, and detail/history the requested date window (initially today minus/plus 30 days). A request may span at most 366 inclusive days. Each eligible enabled segment evaluates only dates inside that window; jumping to 2099 does not fill intervening decades. Repeated requests are deterministic and idempotent, backed by slot uniqueness and serialized transactions.

Range reads load occurrences whose original slot **or** overridden display date falls inside the requested window. This both finds moved-in occurrences and remembers moved-out slots so they do not regenerate. Today/Calendar never load the full occurrence history. Rules-only reads load no occurrence rows; individual occurrence reads use the GUID. Series editing deliberately reads that selected Task's occurrence history for safe reconciliation. The existing definition graph is still loaded as one batch for hierarchy and dependency guards; Phase 7 does not introduce definition paging. Materialization is bounded per window and segment, but retained history can grow over the lifetime of a workspace.

### Editing, skips and history

**This occurrence** changes only its date, status, Actual and/or skip state. It marks the occurrence as manually overridden. Moving to a date already occupied by another slot retains both distinct executions, sorted by display date, original slot and GUID. Original slot identity never changes. A skipped row stays stored, disappears from Today/Calendar and remains accessible in history; clearing Skip restores it. Calendar drag defaults to this-occurrence movement.

**This and future** uses the selected occurrence's original slot as an explicit boundary, requires today or a future slot, and requires the new StartDate to equal that boundary. Earlier segments end exclusively there; replaced later segments are disabled. A new segment begins at the boundary with its own rule anchor. Previous segment rules and past occurrences remain intact. This also preserves the schedule for historical dates not yet materialized.

**Entire series** replaces the remaining schedule from the current local day, or from StartDate when first enabling recurrence. The supplied StartDate remains the interval anchor and lower bound; edits never rewrite historical schedules. Previously enabled segments are capped or disabled at the boundary. An occurrence is protected if either its original or displayed date is in the past, it has any manual override or skip, its status is not ToDo, or its Actual is non-null (including zero). Protected rows keep identity and execution data even if they no longer match the new rule.

Only untouched future rows are reconciled: matching slots are attached to the new segment in place; obsolete slots become suppressed tombstones. Suppressed rows stay stored to reserve their stable IDs and are omitted from display/history. If a later edit makes an untouched, still-future slot valid again, that same GUID is reactivated. No reconciliation deletes and recreates historical rows. Keeping an explicit edit flag also preserves a user's intent after reopening or clearing Actual.

Removing recurrence disables all rule segments and suppresses only unprotected remaining occurrences. The Task becomes ordinary ToDo and unscheduled. Existing past, completed, moved, skipped and otherwise edited occurrences remain available from its history, even after restart. Their status and Actual are never merged back into the Task. Normal views stop displaying its occurrences and no new ones are generated until recurrence is explicitly enabled again.

### Values, hierarchy and dependencies

Occurrence progress uses the definition's fixed base Target and that occurrence's Actual, with all Phase 6 validation, precision, duration formatting and capped-bar behavior. Null means no recorded result. Recording a reached result completes only that occurrence if its guards pass; lowering/clearing it below Target reopens only that occurrence. Manual completion cannot bypass an unmet value target or required subtasks/dependencies. A reached value must be lowered/cleared to reopen manually.

Guards use existing definition-level prerequisites. A recurring Task with children does not generate automatic child occurrences. Required child/dependency definitions must be Done; a recurring prerequisite remains ToDo and therefore cannot satisfy the guard merely through one completed occurrence. This deliberate limitation avoids inventing occurrence-to-occurrence dependency mapping. Changing a blocker does not retroactively recalculate stored occurrence statuses; an explicit occurrence edit reevaluates its guards. Ordinary parent/dependency recalculation remains intact, but recurring definitions are excluded from automatic completion.

For the daily 20 push-ups example, Actual 18 leaves that occurrence at 90% and ToDo; Actual 20 completes it. Tomorrow independently has Target 20 and Actual null. A surplus remains on its own occurrence. **Phase 7 implements no carry-over, deficit/surplus propagation, effective target, future-target adjustment or retroactive calculation.**

### Presentation, lifecycle and isolation

Library and Space views display definitions once, with a Repeats indicator and series context. Definition detail contains recurrence settings and bounded occurrence/history navigation. Today and Calendar combine ordinary Tasks, applicable occurrences and existing Events without duplicating the series definition. Opening an occurrence shows its date, original slot, status, value progress, Actual and skip editor, plus an obvious Open series route. The recurrence editor offers the selected-occurrence future scope or entire-series save. Validation errors retain friendly existing error handling; failed occurrence navigation clears the previous execution editor.

Archive and Trash hide a definition and all its occurrences from normal views and prevent further materialization while inactive. Restoration resumes the stored rule without erasing history. Permanent deletion cascades recurrence rows within the existing Task lifecycle transaction. No recurrence data is stored in the global database. Expected-profile checks and the shared operation gate guard every service operation; profile changes clear recurrence editors/history, and stale references cannot access another workspace.

### Verification, limits and the Phase 8 boundary

Automated tests cover migration 6 on populated Phase 6 data, idempotency and unchanged migration hashes; daily/weekly/monthly/ordinal rules and edge dates; bounded far-future generation, stable IDs and uniqueness; independent statuses and exact Actuals; Today/Calendar coexistence with ordinary Tasks and Events; UTC/local-day divergence; moved/skipped persistence, split and entire-series reconciliation; recurrence removal, lifecycle and cascade; profile isolation, stale references, cancellation, concurrency and transaction rollback. They also verify that materialization does not write the Task definition and failed occurrence navigation clears stale UI state. Earlier regression suites remain included.

Native Windows UI Automation acceptance exercised daily Number 20 at Actual 18 then 20 with tomorrow independent; Library showing one definition; M/W/F from a Tuesday; first Saturday over several months; moving one occurrence in its editor and through Calendar drag; persistent skip; future split from daily to weekdays; entire-series every-two-days with an end; history after recurrence removal; profile switching from a draft; and restart persistence. Temporary acceptance profiles were deleted through named app confirmations and the original profile restored. Alternate DPI, touch and exhaustive keyboard traversal still need separate interactive verification.

Phase 7 has no recurring Event rules, inherited subtask recurrence, occurrence dependency graph, editable historical rule segments, target version history or audit log of individual Actual changes. History displays current stored occurrence state in bounded windows. Target/type/unit/currency remain locked once occurrences exist; large series edits and the inherited definition graph are not paged.

Phase 8 can build on stable occurrence GUIDs, original-slot order independent of display moves, preserved segment anchors/boundaries, explicit overrides/skips, exact per-occurrence Actual, and the separate definition base Target. It will need explicit policies for calculation order after moves, skips and segment changes, historical target changes and recalculation scope. This phase neither assumes those policies nor adds CarryIn, CarryOut, EffectiveTarget or propagation/recalculation fields or logic.

## Phase 8: Carry-over and Retroactive Recalculation

### Eligibility, policy and history safety

Carry is available only for recurring Number, Currency, Duration and CustomUnit Tasks. Checkbox has no quantitative result; Percentage retains its existing 0–100 bounds and does not support carry. Ordinary one-off Tasks keep their Phase 6 behavior and receive neither policy nor occurrence-calculation rows. Duplicate still creates an ordinary Task without recurrence, carry settings or execution history.

`CarrySettings` contains independent Deficit and Surplus flags, both false by default, plus a persisted Locked flag. With both flags off, existing recurring Tasks retain Phase 7 execution behavior. Settings can change while occurrences are only untouched generated ToDo rows. Any explicit occurrence edit (including a move, skip, status change or recording/clearing Actual), or an automatic completed execution, locks the policy. Clearing Actual or unskipping does not unlock it. Re-saving the same policy is allowed. This deliberately conservative rule avoids silently changing historical targets and introduces no policy-version history. The existing value type/Target/unit/currency lock remains in force once occurrences exist.

The policy belongs to the logical Task series, not an individual recurrence segment. Definition detail shows its two options only for eligible recurring Tasks, explains null results and surplus limits, and disables edits after the history lock. Recurrence may still be edited with the Phase 7 scopes; changing its schedule does not reset policy or carry.

### Workspace migration and exact persistence

Workspace migration **7: Create occurrence carry calculations** adds `TaskCarrySettings(TaskId, CarryDeficit, CarrySurplus, HistoryLocked)` and `TaskOccurrenceCalculations(OccurrenceId, BaseTarget, CarryIn, EffectiveTarget, CarryOut)`. Each is an optional one-to-one row with a cascading foreign key to its owner. A partial `(TaskId, SlotDate)` index supports the nearest non-skipped, non-suppressed predecessor lookup. Migrations 1–6, global migrations, original recurrence tables, occurrence GUIDs, original slots and Actual storage are unchanged.

Migration backfills calculation rows for all existing value occurrences, including Percentage and retained historical series. BaseTarget comes from the approved locked Task target; CarryIn and CarryOut start at zero, and EffectiveTarget equals BaseTarget. Duration targets are copied from integer seconds to exact text. Actual, status, overrides and audit timestamps are not modified. Eligible enabled series receive policies with both flags off and a history lock derived from existing meaningful execution. Checkbox receives no calculation row. Previously removed recurrence still retains its backfilled historical calculations, without becoming a new recurring Task.

New value occurrences snapshot BaseTarget at materialization. Subsequent calculation upserts cannot replace that snapshot. All four calculation values use invariant decimal `G29` TEXT, consistent with Phase 7 occurrence Actual; Duration calculations represent whole seconds. No SQLite REAL or floating-point math is used. Core `ExactDecimal` aligns decimal coefficients using BigInteger and rejects results that cannot fit exactly in .NET decimal, rather than accepting silent precision loss during addition/subtraction. Duration effective targets must also remain within the existing supported whole-second range. Overflow or invalid results reject the entire transaction. Progress formatting retains the existing decimal ratio and rounding conventions; arithmetic for a disabled carry direction is not evaluated unnecessarily.

### Calculation semantics

For a non-skipped occurrence, incoming positive carry is unfinished work. Incoming negative carry is surplus credit, clamped to no less than minus BaseTarget. EffectiveTarget is the exact sum of BaseTarget and that clamped CarryIn, so it never becomes negative. Any credit exceeding this occurrence's base requirement is discarded here and is not banked for later dates.

When Actual is supplied, the difference is EffectiveTarget minus Actual. A positive difference produces CarryOut only with Deficit enabled; a negative difference produces CarryOut only with Surplus enabled. Other cases produce zero. Actual is never clamped or overwritten. Thus 20/18 yields +2, then 22/20 still yields +2; with both policies enabled, 22/25 yields -3 and the next base-20 execution has target 17.

Null Actual means no recorded result, not zero, and always produces zero CarryOut. A positive effective target with null Actual is quantitatively incomplete. Explicit Actual zero can create a deficit. Entering or clearing a result later invokes the same downstream recalculation as any other correction.

A zero EffectiveTarget is quantitatively satisfied even with null Actual. It can become Done when hierarchy/dependency guards permit. The UI says “Covered by previous surplus”; no artificial Actual zero is required. Its outgoing carry is zero unless the user explicitly records positive Actual and Surplus is enabled. For example, 20/50 produces -30, the next occurrence stores CarryIn -20 and target zero, and its unrecorded result emits no further credit. Progress at target zero is 100% without division by zero. Definition Target validation still requires a positive target; allowing zero is restricted to occurrence effective-target validation and presentation.

### Original slots, skips, gaps and segments

Carry follows immutable original SlotDate order, never displayed OccurrenceDate. A moved execution keeps its GUID, original slot, calculation snapshot and position in the chain. Calendar dragging changes only its display date. Editing the moved execution's Actual still affects successors in original-slot order.

A skipped execution is retained but has zero CarryIn, EffectiveTarget and CarryOut as its own work calculation. Its stored Actual is preserved but excluded from arithmetic. The in-memory chain passes preceding carry through the skipped row without consuming or adding anything. Unskipping restores its calculated requirement and recalculates successors. Suppressed unused slots are excluded from the execution chain and never acquire results or new identities through recalculation.

An absent conceptual recurrence slot is different from a skip. It has no recorded Actual and therefore breaks incoming carry. `RecurrenceSchedule.Next` tests the enabled segments between stored slots using interval arithmetic and month/weekday candidates, without walking every intervening day or generating missing occurrences. A daily series with only day 1 and day 3 stored cannot transfer day 1's carry directly to day 3: day 2 is unresolved. If day 2 is later materialized it gets its own predecessor's carry, emits zero until a result exists, and can then affect day 3 after an explicit edit.

Carry crosses This-and-future boundaries because the sequence is grouped by TaskId across all segments. An Entire-series edit retains Phase 7 protected history and Actuals, reconciles only unused future slots, then recalculates the resulting stored sequence from the schedule boundary. Past snapshots/identities are not regenerated. Conceptual gaps use the current preserved segment boundaries; an interval or weekend without a scheduled slot does not itself reset carry.

### Transaction and recalculation algorithm

The recurrence repository retains one immediate SQLite transaction and the existing workspace operation gate. Its callback first applies the requested input change to the scoped state and identifies the earliest/latest directly affected original slots. For carry-enabled affected series, the repository expands that batch to the nearest preceding non-skipped, non-suppressed row and the already-materialized suffix. It also loads that Task's segment definitions. This is a batch read per affected series, not one predecessor query per occurrence. There is no occurrence generation during propagation.

`CarryRecalculator` groups rows by Task, sorts them by original slot, starts from the persisted predecessor output, detects unresolved conceptual gaps, and calculates sequentially. It updates only derived fields and resulting completion status, never downstream Actual or IDs. Explicit execution edits reevaluate completion; unchanged historical calculations are not reevaluated merely because Calendar materializes a surrounding window. Once all directly affected slots have been processed, an unchanged outgoing carry allows propagation to stop. Otherwise it continues to the end of the stored suffix. Rules/policy changes and restoration can explicitly request the complete affected stored suffix.

Completion validation runs against the final calculated result before writes commit. Only changed policy/occurrence/calculation rows are persisted. The Actual correction, all dependent calculations and status changes commit together; validation, precision errors, cancellation or failed SQL leave the prior consistent database state intact. The result returned to the UI is rebuilt from the recalculated state, so it cannot expose pre-calculation targets.

Read-only refreshes with no new materialization request no suffix load or recalculation. Today/Calendar retain their Phase 7 date-window queries, and materialization remains limited to 366 inclusive days per request. Historical correction loads a finite existing suffix, not future decades or unrelated series history. Very large stored suffixes are currently processed in memory rather than streamed in fixed-size pages. Sorted traversal and grouped lookups avoid a quadratic per-occurrence search; changes are written inside the same local transaction.

### Completion, dependencies and hierarchy

Quantitative completion compares Actual against EffectiveTarget. With guards satisfied, a decreased target can complete a later occurrence automatically, and an increased target can reopen Done to ToDo. Other incomplete statuses retain the established behavior. A manual Done request cannot bypass the effective target or non-value guards; manually reopening a covered execution requires correcting its prior credit or skipping it.

Carry math is independent of required child/dependency status. An incomplete blocker can prevent Done but cannot erase a numerical +2 deficit or -3 credit. Phase 7 definition-level prerequisites remain the only guards: no occurrence dependency graph or recursive occurrence tree is introduced. Completing an occurrence leaves the recurring definition ToDo, so it does not complete definition ancestors. Existing ordinary TaskGraph propagation remains unchanged. A blocker change alone does not retroactively rewrite stored occurrence completion; a subsequent explicit occurrence edit, affected quantitative recalculation or series restoration reevaluates the applicable guards.

### Lifecycle, presentation and isolation

Archive and Trash retain policy, snapshots, results and calculated history while hiding the series and stopping active materialization. Restoration recalculates an enabled carry series in the same transaction as its lifecycle change, without generating new dates. Permanent deletion cascades policy and calculation rows through the existing Task and occurrence ownership chain.

Removing recurrence preserves historical calculated values exactly and suppresses unused future occurrences using Phase 7 rules. The ordinary Task receives none of an occurrence's carry or Actual. No future slots are generated. Explicit edits to retained history still recalculate its stored sequence; with recurrence disabled there are no active conceptual recurrence slots to generate. Re-enabling uses the retained locked policy and normal segment/materialization rules.

Occurrence detail distinguishes Base target, unfinished amount carried in or surplus credit, Effective target, Actual and carry forward, with current-culture decimal/duration/unit formatting. It explains original-slot order and skipped/covered behavior. Today, history and Calendar value text use the effective target; for example, 18/22 displays 82%, not 90%. Library remains definition-based and never displays occurrence carry as definition data. Saving an occurrence reloads its calculated result; returning to other views reloads persisted downstream values.

All policy and calculation data exists only in the current profile's workspace.db. Expected-profile checks run under the shared gate, stale references fail, and profile switching clears carry controls and occurrence summaries. Restart reads persisted values rather than depending on view-model state.

### Verification and limits

Automated coverage includes migration 7 and populated Phase 7 backfill, unchanged migration hashes 1–6, default-off and independent policies, eligibility, permanent history locks, exact values and duration bounds, precision-loss rollback, four-day propagation, retroactive completion/reopening, null/zero distinctions, surplus floors and explicit results on covered executions, skips/unskips, moves, segment changes, absent conceptual predecessors, far-future bounded queries, unchanged historical completion during materialization, dependency/subtask guards, lifecycle/removal/cascade, duplication, transaction failure, profile isolation, restart and effective-target presentation. Prior regression suites remain included.

Native Windows UI Automation acceptance exercises the requested 20 push-ups correction through day 3; surplus-only 25 then 15; both policies with 22/25 producing target 17; Actual 50 producing a covered zero target and an unchanged following base target; null Actual followed by an entry; skip pass-through; a moved execution corrected in original-slot order; a daily-to-weekly split carrying across its boundary; locked policy controls; profile isolation; and restart persistence. Temporary profiles are removed through named confirmation dialogs and the original profile restored.

Phase 8 deliberately has no missing-as-zero/overdue policy, automatic movement of missed work, unlimited credit bank, Percentage carry, historical target editing, manual effective-target override or carry-policy version history. Policy and value-definition locks are conservative; create a new series for a different policy after execution begins. No recurring Events, occurrence dependency graphs, recurring subtask trees or Phase 9 features are added. Alternate DPI, touch and exhaustive keyboard verification remain separate interactive checks.

## Phase 9: Trackers Core

### Independent identity and layering

A `TrackerItem` composes `WorkspaceItem` (`ItemType.Tracker = 3`) with measurement configuration. It is neither a Task nor a TaskOccurrence, has no completion status, and never creates either automatically. Weight, Water, Steps and Mood are ordinary configurations of the same engine. Core owns records, frequency evaluation, validation, aggregate calculations and contracts; Services coordinates workspace access; Data owns SQL; App owns presentation and native controls. Dependency direction remains unchanged. No packages were added.

A definition contains value settings, an optional target, structured frequency/start/end settings, entry mode and aggregation. Title and lifecycle/audit metadata remain on WorkspaceItem. The target is an optional current reference value, not a completion threshold: recording a value below, above or equal to it never completes anything. Boolean false is a recorded measurement, not missing input. No deficit, surplus, carry or effective target belongs to Trackers.

### Migration 8 and exact storage

Workspace migration **8: Create trackers and entries** adds only `Trackers`, `TrackerEntries`, their indexes and an identity-validation trigger. Workspace migrations 1–7, global migrations and the migration runner are unchanged. Existing Phase 8 workspaces upgrade when opened; a new profile applies the same migration. The global database gains no Tracker tables or values.

`Trackers.ItemId` is both its primary key and a cascading foreign key to WorkspaceItems. Its insert trigger requires the referenced WorkspaceItem to have Tracker item type. Structured columns store ValueType, optional Unit/CurrencyCode/ScaleMin/ScaleMax, optional Target/TargetInteger, Frequency, optional StartDate/EndDate, Interval, Weekdays, EntryMode and Aggregation. The repository creates both identity and definition in one immediate transaction. Meaningful definition/lifecycle changes advance WorkspaceItem.UpdatedAtUtc monotonically; unchanged saves and reads do not.

`TrackerEntries` contains a stable GUID, TrackerId, ValueType/EntryMode integrity keys, PeriodDate, LocalDate, LocalTime, Value/IntegerValue, optional plain-text Note, and UTC CreatedAtUtc/UpdatedAtUtc. A composite foreign key ensures the type and entry mode match the owning definition. A partial unique index on `(TrackerId, PeriodDate)` for Single mode prevents duplicate canonical values. Indexed `(TrackerId, PeriodDate, LocalDate, LocalTime, CreatedAtUtc, Id)` and `(TrackerId, LocalDate, LocalTime, CreatedAtUtc, Id)` support period lookup, latest-period lookup, ordered recent entries and date ranges.

Decimal, Percentage, Currency, Distance and CustomUnit values and targets use invariant decimal `G29` TEXT. Integer, Scale, Duration and Boolean use INTEGER columns; Boolean is exactly 0/1. SQLite REAL and binary floating-point arithmetic are not used. `ExactValueText` is the shared Task/Tracker text boundary for exact persistence, current-culture decimal input and whole-second duration formatting/parsing. It rejects ambiguous grouping separators and inputs that decimal parsing would silently round. Existing Task serialization formats remain unchanged.

Supported values:

- Integer: signed Int64 whole values; optional unit.
- Decimal: signed .NET decimal values; optional unit (for example kg).
- Percentage: exact decimal from 0 through 100, inclusive.
- Currency: signed decimal with a trimmed, uppercase, three-ASCII-letter code on the definition. This validates syntax, not an ISO currency registry.
- Duration: nonnegative whole seconds up to 922337203685, consistent with Phase 6. Input/display accepts minutes:seconds or hours:minutes:seconds, such as 45:00 and 1:15:30; bare numeric seconds are not accepted in the UI.
- Distance: nonnegative decimal in one controlled definition unit, m, km or mi.
- Boolean: true or false, with an optional Boolean target.
- Scale: signed Int64 whole values within configured inclusive minimum/maximum bounds; maximum must exceed minimum. It is not hardcoded to 1–5.
- CustomUnit: signed decimal with a required trimmed unit of 1–32 characters and no control characters.

Optional targets obey the corresponding entry type's validation. There are no unit/currency conversions, compound measurements or task-style progress percentages.

### Local dates, frequency and periods

Semantic dates are DateOnly, stored as invariant yyyy-MM-dd; entry wall times are TimeOnly, stored without offsets. They are not UTC-midnight instants. Creation/update timestamps alone are UTC. Today uses the current Windows local date through the injected TimeProvider; displayed dates, times and numbers use current Windows culture. Changing timezone preserves written entry dates and wall times.

Frequency is independent of Task recurrence, with no generated placeholder entries:

- Unscheduled: frequency and start date may be omitted. Each entered local date is its own period; no Today/pending expectation is created. Optional start/end dates still bound admissible entry dates.
- Daily: each date on or after the explicit StartDate is one period.
- Weekly: seven-day periods anchored exactly to StartDate, including its weekday. An entry on any of those seven days belongs to that period's start date. This does not depend on the culture's calendar-week boundary.
- EveryXDays: the same anchored interval model, with X from 1 through 999.
- SelectedWeekdays: each selected day is an individual one-day period. StartDate is a lower bound, not a forced measurement day; unselected days have no expected period. Weekdays use a seven-bit mask with Sunday bit zero.
- Monthly: StartDate's day-of-month is the measurement anchor. Each month has a period from that day through month end; days before that month's anchor have no expected period. If the month lacks the configured day, the entire month is skipped. January 31 therefore has no February period and resumes on March 31. There is no silent day clamping or backfilling.

StartDate is required for scheduled frequencies. EndDate is optional and inclusive, must not precede StartDate, and truncates entry eligibility even within an anchored week/interval. After EndDate the definition and entries remain discoverable in Trackers/history, but Today excludes it. No replacement Tracker is ever created, and clock passage writes nothing.

### Entries, corrections, ordering and aggregates

Single mode saves without an entry ID upsert the existing entry for that period. Its GUID, original local date/time and creation timestamp survive correction; only changed value/note and UpdatedAtUtc are written. It never silently adds a second value. Multiple mode saves without an ID create distinct GUIDs. Explicit edits always address a stable entry ID belonging to the captured Tracker/profile. Dates and ordering are immutable during a correction; to relocate an entry, delete it and add an entry on the intended date.

`TrackerRules.Aggregate` and `ITrackerService.GetPeriodAsync` provide the canonical period value. Single mode returns its one value. Multiple supports Sum, Average, Min, Max and Last for ordinary numeric types. Percentage and Scale support Average/Min/Max/Last, excluding meaningless bounded-value sums. Boolean supports Last only. Empty periods return null, never an invented zero.

Last sorts by LocalDate, LocalTime, CreatedAtUtc and GUID, in that order. Editing a previous entry does not promote it to Last. Technical timestamp/GUID ties are deterministic. Sum/Average accumulate integer decimal coefficients with BigInteger to avoid binary arithmetic, intermediate overflow and order-dependent precision loss. Sum must fit exactly in .NET decimal or the write is rejected atomically. Average rounds the derived result to the available decimal precision, midpoint away from zero; Integer/Scale averages may be fractional. Duration averages round the summary to whole seconds, midpoint away from zero; duration totals remain within the supported duration range. Canonical entries are never rounded by aggregation.

Entry saves/deletions and aggregate validation occur in one immediate transaction. Validation, cancellation or SQL failure leaves the preceding canonical state intact. Entry correction/deletion changes derived summaries immediately and never modifies the definition's UpdatedAtUtc. Notes are optional trimmed plain text, without rich text or attachments. The UI confirms entry deletion with Cancel as default; deleting an entry never deletes its Tracker. No duplicate aggregate totals, charts or analytic snapshots are persisted.

Once entries exist, type/unit/currency/scale, schedule/start/end, entry mode and aggregation cannot change. These controls are disabled and the service independently enforces the rule, avoiding reinterpretation of stored history. Name and optional current target remain editable. Duplicate the definition to start a differently configured history; the copy has no entries. If every entry is deliberately deleted, there is no remaining history to protect and configuration becomes editable again. There is no target version history or audit log of previous entry values.

### Lifecycle, organization and isolation

Archive and Trash hide a Tracker from active/Today views but preserve its definition and entries. Archive restore clears its archive timestamp; Trash restore clears both lifecycle timestamps. Permanent deletion is allowed only from Trash, requires a named native confirmation, and transactionally cascades entries and generic organization links while leaving Tags/Spaces and all other WorkspaceItems intact. Duplicate creates a fresh active WorkspaceItem with the same title, type, settings, target, schedule, entry mode and aggregation, with fresh timestamps and no entries or assignments, matching existing duplication conventions.

Tracker detail reuses `OrganizationChoice` and the existing organization service for multiple Tag/Space assignments. ItemTags/ItemSpaces reference the same WorkspaceItem GUID; no Tracker-specific organization tables or parallel service were created. Space navigation remains the established Task view; it is not expanded into a mixed-item browser in this phase.

All service operations capture/validate the originating profile inside the shared workspace operation gate. Connections are short-lived, unpooled and enforce foreign keys. Profile switching/deletion waits for in-flight storage operations. View models clear rows, entry inputs, history, definition drafts and assignments when profiles change; a detail route returns to Trackers. Revision checks discard stale reads. Restart reads canonical local data rather than relying on presentation state. Window close also waits for Tracker work.

### Native presentation, Today and bounded reads

The Trackers navigation placeholder is replaced with an active definition library, one compact row per Tracker, with current or latest period value, optional target, frequency, pending state and an explicit Add/Record value action. It does not expand lifetime history into each row. Quick recording opens detail with the value editor ahead of the collapsed definition editor. Relevant creation controls depend on type/frequency; validation is inline and leaves inputs available for correction. Detail includes entry notes, the latest 50 entries, and an explicit date picker to query an older period for corrections/deletion. The selected historical period stays visible after correcting it.

Today keeps its Task/Event sections and adds a bounded-height Trackers section. It includes active scheduled Trackers whose current local date has an expected period, excluding not-started, ended, archived, trashed and unscheduled definitions. A period with no entries is Pending for both Single and Multiple. One entry, including zero or false, removes Pending. Multiple mode is then simply started: no finished-entering flag or target-derived completion is invented. The minute clock refresh detects local date rollover for Today/library without erasing a detail draft. Archived and Trash gain Tracker sections using the same lifecycle actions.

Repository selectors support recent entries (service bound 1–500), a specific period, one selected entry's period, the latest period, or an inclusive local-date range. The service rejects an unspecified/unbounded selector. Library and Today query only the current/latest relevant periods, never every definition's lifetime history. Definitions are still an unpaged collection, consistent with prior libraries, and their period reads are per-definition indexed queries rather than a single batch. A period/date-range result is time-bounded but not row-paged; very dense histories may need paging later. SQLite operations retain the existing local synchronous driver behavior.

### Verification, limits and Phase 10 compatibility

Automated tests cover migration 8 upgrading populated Phase 8 data, idempotency, unchanged migration hashes 1–7, global/workspace separation, all nine types, exact values and input, optional targets, bounds, local-date frequencies, monthly skips, start/end eligibility, Single uniqueness, stable Multiple ordering, each compatible aggregate, overflow/rollback, notes, historical correction/deletion, definition timestamps/history locks, lifecycle/cascades, duplication, generic relationships, profile isolation, local Today and bounded period/range/recent queries. Prior Profile/Task/organization/Event/Calendar/subtask/dependency/value/recurrence/carry tests remain included.

Native Windows UI Automation acceptance exercises Weight Decimal kg Daily Single target 75 (pending, 78.4 then canonical 78.2); Water CustomUnit L Multiple Sum target 2.5 (0.5 + 0.7 + 0.4 = 1.6; delete 0.7 = 0.9); Steps Integer 10000; Mood Scale 1–5 with 4 accepted/6 rejected; Study time Duration 1:15:30; end tomorrow and an already-ended counterpart; definition-only duplication; archive/trash/restore and cancel/confirm permanent deletion; profile switching and restart persistence. Automated clock advancement verifies the end-tomorrow boundary without changing the Windows clock. Acceptance profiles are removed through named confirmation dialogs and the original profile restored.

Phase 9 deliberately defers Tracker Calendar visualization, charts, heatmaps, streaks, cross-Tracker comparisons, annotations, dashboards, automatic replacement after EndDate, Tracker-to-Task automation, compound measurements, notifications and other later item types. Alternative DPI, touch and exhaustive keyboard/screen-reader checks remain separate interactive checks. No Phase 10 feature is implemented.

Phase 10 can query canonical exact entries by local date range, group their persisted PeriodDate values and reuse the same aggregate method. Stable definition/entry IDs, deterministic ordering, notes, type/unit metadata and indexed ranges support future analytics without schema redesign. Corrections/deletions naturally affect recomputation because no stale derived totals are stored. Any later cross-Tracker comparison must check compatible types/units; target history, configuration versioning and richer annotations would require explicitly designed extensions, rather than treating the current optional target as a historical snapshot.

## Phase 10 — Tracker Analytics

### Derived service and persistence boundary

`ITrackerAnalyticsService` returns an immutable range result containing one to four Tracker series, period points, summaries, expected/recorded counts, recording and target streaks, and target metrics. `TrackerAnalyticsService` orchestrates the active-profile guard, workspace operation gate and injected `TimeProvider`; `TrackerAnalytics` in Core owns the pure calculations. View models orchestrate selection, refreshing and formatting. Native chart code consumes period values and never aggregates entries.

TrackerEntries remain the sole canonical recorded values. Every query groups their persisted PeriodDate and calls the same `TrackerRules.Aggregate` used by Phase 9, including Single and all five Multiple modes (Sum, Average, Min, Max, Last). No averages, totals, streaks, points or heatmap cells are persisted, and no analytics migration is needed. Workspace migrations 1–8 are unchanged and hash-tested. Existing Task occurrence/carry behavior, Tracker identity, entries, organization links and lifecycle remain intact. Editing, deleting or adding an older entry changes the next query; detail reload refreshes an already-open Analytics section after canonical edits.

The repository adds a separate batch read API rather than changing Phase 9 selectors. It reads the selected definitions and one indexed entry range for all selected IDs in a single read transaction. There is no per-Tracker entry query during comparison. Explicit queries use `TrackerId IN (...) AND PeriodDate BETWEEN ...`; no unselected Tracker history is loaded. All time alone first queries the selected Trackers' earliest entry period and schedule start. The definition-only comparison catalog does not load entry history. A short-range request never implicitly becomes All time. Queries are read-only, verified against unchanged database bytes.

### Local ranges and canonical periods

Presets are inclusive local-date ranges: last 7/30/90 days including today, this month through today, last complete calendar month, this year through today, All time through today, and explicit custom start/end. Today comes from the injected clock's local timezone. Custom dates may include the future, but future dates are not expected yet.

Ranges select **complete periods by their local start date**, explicitly labelled in the UI. For example, selecting a weekly period's start includes that entire canonical weekly value, even when a constituent entry's local date falls after the selected end. A period beginning before the range is excluded rather than partially aggregated. This keeps values deterministic and identical to Tracker detail. Daily and unscheduled periods coincide with their entry dates.

Expected periods reuse `TrackerSchedule.PeriodOn` and count distinct scheduled anchors through today. StartDate/EndDate are inclusive; unscheduled Trackers have no expected periods. Weekly and Every X days use the persisted anchor, selected weekdays omit other weekdays, and Monthly skips months without its start-day anchor. An ended Tracker remains readable but gets no new expected periods or replacement definition. Archived Trackers can be opened deliberately from Archived; the current local archive date and later anchors are excluded. Trash is excluded from normal choices and analytics reads.

Lifecycle history limitation: Phase 9 retains only current lifecycle timestamps, clearing them on restore. Earlier archive/trash intervals cannot be reconstructed without additional authored history, so restored Trackers use their current schedule. Period eligibility is evaluated at the period start. Targets also use the current definition because historical target versions were not stored. The UI explains the available-data limitation; Phase 10 does not invent historical states or add a migration to reconstruct them.

### Missing values, summaries, rates and targets

An expected period without an entry has a null value, never an invented numeric zero or Boolean false. Numeric line/area segments break at those points; bars omit them. A recorded zero remains a value. Non-scheduled dates are not missing and do not interrupt continuity between consecutive expected periods. Empty ranges show an explanatory message without a zero chart.

Summary Count means recorded canonical periods, Latest means the last recorded period, and numeric Average/Minimum/Maximum use recorded period values only. Average is an unweighted mean of period values rather than a mean of raw entries; Multiple Sum values are not counted twice. Totals are offered for numeric Multiple Sum Trackers and Single Integer/Currency/Duration/Distance/CustomUnit Trackers. Single Decimal measurements such as Weight, Percentage, Scale, Boolean and non-Sum Multiple aggregations omit Total because a sum is not generally meaningful. Multiple Decimal Sum supports Total.

Boolean summaries show true/false period counts and true rate among recorded periods; missing periods are excluded from that denominator and remain visible in recording rate. No numeric Boolean average/chart is presented. Scale and Percentage use validated values and their configured bounds (0–100 for Percentage) on the chart. Currency/unit metadata is preserved without conversion.

Expected/Recorded/Missing counts use expected anchors; recording rate is recorded expected periods / expected periods. A zero denominator yields no rate, not 0%. Historical recorded values can still contribute to descriptive summaries when no longer expected under the currently available lifecycle state.

Targets compare expected canonical period values against the current target: numeric >= target, Boolean equality. Hit rate is reached / expected; missing expected periods are not hits. Average target attainment uses recorded expected numeric periods and a positive target, with its recorded-period denominator labelled. A zero/negative target retains hit counts and streaks but omits a misleading attainment ratio. Latest/target context is shown separately from counts.

All canonical arithmetic remains decimal or whole seconds. Sum/average reuse the existing exact BigInteger coefficient helper, avoiding intermediate decimal overflow. Duration averages round to a whole second using midpoint-away-from-zero, matching canonical Duration aggregation; summaries format hours:minutes:seconds, including totals beyond a single entry's duration bound. Unsupported result overflow produces a friendly validation error instead of floating-point fallback. Only chart positioning converts values to double; inspection text retains the canonical exact value.

### Streaks and heatmap

Recording streak means consecutive expected periods with an entry for numeric types. Boolean positive streak requires true; false or any missing expected period breaks it. Target streak is independently calculated from target hits and is never substituted for recording streak. Current and Best are limited to the selected range. Current is as of the injected local today, is zero for historical-only windows and inactive/ended definitions, and includes the current expected period: if it is missing, the streak is zero until recorded. A recorded false or below-target value also breaks the corresponding streak immediately. Best scans all expected periods in the range.

The calendar heatmap renders up to the final 366 dates of the selected range, respecting the Windows culture's week start. Each date maps to its canonical period; weekly/interval values repeat across dates in that period, while summary counts remain once per period. Missing, unscheduled, recorded zero, true/false and target hit/miss remain distinct. Symbols plus native tooltips and accessibility names expose date, exact value, period and target, so information does not depend solely on color. Long selected ranges retain full summaries/chart data while bounding the calendar's visual elements.

### Native UI, comparison and profile isolation

Tracker Detail gains a collapsed Analytics section with presets, custom dates, summary metrics, comparison selection, Line/Bar/Area presentation and a calendar heatmap. Native WinUI Canvas/Polyline/Polygon/Rectangle/Button elements use existing semantic colors and spacing tokens. Lines/areas retain missing-value gaps; bars use canonical period values. Shared axes show numeric bounds and local dates. Numbered markers, distinct comparison line patterns, legends, pointer inspection, keyboard focus labels and tooltips provide value access. Focusable chart markers are sampled to roughly 300 per series for longer ranges; lines and pointer inspection still use all period points. No chart dependency, network call, subscription or commercial component was added.

Comparison supports two to four directly compatible Trackers (the open definition plus up to three others). Value type, Unit, CurrencyCode and ScaleMin/ScaleMax must match exactly. Currency conversion, unit conversion and silent normalization are never performed. Incompatible selections show a friendly error and retain the choices so the user can correct them. Normal comparison choices are active definitions, including ended definitions; an archived definition can be the explicitly opened base series.

Analytics and range/comparison state clear when the active profile or selected Tracker changes. The service validates the originating profile inside the same workspace operation gate as mutations/profile switching. Revision guards reject stale async results; failed comparisons clear old chart/summary data. Window busy handling includes analytics reads. No cross-profile cache exists, and restart reconstructs analytics from persisted definitions and entries.

Native acceptance also exposed a Boolean entry selector reset issue: an empty string was not a valid ComboBox item, leaving the prior visual selection behind. Its binding now supplies null for an unset Boolean value, allowing consecutive true entries without a stale selection. Canonical entry behavior remains unchanged.

### Verification and limits

Automated coverage includes presets and leap/month/year boundaries; bounded batch queries; exact summaries and all canonical aggregates; old-entry correction/deletion/add-back; missing vs zero; each frequency and monthly short-month skips; start/end/archive/trash eligibility; recording and target rates/streaks; Boolean false/missing semantics; decimal/currency/duration/unit/Scale/Percentage behavior; heatmap mapping; two/four-Tracker comparison and incompatibility; migration hashes 1–8; read-only database behavior; profile isolation and late-result rejection; empty/failure/reset presentation; and the Boolean selector regression. Earlier phase tests remain part of the full suite.

Final verification: two full-solution build/test passes succeeded with zero build warnings/errors and 369/369 tests. Native Windows UI Automation verified Weight exact summary and downward line (79.4, 79.0, 78.7, 78.2; average 78.825), older correction/deletion, Water daily Sum, seven-day Steps bars/targets/streaks, Mood Scale and area selection, Boolean true/false and missing-current streaks, selected weekdays, ended/archived history, compatible comparison, incompatible units/currencies, custom/All-time ranges, Duration formatting, profile isolation and restart reconstruction. Window captures were inspected for the native line and comparison bars/heatmap; keyboard marker inspection exposed exact values. Named temporary acceptance profiles were deleted through native confirmations, the original profile restored, and temporary scripts/images removed. No commit or push was performed.

Phase 10 deliberately keeps analytics in Tracker Detail rather than introducing a separate dashboard engine or duplicating navigation. Pie/donut, annotations, advanced zoom/pan, normalization, conversion, forecasting and main Calendar integration are deferred. Today and Tags/Spaces are unchanged. Current metadata cannot reconstruct past target or restored lifecycle versions. Very dense period histories are date-bounded but not row-paged; definitions remain unpaged. Chart positioning has display-only floating-point precision, while accessible values and all calculations retain exact decimal semantics. Alternative DPI/touch and exhaustive screen-reader traversal remain separate interactive checks.

Future Pages/widgets can call the same profile-scoped analytics service and render immutable results without owning new analytical truth. Stable Tracker/entry IDs, explicit ranges, exact values, type/unit metadata and pure calculations support embedding without a persistence redesign. No Pages, Canvas, widgets, automation or other Phase 11 implementation was started.

## Phase 11 — Journal Core

### Identity, definitions and logical days

A Journal is a reusable definition with `WorkspaceItemType.Journal = 4` and the existing WorkspaceItem GUID, title, UTC timestamps and archive/trash lifecycle. Its description and ordered fields are separate from daily content. Multiple Journals, including duplicate titles, are supported; GUIDs distinguish them. A new definition may have zero fields. Generic ItemTags and ItemSpaces relationships belong to the definition, never to individual days.

Journal data is independent of Tracker data. Number, Percentage and Currency fields only save Journal values; they never create Trackers or Tracker entries. TaskReference and TrackerReference select existing definitions rather than copying their business data.

Each Journal has a logical entry for every local DateOnly. The injected TimeProvider supplies local today. Opening Journal, changing dates, Today summaries and Calendar Day reads never create rows or update timestamps. The first explicit Save containing meaningful content creates a GUID JournalEntry, unique on `(JournalId, EntryDate)`. Corrections retain its ID and CreatedAtUtc. Zero and explicitly selected false are meaningful; unset and whitespace-only text are empty. Clearing all values through Save deletes the physical entry and returns the day to its virtual state. Saving again then creates a new physical identity. Confirmed field deletion deliberately preserves existing entry rows, even when it removes their final value.

### Migration and typed persistence

Workspace migration 9 adds Journals, JournalFields, JournalFieldOptions, JournalEntries, JournalValues and JournalSelections. Migrations 1–8 and the global database schema are unchanged. All Journal storage resides in the selected profile's `Profiles/{profile-guid}/workspace.db`. Foreign keys enforce ownership, selection membership and cascades; indexes support definition order, date ranges, field usage and references. Entries are not opaque JSON.

JournalFields carry stable GUIDs, deterministic SortOrder, type, optional CurrencyCode or ScaleMin/ScaleMax, and a permanent first-use HistoryLocked flag. JournalValues has one independently queryable row per entry/field with a typed payload. Supported types are ShortText, LongText, Number, Percentage, Currency, Duration, Checkbox, Scale, Select, MultiSelect, Date, Link, TaskReference and TrackerReference.

Number/Percentage/Currency use the shared exact decimal G29 TEXT persistence helper, never canonical floating point. Percentage accepts 0–100 inclusive. Currency has a three-letter uppercase code without currency conversion. Duration stores whole seconds in INTEGER and uses existing duration text conventions, including 45:00 for 45 minutes. Scale stores an integer within configured bounds. Checkbox has unset, false and true states; opening an unset control does not persist false. Date stores local yyyy-MM-dd. Technical timestamps remain UTC. Numeric input/display follows current culture. Text trims surrounding whitespace but preserves internal multiline content. ShortText/Link are limited to 2,048 characters and LongText/description to 50,000. Links accept absolute http, https and mailto URIs, reject other schemes, and do not fetch content.

Options have stable GUIDs, labels and deterministic order; Select stores one normalized JournalSelections relationship and MultiSelect stores distinct relationships. Renaming an option updates its displayed historical label without changing selection identity. Referenced options cannot be deleted; unused options can be removed. Labels must be unique within the field ignoring case, with at most 200 options. Fields and options have simple up/down ordering controls.

### History safety, references and transactions

Once a field has ever stored a value, its type, currency code and scale bounds are fixed, including after clearing all current values. Renaming and reordering remain allowed. This conservative policy also blocks scale expansion; a different configuration uses a new field. Deleting a used field requires explicit service-level confirmation, and the native dialog names the loss of historical values. Its values and selections cascade away while JournalEntry identities remain.

Task/Tracker references store same-profile stable definition IDs. Current titles and archived/trashed state are resolved at read time. Cross-profile IDs and the wrong item type are rejected. Archive/trash retain references; permanent referenced-item deletion uses SET NULL and preserves the Journal entry. The UI shows Reference removed until the field is explicitly cleared or replaced. Such retained value rows count as entry content until Save removes them. FK reference clearing does not rewrite the authored entry timestamp.

Create WorkspaceItem plus Journal, duplicate definition plus fields/options, and each multi-field entry save are atomic transactions. Validation occurs before applying a patch; storage failures roll back all changes. Unmentioned fields in the service patch are preserved. Entry edits update only entry content timestamps; first-use metadata does not touch the Journal definition timestamp. Definition/configuration changes advance the definition timestamp. Unchanged saves and navigation do not change content timestamps.

Archive removes a Journal from active lists while preserving history; Trash supports restore, and permanent deletion from Trash cascades all Journal-specific data and generic organization assignments. Duplication creates fresh definition/field/option GUIDs, copies description/order/configuration, and starts with no entries or values. Tags/Spaces are not copied, consistent with existing WorkspaceItem duplication behavior.

### Native presentation and bounded reads

Journal replaces the placeholder with a definition selector, previous/today/next navigation, date picker and explicitly saved daily form. Configuration is a separate area for title, description, fields, options and generic Tags/Spaces. Save before changing Journal/date or applying configuration: unsaved drafts are not persisted and no per-keystroke SQL or autosave is implemented. Archived and trashed entry forms are read-only.

Today lists active Journals with Entry started/Empty. Calendar Day lists active Journals for its selected date and opens the matching Journal/date. Summaries derive the first two stored fields in SortOrder with shortened text; they are neither generated by AI nor stored. Month/Week do not query Journal entries. Archived/Trash sections reuse the modest list and native lifecycle menus.

The repository reads a single date or explicit range in a fixed set of batch queries. Definitions-only reads retrieve no entries. The service range API accepts at most 366 inclusive days. Field/option usage uses indexed existence checks rather than loading historical values. Definition, option and reference catalogs remain unpaged. The shared workspace operation gate and profile guard protect reads/writes; presentation revision checks discard stale results. Profile changes clear definition, date, fields, values and organization state. Restart reconstructs state from SQLite.

### Verification, limits and future compatibility

Automated tests cover migration 9/upgrading Phase 10/idempotency/unchanged previous migration hashes; all 14 field types; exact storage; lazy entries and uniqueness; history correction and cleanup; field/option history safety; references and lifecycle; profile isolation; atomic rollback; ordering/duplication; bounded queries; read-only summaries and presentation regressions. Native acceptance exercises Daily and Training, exact persisted values, historical correction, option rename, incompatible type rejection, duplication, Today/Calendar Day, restart, profile switching and lifecycle. Native checks also identified and corrected Journal selection synchronization, profile-event reload, deletion navigation and shell visibility issues.

Explicit Save, unpaged definition/reference catalogs, bounded rather than row-paged history, and no unsaved-draft recovery are Phase 11 limitations. RichText, Images, Attachments and Drawing are explicitly deferred, as are required/default-field engines, scheduling, Journal analytics, AI summaries, global search and export UI. Stable WorkspaceItem/entry/field/option IDs, normalized typed values and bounded service reads support future Pages/widgets, search, history and export without adding those features now. Phase 12 was not started.

Final verification: two full solution builds passed with zero warnings/errors, and both complete test runs passed 426/426 tests (57 added for Phase 11). Final native captures confirmed separate Today/Journal presentation; the final binary passed persisted-value, historical-date and Calendar navigation checks. The two named acceptance profiles were deleted through native confirmation, the original Personal profile restored, and temporary acceptance scripts/captures removed. No commit or push was performed.

## Phase 12 — Pages Core

### Identity and migration 10

A Page is a `WorkspaceItem` with `WorkspaceItemType.Page = 5`. Its GUID is the canonical identity; title, UTC creation/modification timestamps and archive/trash metadata remain on WorkspaceItems. Renaming, moving, reordering and organization assignment never replace that GUID. Duplicate Page titles are permitted. Titles are trimmed, required, limited to 200 characters and reject control characters.

Workspace migration **10: Create page core** adds only Pages: ItemId (primary key and cascading foreign key to WorkspaceItems), nullable ParentPageId (foreign key to Pages with ON DELETE SET NULL), nonnegative SortOrder and nullable controlled Icon. The built-in choices are None, Book, Star, Flag and Home; None persists as NULL. A parent/order/identity index supports sibling access, a CHECK rejects self-parenting, and an insert trigger requires a Page WorkspaceItem. Migrations 1–9, their SQL/name hashes, and global app.db remain unchanged. There is no content body, opaque JSON/HTML/Markdown blob or Canvas persistence.

### Hierarchy, order and reads

ParentPageId is a same-workspace Page GUID; NULL means root. There is no domain nesting-depth limit. PageGraph exposes ID lookup, immediate children, ancestors in root-to-parent order, descendants and parent validation. Traversal uses dictionaries/lookups and explicit stacks or loops, avoiding recursive call-stack limits. The service rejects self-parenting, direct/deep cycles, unknown/cross-profile parents and non-Page identities. New/move targets must be active. The native parent picker excludes self, descendants and inactive Pages, while service checks remain authoritative.

One repository SELECT joins Page metadata with WorkspaceItems for the active profile. It includes lifecycle metadata so hidden ancestors can be explained. Tree construction, ancestors, breadcrumbs, children and target choices operate on that batch; no per-node/per-level SQL is issued. Expansion/collapse is presentation-only and performs no database query or write. Tests exercise 2,000 persisted nested nodes with one repository read, and 5,000-level domain traversal. Metadata remains unpaged, appropriate for the intended hundreds/few-thousand personal Pages; no content/history data is loaded.

SortOrder is contiguous and zero-based within each stored parent group, including inactive siblings. Create and reparent append at the destination. Move normalizes the old and new groups in one transaction. Up/Down exchanges positions with the next sibling in the same visible lifecycle collection, skipping hidden siblings; unrelated groups are untouched. Ties at the read boundary use GUID for determinism. Reads never normalize or modify data. Application permanent deletion normalizes the old parent group and root group after detachment; the database SET NULL safety net independently preserves children if the parent row is removed directly.

### Lifecycle and duplication

Archive and Trash affect only the selected Page. Descendant lifecycle metadata and stored ParentPageId remain unchanged. An active Page whose immediate parent is archived/trashed is presented at the active tree root with explicit parent name/state context. Its own active descendants remain nested below it. This is a presentation projection, not a persisted reparenting. Restoring the hidden parent naturally reconnects the visible hierarchy. Breadcrumbs retain real ancestors and can open their lifecycle collection.

Archive restore clears only ArchivedAtUtc; Trash restore clears both lifecycle flags, matching prior WorkspaceItem behavior. Deleted Pages cannot be edited or duplicated until restored. Active and archived metadata can be edited. Pages management has Active, Archived and Trash collections, without changing existing global Task/Tracker/Journal lifecycle screens.

Permanent deletion requires Trash and a native confirmation naming the Page and explaining child survival. In one transaction, direct children are moved to root, their meaningful metadata timestamps advance, ordering is normalized, then the parent's WorkspaceItem is removed. Deeper descendants retain their parent links. Generic ItemTags/ItemSpaces links cascade for the deleted Page only; Tags/Spaces themselves survive.

Duplicate creates one fresh active WorkspaceItem, copies title/icon and appends in the same stored parent group, including when that parent is hidden. It does not duplicate children/subtrees or generic assignments, consistent with existing duplication conventions. No Canvas/content exists to copy. New timestamps belong to the duplicate.

### Transactions, organization and isolation

IPageService coordinates validation and metadata semantics; IPageRepository owns SQL. Every mutation reads an operation-scoped graph inside an immediate SQLite transaction and persists differences only. WorkspaceItem plus Page creation, multi-row move/reorder, duplicate and parent deletion/detachment are atomic; validation/storage failures roll back the whole change. No SQL is executed by the UI.

Meaningful title/icon/parent/order/lifecycle changes advance UpdatedAtUtc monotonically. Creation timestamps stay stable. Reads, expansion, navigation and no-op saves/moves/reorders do not alter timestamps. Tags and Spaces use existing generic relationships and organization service; assignments intentionally do not update WorkspaceItem timestamps, following Phase 3 policy. Multiple Tags/Spaces are supported independently of Page hierarchy; assigning a Space never moves a Page.

All Page data belongs to `Profiles/{profile-guid}/workspace.db`. Service operations validate their originating profile under the shared workspace operation gate before resolving that database. Foreign IDs and stale profile actions fail safely. Profile switching clears selected Page, graph, tree, breadcrumbs, drafts, parent choices, assignments and expansion state. Revision checks reject late loads; the new profile receives its own metadata batch. Window close waits for Page operations alongside existing work.

### Native workspace and navigation

Pages replaces its placeholder inside the unchanged application shell. Its workspace contains a virtualized hierarchy list with expand/collapse, selected-title emphasis and native buttons; indentation is visually capped at 12 levels without limiting underlying depth. Detail provides navigable ancestor buttons, immediate active child links, New child Page, lifecycle/duplicate/reorder actions, a lightweight Page details expander, and Move and organize. The parent picker distinguishes duplicate titles with a short identity suffix. Created/Last modified use local Windows time/current culture without seconds or offsets. Native errors are friendly and retain validation drafts.

An empty surface says the Page is ready for content; it contains no editable body or simulated widget. Title/icon saves are explicit. Unsaved metadata is discarded on navigation/refresh; organization and move actions save immediately. Active/Archived/Trash collections live inside Pages management. Removing the selected Page chooses its parent when eligible, otherwise a deterministic available Page or empty detail; stale deleted detail is never retained.

Navigation uses `Pages/{guid}` in the existing NavigationRoute model, not a hierarchy path. Returning within the session retains an eligible selection; first entry/profile switch falls back to the first eligible root/order/GUID. Startup does not force a Page route or persist Page navigation history. Today and Calendar receive no Page integration.

### Verification and limits

Automated coverage includes populated Phase 11 upgrade/idempotency/global separation and migration hashes 1–9; required/duplicate titles, icons and identity; nested ancestry/children; self/direct/deep cycles; root/child/reparent operations; sibling normalization and unrelated groups; archive/trash discovery/restoration; direct-child detachment and surviving descendants; definition-only duplication; multiple generic assignments; timestamps/read-only behavior; transaction rollback/cancellation; profile isolation; selection clearing/lifecycle fallback; deep traversal and batch-loading behavior. An existing value-task profile-switch test now awaits its final presentation refresh before temporary-directory teardown, fixing an observed pending-read file-lock race without changing Task behavior.

Native Windows UI Automation acceptance created Training > Goals > Muscle-up/Planche and Training > Progress; renamed Muscle-up Goal with the same entity, moved Planche under Training and reordered it; expanded/collapsed the hierarchy; exercised ancestor/child navigation, archive/trash/restore and permanent parent deletion with child survival; duplicated Training without its subtree; assigned Personal/Training Spaces and planning Tag; switched isolated profiles and restarted. Read-only acceptance-database checks confirmed persisted ordering, child detachment, no copied subtree, assignment counts and foreign-key integrity. Invalid descendant targets are excluded natively and cycles are rejected in direct service tests.

Phase 12 limitations: explicit metadata Save and no unsaved-draft recovery, unpaged metadata catalogs, no tree drag/drop or title filtering, no persisted expansion/navigation history, fixed built-in icons and capped visual indentation. Full alternate-DPI/touch/screen-reader traversal remains a separate interactive check. Pages lifecycle management stays in its own collections; Space navigation remains the prior Task-focused view.

Phase 13 can attach properly designed Canvas layout/content tables to stable Page GUIDs without redefining Page identity or encoding hierarchy paths as keys. That identity is compatible with a future `personalworkspace://item/{guid}` resolver, but no deep-link registration/resolver is added now. Phase 12 explicitly does not implement Canvas, widgets, layout, coordinates/sizes, zoom/pan, rich text, images, containers, drawing, attachments, templates, global search or export. No opaque Page body or premature Canvas schema exists. Phase 13 was not started.

Final Phase 12 verification: two full-solution builds passed with zero warnings/errors and both complete test runs passed 459/459 tests (33 added, including the migration 9 hash check). Final-binary native checks verified creation, three-level breadcrumbs, ancestor/child navigation, parent-target exclusion, lifecycle and the empty content surface; native screenshots were inspected. The two named acceptance profiles were deleted through native confirmation, Personal was restored, and temporary Phase 12 scripts/captures were removed. Git status/diff were reviewed and tracked/new-file whitespace checks passed. No commit or push was performed.

## Phase 13 — Canvas layout engine

Phase 13 adds layout infrastructure to existing Page identities. It does **not** implement real content Widgets, Task/Tracker bindings, charts, rich text, images, templates or other Phase 14 content. Empty Blocks are neutral layout hosts, not WorkspaceItems or fake user records. Containers are layout primitives with an optional plain-text title of up to 120 characters.

### Geometry, grid and collision rules

Each Page has a finite 65,536 × 65,536 logical-unit canvas. Geometry consists of integer X/Y/Width/Height on an invisible 8-unit grid; minimum dimensions are 64 × 48. Coordinates never include display zoom. Default Blocks are 192 × 128 and Containers 512 × 384. Snapping rounds nearest grid units, with midpoint ties away from zero. Invalid negative positions, unsupported numeric values and bounds/minimum violations are rejected.

Strict axis-aligned rectangle collision checks apply only to siblings sharing the same parent coordinate space. Touching edges are allowed; positive-area overlap is not. A proposed operation is validated in memory before persistence, including every selected item against unselected siblings. Group movement applies one shared snapped delta, preserves relative spacing and succeeds or fails together. Container resize also validates existing child bounds. Invalid previews show a danger outline and a textual error; release restores the pre-gesture geometry without writing. Escape cancels an unfinished gesture.

Creation and explicit reparenting find a deterministic nearby free grid position. Candidate X/Y coordinates come from the desired point, boundaries and sibling rectangle edges, sorted by squared distance and then Y/X. This avoids scanning the enormous grid. If no candidate fits, the operation fails without changing data. Movement also supports temporary horizontal/vertical alignment guides for sibling edges and centers within eight logical units, restricted to deltas that preserve the grid. Guides do not override collision validation. Resize uses grid snapping.

### Containers and selection

Root item coordinates are relative to the Page canvas. Block coordinates inside a Container are relative to its content origin, offset eight units horizontally and forty vertically from the Container origin. Content bounds reserve eight units at the sides/bottom. Moving a Container updates only that Container; children retain relative geometry, identity and timestamps while moving visually with it. Containers inside Containers are rejected by domain rules and database constraints.

Click selects; Ctrl+click toggles siblings. Attempting to mix coordinate spaces replaces the selection with the clicked item. Dragging a selected item moves the sibling group. Selected individual items expose right, bottom and bottom-right resize handles; proportional group resize is not implemented. Empty-canvas click clears selection. Delete removes selected layout items when unlocked; arrows move by one grid unit, Shift+arrow by ten. Text inputs and destination pickers retain their ordinary keyboard behavior. Accessible names describe selection, geometry, zoom, lock and resize controls; validation feedback is announced through a polite live region.

Membership uses an explicit destination picker and action. It preserves absolute position if legal, otherwise chooses a nearby free position in the destination space. Removing a child usually needs a new root position because the surviving Container occupies its previous absolute position. Deleting Containers atomically detaches their direct children to root with exact absolute geometry, then removes only the Containers. If the resulting layout cannot be validated, deletion is rejected; children are never silently deleted. Deleting an Empty Block affects only its layout row.

### Persistence, migration and lifecycle

Workspace migration **11**, `Create canvas layout`, adds `PageCanvasSettings` and `PageCanvasItems` plus an index and parent-validation triggers. Migrations 1–10 remain unchanged and have regression hash coverage. Nothing is stored in the global app database. Settings are keyed by PageId and hold LayoutLocked and ZoomPercent. Defaults (locked, 100%) are virtual: opening an empty Page writes no rows; non-default settings create a row, and returning to both defaults removes it.

Canvas items have stable GUID Id, PageId, optional ParentContainerId, Kind, integer geometry, optional Container title, CreatedAtUtc and UpdatedAtUtc. UTC timestamps remain internal. Page and parent foreign keys enforce ownership; a composite parent/Page key prevents cross-Page containment. Child links use NO ACTION rather than cascading Container deletion. Page deletion cascades the entire owned layout and settings, including Container children, safely in the existing Page transaction.

`CanvasLayout` and `CanvasSnapping` implement pure geometry/command rules. `ICanvasService` validates the originating Profile under the shared workspace operation gate. `SqliteCanvasRepository` loads only the selected Page, using a fixed three-query snapshot (Page state, settings, indexed items). A completed command reads/validates a fresh snapshot within an immediate transaction and writes changed rows only. Group movement, containment and safe Container deletion are atomic. Pointer previews use the loaded snapshot without database reads or writes; persistence happens once on successful release. Storage errors roll back and present a friendly error.

Layout geometry/title/membership changes advance affected Canvas item timestamps monotonically, preserving CreatedAtUtc. Child timestamps do not change when their Container moves. No-op geometry changes preserve timestamps. Layout edits do not churn WorkspaceItem/Page metadata timestamps. Archive/trash preserves all Canvas rows and presents the layout read-only; restore brings back the same items/settings. Phase 12 definition-only Page duplication remains unchanged: a new Page has an empty Canvas, with virtual default settings.

### Viewport, navigation and interaction

The native WinUI ScrollViewer retains horizontal/vertical scrolling, ordinary wheel navigation and Shift+wheel horizontal navigation. A deterministic ScaleTransform displays the logical surface within a correspondingly scaled extent. Native gesture zoom is disabled so asynchronous ScrollViewer zoom events cannot race with restoration or pointer coordinates. Explicit minus/100%/plus buttons and Ctrl+wheel apply 25-point increments from 25% to 200%, focusing around the viewport center or pointer. These commands persist Page-specific zoom without changing item geometry. Restoring a view never persists a setting.

Viewport scroll offsets are intentionally not persisted; Page/profile changes return to the canvas origin while restoring that Page's zoom and lock. Selection, previews, guides and loaded items clear on navigation/profile switching. Revision/reference checks discard late load/save results without leaking old layout into the new Page. Completed commands remain associated with their originating Page. Canvas busy state participates in existing Page/window-close coordination. The canvas keeps fixed toolbar/feedback space so selection and errors cannot shift the pointer origin during a gesture; focus does not scroll the surrounding Page during dragging.

### Performance, extension and limitations

The collision implementation uses straightforward sibling scans, approximately O(k × n) for k changed items, and is replaceable with a spatial index later. Placement considers edge-coordinate combinations rather than all grid cells. Selected-Page queries avoid loading layouts from other Pages. Rendering reuses item borders; previews run entirely in memory. Automated coverage includes a 500-item layout and repeated previews, as well as zero-write intermediate frames, rollback, migration, lifecycle, isolation and late asynchronous responses.

Discrete `CanvasEdit` commands (create, move, resize, delete, membership, title, lock and zoom) pass through one transactional edit boundary. Future Undo/Redo can wrap commands and retain before/after snapshots without moving geometry rules into UI or redesigning persistence. Future `WidgetInstance` records can attach to a stable Canvas item GUID through a unique foreign key. Geometry/containment stay in Canvas; content/configuration stay in Widget-specific infrastructure. There are no Widget tables or nullable TaskId/TrackerId/etc. columns in this phase. Future content-backed deletion must explicitly decide whether a Widget survives removal; today's Empty Block deletion does not predefine destructive content behavior.

Phase 13 limitations: no nested Containers, selection marquee, proportional group resize, eight-direction handles, custom drag panning, touch pinch zoom, persisted scroll offsets, spatial virtualization, full Undo/Redo or content widgets. The viewport is a compact fixed-height area within Page detail; large layouts use native scrolling/zoom. Hundreds of items are covered, not unbounded tens of thousands. Alternate DPI, touch and complete screen-reader traversal need additional device-level verification. Phase 14 has not been started.

Final Phase 13 verification: two final full-solution builds passed with zero warnings/errors, and both complete test runs passed 500/500 tests (41 added). Native Windows UI Automation and pointer/keyboard acceptance, with read-only database checks, covered empty viewing without rows; three-block placement; grid move and collision rollback; right/bottom/corner resizing; sibling group movement and atomic rejection; Container title, relative children, movement, shrink rejection, membership and safe deletion; lock protection; arrows/Delete/Escape, active-gesture cancellation and text-input protection; Ctrl+wheel and geometry-independent zoom; scrolling; Page navigation, archive/trash restoration, empty duplication and permanent deletion; Profile isolation; and restart persistence. Final-binary checks and a visual screenshot inspection passed. Native findings led to stable toolbar space, passive resize handles, deterministic display scaling, persistent canvas keyboard focus and a stationary-click snapping regression test. The two named temporary profiles/workspaces were deleted through native confirmation and Personal was restored. No packages were added; no commit or push was performed.

## Phase 14 — Page Widgets

### Content identity and persistence

Canvas geometry remains generic. `CanvasItem` owns position, size, Container membership and layout timestamps. A Block may host zero or one `WidgetInstance`; Containers remain layout primitives. Each Widget has its own stable GUID, distinct from its CanvasItem, source and Page. `WidgetRules` chooses initial sizes through the existing Canvas create command's optional generic dimensions. All placement, collision, resize, containment and movement validation remains in `CanvasLayout`.

Workspace migration **12**, `Create page widgets`, adds `WidgetInstances` and `WidgetChartSettings`. Migrations 1–11 are unchanged, with hash regression coverage. No global database tables or packages are added. WidgetInstances stores Id, unique CanvasItemId, WidgetType, nullable/non-unique SourceItemId, validated PresentationMode and UTC creation/update timestamps. SourceItemId references the generic WorkspaceItems identity; there are no per-entity nullable foreign-key columns or opaque configuration JSON. Database constraints/triggers enforce compatible source types, Block hosts, immutable identity, supported modes and no self PageLink. Multiple Widgets can reference the same source across Pages and Containers.

WidgetChartSettings is a typed one-to-one table keyed by WidgetId, with chart kind and bounded range preset. Changing these settings never changes the Tracker definition or the separate Analytics UI. Widget configuration changes advance only Widget UpdatedAtUtc. Canvas movement/resizing advances only layout timestamps. Displaying Widgets does not change any timestamps, generate occurrences, or create Journal entries.

### Supported presentations and canonical services

| Widget | Modes | Canonical behavior |
| --- | --- | --- |
| Task | Card, Checkbox, Progress | TaskService supplies definitions, completion guards and hierarchy progress; TaskValuePresentation formats values. |
| Tracker | CurrentValue, Progress, QuickEntry | TrackerService supplies canonical period aggregation and Single upsert/Multiple insert entry semantics. |
| TrackerChart | Chart: Line, Bar, Area | TrackerAnalyticsService supplies immutable bounded series; the native renderer only plots points. |
| Event | Card | EventService supplies local dates/times, all-day/multi-day data and past context; Open routes to the existing editor. |
| Journal | TodaySummary | JournalService and existing JournalPresentation supply today's lazy state and compact field summary. |
| PageLink | Card | PageService supplies current title/icon/ancestry; stable GUID navigation survives rename/move. |

An ordinary checkbox Task can complete/reopen through TaskService, including all dependency/subtask guards. Value and subtask progress stay separate. A recurring definition is never a global checkbox: `RecurrenceService.ReadDayAsync` reads only existing, visible executions for the local day. A single eligible checkbox occurrence gets an explicitly today-only action; zero or multiple occurrences require opening the existing series UI. Value occurrences display the existing canonical Actual/EffectiveTarget; Widgets do not implement carry calculations or recurrence materialization.

CurrentValue may fall back to the latest recorded period, whose date remains visible. Progress and QuickEntry use the current period and preserve missing versus zero. Ended/not-started/no-period states disable entry as appropriate. Quick Entry uses the shared input parser and TrackerService validation. Chart ranges are 7, 30 (default), 90 days or This month, using canonical local-date Analytics semantics. Boolean Trackers are excluded from numeric charts. Missing chart points remain gaps; no artificial zero values are introduced. Charts expose accessible point descriptions and tooltips.

### Editing, interaction and lifecycle

Unlocked layouts offer Add Widget with a type-specific title-filtered source picker and mode/settings. New sources must be active and profile-local; PageLink excludes/rejects its own Page. Atomic creation makes a host plus Widget, or attaches content to an existing Empty Block without changing geometry. Remove Widget content returns the host to an Empty Block; Remove from Page removes the Widget and host. Neither removes source data. Widget duplication creates fresh Widget/Canvas GUIDs, copies presentation settings, references the same source and uses existing nearest-free placement. Page duplication retains the approved empty-Page behavior.

The explicit Widget header selects/drags layout when unlocked. Checkboxes, entry fields, open actions and chart points retain ordinary content interaction and keyboard behavior. Lock hides configuration/removal controls and blocks movement/resizing, while source actions remain interactive. Content is clipped to its host and vertically scrolls when compact; titles ellipsize with full-title tooltips. Existing design brushes/styles are reused.

Archived/trashed sources keep their Widgets and layout, with a clear read-only lifecycle message. Restore resolves the same source again. Permanent source deletion uses `ON DELETE SET NULL`: the Widget remains a Missing reference placeholder with replacement/configuration and removal actions when unlocked. Replacement keeps Widget/Canvas identities and geometry. Page archive/trash preserves layout/content and makes the Page read-only; restoration returns the original Widgets. Permanent Page deletion cascades owned Canvas/Widget/settings rows, never the referenced source items. Container deletion continues to detach children safely rather than deleting them.

### Transactions, refresh and isolation

WidgetService validates the originating Profile under the shared workspace gate. SqliteWidgetRepository reads fresh state in an immediate transaction, applies pure WidgetRules, then persists changed content and geometry together using the existing Canvas repository's shared read/write routines. Creation, duplication, conversion, replacement/configuration and either removal mode roll back together on failure. Source edits run exclusively through their approved services and transaction boundaries.

Page navigation/return reloads Widgets, so edits made in Today, Task/Tracker details, Journal or Page details are reflected on return. A successful Widget action refreshes every Widget on the visible Page. The existing minute timer checks the local date in memory; it queries Widget data only on a day change, not continuously. Page/profile changes clear rows, selections, source snapshots and open configuration UI. Revision, originating Profile and current-row checks discard late results and reject stale actions. Other Pages resolve current canonical values when opened; there is no background polling of inactive Pages.

Loading batches Widget/settings rows and generic source metadata for the selected Page. Source summaries resolve once per type through existing services. Task and Page graphs and Event metadata are read in batches; no complete execution history is loaded. Tracker's new batch read deduplicates source IDs, chunks at 400, and reads only the current and latest period entries before invoking its existing aggregation helper. Journal uses the existing one-day summary read. Chart reads are bounded and cached once per distinct source/range for that refresh. A source-type/chart error stays local and does not prevent other Widget types rendering.

### Verification and boundaries

Automated Phase 14 coverage adds 36 cases to the 500-test baseline: migration/legacy hashes, atomic rollback, identity/host/source constraints, conversion/removal, timestamps, lifecycle, duplication, Container geometry, lock/content guards, canonical task/recurrence/carry/Tracker/Analytics/Journal/Event/PageLink behavior, batch resolution and late Page/profile loads. No diagnostic endpoints or acceptance hooks are part of the application.

Phase 14 uses compact summaries and existing detail editors; it does not embed full source forms. Layout limitations from Phase 13 remain, including fixed-height viewport, no nested Containers, virtualization or full Undo/Redo. Small Widgets scroll their content; rendering all configured Widgets is intended for practical dashboards rather than unbounded Pages. Touch, alternate DPI and complete screen-reader traversal require further device-level testing. Numeric charts support Line/Bar/Area only; Boolean charts and Pie/Donut are excluded.

Future Widget types extend the Widget type/mode validation, resolver/presentation, compatible generic source rules and, only where needed, a typed settings table/new migration. They do not change Canvas geometry or move source business logic into Pages. Lists, RichText, Images, Tables, Attachments, Drawing and Web embeds remain deferred, as do connectors, templates, duplication with content, global search and export. **Phase 15 has not been started.**

Final Phase 14 verification: both final full-solution builds passed with zero warnings/errors; both complete automated test passes passed **536/536**. Native Windows UI Automation, pointer input and read-only SQLite checks covered all six Widget types, shared Task completion and Water Quick Entry while locked, external Tracker edit refresh, chart configuration, lazy Journal rendering, PageLink navigation/rename, restart persistence, source archive/trash/restore/permanent deletion/null replacement, stable Empty Block conversion, both removal modes for every source type, Container move/collision/lock behavior, Page/profile isolation, Page lifecycle and empty duplication. Database checks found no orphan Widgets, duplicate hosts, invalid source references or foreign-key violations; the source index remains non-unique. Final dashboard, Container and chart/Event layouts were visually inspected. The two temporary profiles/workspaces were removed through native confirmation and Personal was restored. No application diagnostic code, packages, commit or push were added.
