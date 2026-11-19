# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).
*(Applies once the SignPath Foundation application is approved; until then releases are unsigned.)*

## What is signed
Only `FoxholeModManager.exe`, built by this repository's GitHub Actions workflow ([`.github/workflows/release.yml`](.github/workflows/release.yml))
from the source code in [`source/`](source) at the release's tag. Nothing built on a personal computer is signed, and no third-party
program is signed.

## Team and roles
- **Committer and reviewer:** the project maintainer (members of the [foxhole-mod-manager](https://github.com/foxhole-mod-manager)
  organization). Changes from anyone else are reviewed by the maintainer before they are merged.
- **Approver:** the project maintainer approves every signing request by hand in SignPath.
- All team members use multi-factor authentication for GitHub and SignPath.

## Privacy
This program will not transfer any information to other networked systems unless specifically requested by the user or the person
installing or operating it. In detail (see the [README](README.md#privacy)): the mod browser and the optional update check read public
data from Nexus Mods and from this repository's `update.json`; no account, no Steam ID, no tracking, no analytics, no crash reports.

## System changes and uninstall
The app copies the mod files you choose into Foxhole's mod folder and takes them out again when you switch a mod off or remove it. It
does not install anything into Windows. Uninstall: switch your mods off, then delete the app's folder and
`%LOCALAPPDATA%\FoxholeModManager`.
