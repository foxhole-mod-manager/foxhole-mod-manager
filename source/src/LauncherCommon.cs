// Foxhole Mod Manager (unofficial)
// Copyright (C) 2026 Foxhole Mod Manager contributors
// SPDX-License-Identifier: GPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
// version 3 as published by the Free Software Foundation. This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License (LICENSE) for more details.

// SHARED HELPERS (2026-11-18): small pieces both editions use. They lived in files that are now private-launcher only (LauncherHost.cs,
// LauncherReport.cs, LauncherMapEditor.cs) and were moved here unchanged, so the public build (/define:PUBLIC) compiles without those files.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FoxholeModLauncher
{
    partial class Launcher : Form
    {
        static bool Headless;                       // (command-line mode: no window, everything to the console + launcher.log)

        // (1.1.1) the public app does not attach to a console: its command-line modes (tests) write to the output they are given - a
        // redirected stdout - and nothing shows in a plain console window. One Windows call less for antivirus heuristics.
        static bool AttachConsole(int pid) { return false; }

        // (also opens a file's ":Zone.Identifier" stream - the Mark of the Web, LauncherMods.cs)
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        static string SavedDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foxhole", "Saved"); } }

        static void SetOption(string key, object value)
        {
            var o = ReadJson(OptionsPath);
            o[key] = value;
            try { File.WriteAllText(OptionsPath, new JavaScriptSerializer().Serialize(o)); } catch { }
        }

        static Form DarkForm(string title, int w, int h)
        {
            var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false,
                MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(w, h), BackColor = Color.FromArgb(24, 24, 22),
                ForeColor = Color.FromArgb(226, 221, 206), Font = new Font("Segoe UI", 10f)
            };
            ScaleDialogOnLoad(f);                                        // (laid out in 96-DPI pixels: grown for the screen - LauncherWindow.cs)
            return f;
        }
        static TextBox DarkBox(int x, int y, int w, string text)
        {
            return new TextBox { Left = x, Top = y, Width = w, Text = text, BackColor = Color.FromArgb(40, 40, 37), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        }

        // Steam's libraries (registry + libraryfolders.vdf) -> steamapps\common\Foxhole
        static string FindFoxholeSteam()
        {
            var roots = new List<string>();
            foreach (var k in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            {
                var v = Registry.GetValue(k, k.StartsWith("HKEY_CURRENT") ? "SteamPath" : "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(v)) roots.Add(v.Replace('/', '\\'));
            }
            var libs = new List<string>(roots);
            foreach (var r in roots)
            {
                string vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            foreach (var l in libs)
            {
                string g = Path.Combine(l, "steamapps", "common", "Foxhole");
                if (File.Exists(Path.Combine(g, "War", "Content", "Paks", "War-WindowsNoEditor.pak"))) return g;
            }
            return "";
        }

        bool busy { get { return false; } }

        // the game folder (the private edition's MeGameDir in LauncherMapEditor.cs also asks the kit, which the public edition does not have):
        // launcher_options.json "game_dir" when that folder exists, else Steam's Foxhole
        string MeGameDir()
        {
            string o = Str(Options(), "game_dir");
            if (o != "" && Directory.Exists(o)) return o;
            return FindFoxholeSteam();
        }
    }
}
