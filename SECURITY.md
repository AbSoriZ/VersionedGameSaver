# Security Policy

## Supported Versions

Only the latest public release is actively supported.

## Reporting A Vulnerability

Please report security issues privately before opening a public issue. If GitHub private vulnerability reporting is enabled for this repository, use that. Otherwise, contact the repository owner through GitHub.

Include:

- A clear description of the issue.
- Steps to reproduce it.
- The affected version or commit.
- Whether the issue can cause save-data loss, arbitrary file overwrite, unsafe restore behavior, or unexpected file deletion.

## Save-Data Safety

Versioned Game Saver reads, archives, restores, overwrites, and deletes local files selected by the user. Bugs in those paths can affect real game saves, so reports involving restore, overwrite, delete, or archive extraction behavior are treated as high priority.
