<p align="center"><img src="logo.png" width="128" alt="Foxhole Mod Manager logo"></p>

# Foxhole Mod Manager (unofficial)

A fan-made Windows app for **Foxhole's cosmetic mods** and a **solo offline sandbox**.

> Fan-made. Not affiliated with or endorsed by Siege Camp. Foxhole is a trademark of Siege Camp.

> **About AI:** developed with an AI coding assistant; every change was reviewed and tested by a human. No AI-generated art, voices
> or game content.

## What it does
- **Play** - one button: **Live War** (the default) starts Foxhole through Steam with the cosmetic mods you switched on; the
  arrow on the button switches to **Singleplayer (offline sandbox)**.
- **Mods** - add `.pak` mods and settings files (`.pak`, `.zip` or `.7z`), switch them on and off, remove them. Each one is
  checked against your game version first: it tells you whether a mod loads on official Foxhole, clashes with another mod or
  will not load. Archives are unpacked safely (nothing outside the mod's own folder, no links, size limits) and only `.pak` and
  the known settings files are ever kept. Nothing from an archive is ever run.
- **Browse mods** - Foxhole mods on Nexus Mods as cards, by category, searchable. *Download* opens the mod's page in your own
  browser; the finished file is offered for adding.
- **Offline sandbox** - a solo game on your own PC: pick a hex and a faction and play the plain game. Nobody else can join (the
  sandbox server only answers this PC, and only one player). Your game is changed **only while the sandbox runs** and is put back
  to original when you close the game. Live War is never started while the game is changed.

## Requirements
Windows 10 or 11 (64-bit), Foxhole from Steam, .NET Framework 4.7.2 or newer (part of Windows 10 / 11). Steam must be running
for the offline sandbox.

## Install
1. Download the latest zip from [Releases](../../releases).
2. Unzip it anywhere (keep the folder together) and start `FoxholeModManager.exe`.
3. Windows may show *"Windows protected your PC"* the first time (the app is new and not code-signed): *More info* -> *Run anyway*.

## Update
*Check for updates* tells you when a new version is out and opens its page. Unzip the new version anywhere, or over the old
folder: your mods and settings live in `%LOCALAPPDATA%\FoxholeModManager` and are found again.

**After a crash during the offline sandbox** (the PC switched off, the app killed): open the app and press **Restore the game**
before you play again. Starting Foxhole directly from Steam bypasses the app - the game would still be the changed sandbox copy.
The app's own PLAY (Live War) refuses while the game is changed.

## Uninstall
1. Press **Restore the game** (or use Steam's *Verify integrity of game files*) - only needed if a sandbox did not end normally.
2. Switch your mods off if you want them gone from the game, then delete the app's folder and `%LOCALAPPDATA%\FoxholeModManager`.

## Bugs and questions
Report problems on the mod's **Bugs** tab on Nexus Mods. Please add what you did, what happened, and the lines from the app's
**Activity** page (check them before you post). The app sends no crash reports by itself.

## Privacy
No account, no Steam ID and nothing about your PC is sent anywhere; no tracking, no analytics, no crash reports. The app talks to
the internet only for:
- **Browse mods** - asks Nexus Mods for its public list of Foxhole mods and their pictures (Nexus sees your IP address and the
  app's name, as any website does).
- **Check for updates**, and once before each offline sandbox start - reads one small file (`update.json`) from this repository.
  It says the newest version and, if a Foxhole update breaks the sandbox, which game versions the sandbox should wait on.
- **Mod updates** (only once the in-app browser is switched on, after Nexus Mods approves the app) - about once an hour, and when
  you press *Check for mod updates*, asks Nexus Mods for the newest files of the mods you added from Nexus. The request holds only
  those mods' Nexus numbers; *Installed* then also shows those mods' pictures from Nexus' image servers. Before that, a newer download
  of an installed mod is recognised in your Downloads folder, on your PC, and *Installed* shows only pictures already on your PC.
- Adding a `.7z` mod - downloads 7-Zip's `7zr.exe` once from www.7-zip.org.

Mod lists, thumbnails and your settings stay on your PC in `%LOCALAPPDATA%\FoxholeModManager`.

## License
- **The app** (`FoxholeModManager.exe`) is free software under the [GNU GPL version 3 only (GPL-3.0-only)](LICENSE); its source code
  is in [`source/`](source) (each release's tag holds the source of that release).
- **The offline sandbox components** (the sandbox server and client programs shipped next to it, in compiled form only) are separate
  programs under their own freeware license: [LICENSE-SANDBOX.txt](LICENSE-SANDBOX.txt) - free to use with the app (streams and
  videos included), not to modify or reverse engineer; passing on the whole, unmodified download is allowed. The app's source builds and works without them (mods and Live War); they are only
  needed for the offline sandbox.
- Third-party parts keep their own licenses (below).

## Third-party software
The package includes [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (MIT License) and UE4SS's own Lua mods (listed in the
package's THIRD-PARTY.txt). 7-Zip's `7zr.exe` is not included: the app
downloads it from www.7-zip.org (checksum-checked) only when you add a `.7z` mod.
