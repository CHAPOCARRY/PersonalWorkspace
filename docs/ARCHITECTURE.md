# Phase 0 architecture

## Scope

PersonalWorkspace is an offline Windows x64 desktop application built with C#, .NET 10, WinUI 3, MVVM, dependency injection, and SQLite. There are no cloud dependencies, accounts, subscriptions, remote databases, analytics clients, or telemetry services in the application. NuGet access is needed for development restore, not application use. The .NET CLI's own telemetry can be disabled with `DOTNET_CLI_TELEMETRY_OPTOUT=1`.

Tasks, Calendar, Trackers, Journal, Pages, Lists, Today, Archived, Trash, and Settings are navigation placeholders. SPACES is a disabled section. Search and Add are disabled controls. No Phase 1 models, tables, or workflows exist.

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

Only `app.db` exists. `SchemaMigrations` records version, name, and UTC application time. Migration 1 creates `AppSettings` with a unique text primary key, JSON value, and UTC update timestamp. There are no profile/workspace databases.

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
5. Inspect `%LOCALAPPDATA%\PersonalWorkspace` for one database, empty Profiles/Backups folders, and structured startup/migration/shutdown logs. A second launch must not apply migration 1 again.
6. Test offline launch, narrow windows, high DPI, and screen-reader labels. Visual and accessibility checks require an interactive Windows session.

## Phase 0 limitations

Only x64/unpackaged delivery and one light theme are configured. There is no installer, window-coordinate restoration, navigation history, profile storage, backup behavior, or business functionality. Window geometry and view-model/WinUI bindings require manual integration checks; automated tests intentionally target non-UI infrastructure. Multiple instances can access SQLite safely at the transaction level but do not live-synchronize shell preferences. Schema upgrade backups and single-instance activation can be decided in future phases.
