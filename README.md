# Versioned Game Saver

Windows desktop app for local, versioned game-save snapshots.

## Current MVP

- Choose a portable backup library folder.
- Scan a bundled game-save manifest for detected games and live save data.
- Add manual game profiles.
- Track whole save folders, custom folders, or single save files.
- Right-click save entries to view details, edit aliases, clear aliases, or delete entries.
- Right-click versions to view details, restore, overwrite, or delete.
- Selecting an all-saves entry shows versions from every save entry under that game, prefixed with each save entry name.
- Versions are shown in columns with stable per-save-entry slots, version alias/name, save entry, date, file count, and archive size.
- Version columns can be sorted by clicking headers and reordered by dragging headers.
- Version aliases can be edited directly in the Alias column.
- Multiple selected versions can be deleted together.
- Create quick ZIP snapshots without a naming prompt.
- Backup and overwrite run in the background with a status message so the window stays responsive.
- Write snapshot notes directly in the notes box; they autosave.
- Restore selected versions after creating an automatic safety snapshot.
- Keep safety snapshots in a separate tab.
- Overwrite the selected manual version.
- Delete versions, save entries, or game backups with confirmation.

## Run

```powershell
dotnet run
```

## Create Portable EXE

Run:

```powershell
.\publish-portable.ps1
```

The shareable executable will be copied to:

```text
VersionedGameSaver.exe
```

That root-level `.exe` is self-contained for Windows x64, so it can be shared without asking users to install the .NET runtime separately.

## Notes

The bundled game-save manifest is sourced from the Ludusavi Manifest project and included under its MIT license. It is sanitized for backup-location scanning, so launcher command metadata is omitted. Runtime scanning is offline and does not require internet access or any external executable.
