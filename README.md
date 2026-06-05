# Versioned Game Saver

Versioned Game Saver is a Windows desktop app for making local, versioned snapshots of game save data. It is built for players who want a simple way to back up saves before experimenting, modding, restoring old progress, or protecting against save corruption.

The app stores snapshots in a backup library folder that you choose. It can scan a bundled game-save manifest for known save locations, and it can also track manually added folders or individual save files.

## Download

Download the latest Windows x64 executable from the [GitHub Releases](https://github.com/AbSoriZ/VersionedGameSaver/releases) page.

The release executable is self-contained, so you do not need to install the .NET runtime separately.

## Unsigned Executable Notice

Versioned Game Saver releases are currently unsigned. Windows SmartScreen or antivirus software may warn that the publisher is unknown, especially for early releases.

If you prefer not to run the release executable, you can review the source and build the app locally with the commands below.

## Quick Start

1. Download `VersionedGameSaver.exe` from the [GitHub Releases](https://github.com/AbSoriZ/VersionedGameSaver/releases) page.
2. Run the executable.
3. Click `Choose Library` and select a stable folder where backups should be stored.
4. Click `Scan Games` to detect supported games and live save data from the bundled manifest.
5. Select a game in `Games`.
6. Select a save entry in `Live Save Data`.
7. Click `Back Up Selected` to create a new version.
8. Select a version later to view details, edit its alias or notes, `Restore`, `Overwrite`, or `Delete Version`.

Restoring a version modifies the live save files for the selected save entry. Versioned Game Saver creates a safety snapshot before restore, but you should still keep your backup library in a folder that will not be moved or cleaned up accidentally.

## Common Tasks

- Add a custom game: click `Add Custom Game`, then add save entries with `Add Folder` or `Add File`.
- Back up one save entry: select a game, select a live save entry, then click `Back Up Selected`.
- Back up everything: click `Back Up All Games` and choose how each game should be handled.
- Restore a version: select a version, confirm it is the right save entry, then click `Restore`.
- Overwrite a version: select a version and click `Overwrite` to replace that version with the current live save data.
- Rename a save entry or version: use `Edit Alias` from the right-click menu, or edit a selected version in the alias field.
- Add notes: select a version and type in the `Notes` box; notes autosave.
- Import an older backup: select the relevant save entry or version area, then use `Import Prior Save...`.
- Review safety backups: open the `Safety` tab to see snapshots created before restore operations.

## Features

- Choose a portable backup library folder.
- Scan a bundled game-save manifest for detected games and live save data.
- Add manual game profiles.
- Track whole save folders, custom folders, or single save files.
- Create quick ZIP snapshots without a naming prompt.
- View, sort, reorder, alias, overwrite, restore, and delete saved versions.
- View all versions for a game across all tracked save entries.
- Delete multiple selected versions together.
- Edit snapshot notes with autosave.
- Run backup and overwrite work in the background so the window stays responsive.
- Create an automatic safety snapshot before restoring a selected version.
- Keep safety snapshots separate from manual snapshots.
- Confirm destructive actions before deleting backups, save entries, or versions.

## Requirements

- Windows x64.
- .NET 8 SDK only if you want to build from source.

## Run From Source

```powershell
dotnet run
```

## Build A Portable EXE

```powershell
.\publish-portable.ps1
```

The script publishes a self-contained Windows x64 executable and copies it to:

```text
VersionedGameSaver.exe
```

That generated root-level executable is for local testing and manual sharing. Official public binaries are published through GitHub Releases.

## Safety Notes

Versioned Game Saver works with real save files. Before restoring, overwriting, or deleting anything important, make sure you understand which save entry and version are selected.

The app creates a safety snapshot before restoring a selected version, but you should still keep backups in a folder that will not be accidentally cleaned up by another tool.

## Current Limitations

- Windows x64 only.
- Release executables are unsigned.
- Runtime scanning is offline and uses the bundled manifest only.
- Registry-based save detection and scheduled backups are not currently implemented.
- The bundled manifest is sanitized for backup-location scanning, so launcher command metadata is omitted.

## Third-Party Notices

The bundled game-save manifest is sourced from the Ludusavi Manifest project and included under its MIT license. This project also uses YamlDotNet. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.

## License

Versioned Game Saver is released under the MIT License. See [LICENSE](LICENSE).
