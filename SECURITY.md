# Security Policy

## Supported Versions

| Version | Supported | Jellyfin |
|---------|-----------|----------|
| 7.x | ✅ | 12.x |
| 6.x and older | ❌ | 10.11 and older (end of life) |

## Reporting a Vulnerability

Please use GitHub's [private vulnerability reporting](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/security/advisories/new)
("Security" tab → "Report a vulnerability") to report security issues. Please do not open a public
issue for vulnerabilities.

Useful detail to include: the plugin version, Jellyfin version, the log output, and — for anything
touching file handling — the path that was involved. Theme files are written only under
`theme-music/` and `backdrops/` next to the media, and file names arriving from the AnimeThemes API
are treated as untrusted (stripped to their last path segment).

## What this repository is covered by

- CodeQL code scanning (C# and GitHub Actions workflows)
- Secret scanning and push protection
- Dependabot alerts and security updates
- Dependency review on pull requests
