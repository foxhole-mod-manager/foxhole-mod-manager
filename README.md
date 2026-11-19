<p align="center"><img src="logo.png" width="128" alt="Foxhole Mod Manager logo"></p>

# Foxhole Mod Manager (unofficial)

A fan-made Windows app for **Foxhole's cosmetic mods**.

> Fan-made. Not affiliated with or endorsed by Siege Camp. Foxhole is a trademark of Siege Camp.

> **About AI:** developed with an AI coding assistant; checked with automated tests, independent AI code reviews and testing in the game. No AI-generated art, voices
> or game content.

## What it does
- **Play** - starts the live war through Steam with the cosmetic mods you switched on.
- **Mods** - add `.pak` mods and settings files (`.pak`, `.zip` or `.7z`), switch them on and off, remove them. Each one is
  checked against your game version first: it tells you whether a mod loads on official Foxhole, clashes with another mod or
  will not load. Archives are unpacked safely (nothing outside the mod's own folder, no links, size limits) and only `.pak` and
  the known settings files are ever kept. Nothing from an archive is ever run.
- **Browse mods** - Foxhole mods on Nexus Mods as cards, by category, searchable. *Download* opens the mod's page in your own
  browser; the finished file is offered for adding.

The app never loads anything into the game and never changes the game's program (only *Restore the game* puts original files back, for players of the old 1.0.0 sandbox): mods are files in the game's mod folder, and
*Play* starts Foxhole through Steam as it is.

**The offline sandbox from 1.0.0 is removed for now.** If you used it, open the app once and press **Restore the game** (the
sidebar shows the button while the app finds the sandbox's changes in your Foxhole): it puts the original game files back. Steam's
*Verify integrity of game files* also repairs the game. *Play* refuses while the game still has the sandbox's changes.

## Requirements
Windows 10 or 11 (64-bit), Foxhole from Steam, .NET Framework 4.7.2 or newer (part of Windows 10 / 11).

## Install
1. Download the latest zip from [Releases](../../releases).
2. Unzip it anywhere and start `FoxholeModManager.exe`.
3. Windows may show *"Windows protected your PC"* the first time (the app is new and not code-signed): *More info* -> *Run anyway*.

## Update
*Check for updates* tells you when a new version is out and opens its page. Unzip the new version into a new folder and delete the old one (1.0.0 left extra sandbox files in its folder).
Your mods and settings live in `%LOCALAPPDATA%\FoxholeModManager` and are found again.

## Uninstall
1. If you used the offline sandbox of 1.0.0: press **Restore the game** first (or use Steam's *Verify integrity of game files*).
2. Switch your mods off if you want them gone from the game, then delete the app's folder and `%LOCALAPPDATA%\FoxholeModManager`.

## Bugs and questions
Report problems on the mod's **Bugs** tab on Nexus Mods. Please add what you did, what happened, and the lines from the app's
**Activity** page (check them before you post). The app sends no crash reports by itself.

## Privacy
No account, no Steam ID and nothing about your PC is sent anywhere; no tracking, no analytics, no crash reports. The app talks to
the internet only for:
- **Browse mods** - asks Nexus Mods for its public list of Foxhole mods and their pictures (Nexus sees your IP address and the
  app's name, as any website does).
- **Check for updates** - reads one small file (`update.json`) from this repository: the newest version.
- **Mod updates** (only once the in-app browser is switched on, after Nexus Mods approves the app) - about once an hour, and when
  you press *Check for mod updates*, asks Nexus Mods for the newest files of the mods you added from Nexus. The request holds only
  those mods' Nexus numbers; *Installed* then also shows those mods' pictures from Nexus' image servers. Before that, a newer download
  of an installed mod is recognised in your Downloads folder, on your PC, and *Installed* shows only pictures already on your PC.
- Adding a `.7z` mod - downloads 7-Zip's `7zr.exe` once from www.7-zip.org.

Mod lists, thumbnails and your settings stay on your PC in `%LOCALAPPDATA%\FoxholeModManager`.

## License
The app (`FoxholeModManager.exe`) is free software under the [GNU GPL version 3 only (GPL-3.0-only)](LICENSE); its source code is
in [`source/`](source) (each release's tag holds the source of that release).

## Third-party software
None is included. 7-Zip's `7zr.exe` is not part of the download: the app downloads it from www.7-zip.org (checksum-checked) only
when you add a `.7z` mod.
