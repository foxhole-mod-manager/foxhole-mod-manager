## Foxhole Mod Manager (unofficial) 1.1.2

Fan-made. Not affiliated with or endorsed by Siege Camp. Foxhole is a trademark of Siege Camp.

**Download:** `FoxholeModManager.zip` below - unzip it anywhere and start `FoxholeModManager.exe`. `FoxholeModManager.zip.sha256` holds
the zip's SHA-256 checksum. Keep the app in its own folder (not inside the game's folders).

### What's new in 1.1.2
- **Mods found in your game.** *Installed* now lists mods that were put into Foxhole by hand or by another tool ("Found in your game",
  with the same compatibility check). *Manage with the app* takes one over - it stays on and can then be switched off, removed and
  updated like any other. Archives left in the Paks folder get a hint; a mod loader (e.g. UE4SS) next to the game gets a warning - the
  app never changes those.
- **Sharp text at any display scaling** (125 %, 150 %...), crisper body text, brighter secondary text.
- **Rounded window corners and a thin silver edge** on Windows 11.
- **Safer by design:** every Windows function the app uses is loaded only from Windows' own system folder.

### From 1.1.1
- **A quieter program.** Fewer things for antivirus heuristics to react to: the app no longer downloads or starts any helper tool,
  declares fewer Windows functions, and carries complete version details and its own application manifest (runs as the normal user,
  never asks for administrator rights).
- **`.7z` mods must be unpacked first.** The app reads `.pak` and `.zip` files only and downloads no extra tools. Unpack a `.7z` with
  7-Zip, then add the `.pak` (or zip the unpacked files and add the `.zip`). Mods added that way have no Nexus Mods update tracking.
- **The offline sandbox of 1.0.0 stays removed.** If you used it: open the app once and press **Restore the game** (shown while the
  app finds the sandbox's changes in your Foxhole) - it puts the original game files back. Steam's *Verify integrity of game files*
  also repairs the game.
- **Mod updates and Nexus pictures** in *Installed* switch on once Nexus Mods approves the app's in-app browser. Until then a newer
  download of an installed mod is recognised in your Downloads folder, on your PC.

### License
The app is free software under the GNU GPL version 3 only (GPL-3.0-only, `LICENSE.txt`); the source code of this release is in this
repository's `source/` folder at this release's tag, and this zip was built from it by the repository's GitHub Actions workflow.

### About AI
Developed with an AI coding assistant; checked with automated tests, independent AI code reviews and testing in the game. No
AI-generated art, voices or game content.
