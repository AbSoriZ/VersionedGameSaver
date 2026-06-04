# Versioned Game Saver

Windows desktop app for local, versioned game-save snapshots.

## Current MVP

- Choose a portable backup library folder.
- Manually scan for preset games.
- Add manual game profiles.
- Track whole save folders, custom folders, or single save files.
- Create quick ZIP snapshots without a naming prompt.
- Edit snapshot names and notes later.
- Restore selected versions after creating an automatic safety snapshot.
- Keep safety snapshots in a separate tab.
- Overwrite the selected manual version.
- Delete versions, scopes, or game backups with confirmation.

## Run

```powershell
cd VersionedGameSaver
dotnet run
```

## Notes

The first implemented preset is Project Zomboid, using the common Windows save path:

```text
%USERPROFILE%\Zomboid\Saves
```

The scanner adds profiles only when confirmed save folders exist on disk. Registry-based saves, scheduled backups, and full Ludusavi manifest ingestion are future work.
