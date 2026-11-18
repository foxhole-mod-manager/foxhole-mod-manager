<p align="center"><img src="logo.png" width="128" alt="Foxhole Mod Manager logo"></p>

# Foxhole Mod Manager (unofficial)

A fan-made Windows app for **Foxhole's cosmetic mods** and a **solo offline sandbox**.

> Fan-made. Not affiliated with or endorsed by Siege Camp. Foxhole is a trademark of Siege Camp.

## What it does
- **Mods** - add `.pak` mods and settings files (`.pak`, `.zip` or `.7z`), switch them on and off, remove them. Each one is
  checked against your game version first: it tells you whether a mod loads on official Foxhole, clashes with another mod or
  will not load. Archives are unpacked safely (nothing outside the mod's own folder, no links, size limits) and only `.pak` and
  the known settings files are ever kept.
- **Browse mods** - Foxhole mods on Nexus Mods as cards, by category, searchable. *Download* opens the mod's page in your own
  browser; the finished file is offered for adding.
- **Play offline** - a solo sandbox on your own PC: pick a hex and a faction. Nobody else can join (the sandbox server only
  answers this PC, and only one player). Your game is changed **only while the sandbox runs** and is put back to original when
  you close the game.

## Install
1. Download the latest zip from [Releases](../../releases).
2. Unzip it anywhere (keep the folder together) and start `FoxholeModManager.exe`.

## Uninstall
Press **Restore the game** (or use Steam's *Verify integrity of game files*), then delete the folder.

## Privacy
No account, no Steam ID and nothing about your PC is sent anywhere; no tracking or analytics. The mod browser asks Nexus Mods
for its public list of Foxhole mods (Nexus sees your IP address and the app's name, as any website does). *Check for updates*
reads one small file from this repository. Lists and thumbnails stay on your PC.

## Third-party software
The package includes [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (MIT License). 7-Zip's `7zr.exe` is not included: the app
downloads it from www.7-zip.org (checksum-checked) only when you add a `.7z` mod.
