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
`resources\launcher.manifest` (runs as the normal user, never asks for administrator rights).

`public_release.json` names the GitHub repository (`owner/name`) whose releases the app's update check reads (this project's own).
For a build of your own, put your repository there (the example: `public_release.example.json`) or delete the file: without it the app
still builds and runs; the update check then reports that it has no update source.

## Layout
- `src\` - the C# source (C# 5). `ExePatcher.cs` and `PakIni.cs` are the patch helpers the offline sandbox uses.
- `resources\` - the icon, the logo pictures and the application manifest.

## What is not in this source
The offline sandbox also uses separate, closed components that are shipped next to the app in its download: `FoxServer.dll`,
`FoxClient.dll`, `X3DAudio1_7.dll`, `patch.json`, the server address caches and `sandbox.txt`. They are separate programs under
their own license (`LICENSE-SANDBOX.txt` in the repository), not part of this program, and they are **not needed to compile it**.
Without them the app works for cosmetic mods and Browse mods; the offline sandbox needs them. UE4SS (MIT) is shipped with the
download too and keeps its own license.
