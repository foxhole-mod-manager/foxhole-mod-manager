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

// (2026-11-18) the public mod manager's own details (antivirus heuristics and Windows show these)
[assembly: AssemblyTitle("Foxhole Mod Manager (unofficial)")]
[assembly: AssemblyDescription("Foxhole Mod Manager (unofficial) - cosmetic mods for Foxhole")]
[assembly: AssemblyCompany("Foxhole Mod Manager project (unofficial fan project)")]
[assembly: AssemblyProduct("Foxhole Mod Manager (unofficial)")]
[assembly: AssemblyCopyright("Unofficial fan project - not made or endorsed by the developers of Foxhole")]
// (the public app's version: its update check compares it with update.json - 1.1.0, 2026-11-18: the offline sandbox taken out)
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]
[assembly: ComVisible(false)]
