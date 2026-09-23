# PersonalWorkspace

A local-first Windows desktop application. This repository implements **Phase 0: Foundation** and **Phase 1: Local Profiles**. Tasks, Calendar, and other business-feature destinations remain placeholders.

## Development

Windows x64 and .NET SDK 10.0.303 (or a later 10.0.3xx patch) are required. The first restore downloads NuGet dependencies, including the Windows build tools. Application use requires no network or account.

```powershell
dotnet build PersonalWorkspace.sln
dotnet test PersonalWorkspace.sln --no-build
dotnet run --project src/PersonalWorkspace.App --no-build
```

The development build requires the .NET 10 runtime. Windows App SDK is included in the output. To include the .NET runtime too:

```powershell
dotnet publish src/PersonalWorkspace.App -c Release -r win-x64 --self-contained true
```

Copy the entire publish directory; the executable is not a standalone single file. No installer or MSIX packaging is provided yet.

Data lives under `%LOCALAPPDATA%\PersonalWorkspace`: global `app.db`, `Logs`, `Backups`, and `Profiles`. Each profile has `Profiles/{guid}/workspace.db` and an empty `attachments/` directory. Backups and attachment functionality are not implemented. Logs are local JSON lines, rotated daily/by size with at most 14 retained files.

On first launch, enter a name to create your first local profile. The application reopens the last profile automatically on subsequent launches. Use the button at the bottom of the sidebar to switch, create, or manage profiles. Profile deletion requires confirmation and permanently removes its local files. Profiles have no authentication or online identity. Only one application process may use the data directory at a time.

See [architecture and manual verification](docs/ARCHITECTURE.md).
