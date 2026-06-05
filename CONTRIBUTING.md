# Contributing

Thanks for taking a look at Versioned Game Saver. This is a small Windows desktop app, so the best contributions are focused, tested changes that make backup and restore behavior clearer, safer, or more reliable.

## Setup

Install the .NET 8 SDK, then run:

```powershell
dotnet restore
dotnet build -c Release
```

To run the app locally:

```powershell
dotnet run
```

To create a local portable executable:

```powershell
.\publish-portable.ps1
```

## Pull Requests

- Keep changes scoped to one feature or fix.
- Avoid unrelated formatting churn.
- Include manual test notes for backup, restore, overwrite, and delete behavior when those areas are touched.
- Do not commit generated build output or release executables.
- Be careful with changes that affect real save files, deletion, restore behavior, or archive structure.

## Release Notes

Public releases are created from version tags such as `v0.1.0`. Release binaries are built by GitHub Actions and attached to GitHub Releases.
