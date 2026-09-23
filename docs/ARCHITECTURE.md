# PersonalWorkspace architecture

The foundation sections below record the approved Phase 0 design. The **Phase 1: Local Profiles** section extends it and supersedes the historical statements about profile storage and multiple application instances.

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
