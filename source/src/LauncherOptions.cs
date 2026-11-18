// Foxhole Mod Manager (unofficial)
// Copyright (C) 2026 Foxhole Mod Manager contributors
// SPDX-License-Identifier: GPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
// version 3 as published by the Free Software Foundation. This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License (LICENSE) for more details.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher : Form
    {
        static readonly string OptionsPath = Path.Combine(DataDir, "launcher_options.json");   // (public: in app data - Launcher.cs DataDir)
        bool optionsOpen;

        // (2026-11-18) parsed again only when the file changed (its time stamp) - pages ask often while painting; callers get a copy
        static Dictionary<string, object> optionsCache;
        static DateTime optionsStamp;
        static Dictionary<string, object> Options()
        {
            DateTime st = DateTime.MinValue;
            try { st = File.Exists(OptionsPath) ? File.GetLastWriteTimeUtc(OptionsPath) : DateTime.MinValue; } catch { }
            if (optionsCache == null || st != optionsStamp) { optionsCache = ReadJson(OptionsPath); optionsStamp = st; }
            var o = new Dictionary<string, object>(optionsCache);
            if (!o.ContainsKey("camo_textures")) o["camo_textures"] = false;
            if (!o.ContainsKey("camo_nets")) o["camo_nets"] = false;
            return o;
        }
        static bool On(Dictionary<string, object> o, string k) { return Str(o, k).ToLowerInvariant() == "true"; }


        void ToggleOption(string key)
        {
            if (busy) return;
            if (key == "lowspec") { ToggleLowSpec(); return; }     // (LauncherMachine.cs: no files to download - the flag and the game's settings)
            var o = Options();
            o[key] = !On(o, key);
            File.WriteAllText(OptionsPath, new JavaScriptSerializer().Serialize(o));
            Say("option " + key + (On(o, key) ? " on" : " off"));
            Redraw();
        }

    }
}
