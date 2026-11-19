# Foxhole Mod Manager (unofficial) - building from source

This folder is the complete source of `FoxholeModManager.exe` (the Corresponding Source under the GNU General Public License
version 3, see `LICENSE`). Fan-made; not affiliated with or endorsed by Siege Camp. Foxhole is a trademark of Siege Camp.

## Build
Requirements: Windows 10 or 11 with the .NET Framework 4.7.2 or newer (part of Windows). No Visual Studio or .NET SDK is needed: the
script uses the C# compiler that ships with the .NET Framework (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).

    powershell -ExecutionPolicy Bypass -File build.ps1

The program is written to `bin\FoxholeModManager.exe` (`-Out <path>` for another place). It is compiled with `/define:PUBLIC`, the
references in `build.ps1` (Windows Forms, System.Drawing, System.Web.Extensions, System.IO.Compression and the WPF assemblies
PresentationCore / WindowsBase / System.Xaml), the icon `resources\logo.ico` and the application manifest
`resources\app.manifest` (runs as the normal user, never asks for administrator rights; Windows 10 / 11).

`public_release.json` names the GitHub repository (`owner/name`) whose releases the app's update check reads (this project's own).
For a build of your own, put your repository there (the example: `public_release.example.json`) or delete the file: without it the app
still builds and runs; the update check then reports that it has no update source.

## Layout
- `src\` - the C# source (C# 5). `PakIni.cs` reads the game pak's network settings (used by *Restore the game*).
- `resources\` - the icon, the logo pictures and the application manifest.

## What the app does to the game
Nothing is loaded into or started inside the game: *Play* starts Foxhole through Steam, cosmetic mods are files in the game's mod
folder, and *Restore the game* only puts back original files that the offline sandbox of version 1.0.0 changed (the game exe from its
backup, the network settings in the game pak, the items it added to `War\Binaries\Win64`, set-aside mod files) and removes its local
server copy. The offline sandbox itself is not part of this version.
