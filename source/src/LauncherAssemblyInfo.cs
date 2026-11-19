// Foxhole Mod Manager (unofficial)
// Copyright (C) 2026 Foxhole Mod Manager contributors
// SPDX-License-Identifier: GPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
// version 3 as published by the Free Software Foundation. This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License (LICENSE) for more details.

using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Foxhole Mod Manager (unofficial)")]
[assembly: AssemblyDescription("Foxhole Mod Manager (unofficial) - adds, checks and switches cosmetic mods for Foxhole")]
[assembly: AssemblyCompany("Foxhole Mod Manager contributors")]
[assembly: AssemblyProduct("Foxhole Mod Manager (unofficial)")]
[assembly: AssemblyCopyright("Copyright (C) 2026 Foxhole Mod Manager contributors")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
// (the public app's version: its update check compares it with update.json - 1.1.0, 2026-11-18: the offline sandbox taken out;
//  1.1.1: no .7z support, fewer Windows calls, complete version details - fewer things for antivirus heuristics to react to;
//  1.1.2: sharp text on scaled screens (DPI aware), rounded window with a silver edge, mods found in the game shown on Installed)
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]
[assembly: AssemblyInformationalVersion("1.1.2")]
[assembly: ComVisible(false)]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
