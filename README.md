# PersonalWorkspace

A local-first Windows desktop application. This repository currently implements **Phase 0: Foundation** only. All feature destinations are placeholders.

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

Data lives under `%LOCALAPPDATA%\PersonalWorkspace`: `app.db`, `Logs`, `Backups`, and `Profiles`. The last two folders are empty reserves; backup/profile functionality is not implemented. Logs are local JSON lines, rotated daily/by size with at most 14 retained files.

See [architecture and manual verification](docs/ARCHITECTURE.md).
