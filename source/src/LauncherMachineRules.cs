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
using System.Globalization;

namespace FoxholeModLauncher
{
    class MachineFacts
    {
        public double RamGb;                 // installed memory (GB)
        public bool GpuKnown;                // a graphics adapter was read
        public string Gpu = "";              // the adapter the game will use (Unreal prefers the first dedicated one)
        public bool Integrated;              // ... and it is integrated graphics (shares the PC's memory)
        public double VramGb;                // its dedicated video memory (GB)
        public int Cores;                    // logical processors
        public double SystemFreeGb = -1;     // free space on Windows' drive (the page file grows there); -1 = unknown
        public List<string> Adapters = new List<string>();
    }

    class MachineRules
    {
        public double BelowRamGb = 8;                // less memory than this: below the minimum
        public double IntegratedBelowRamGb = 12;     // integrated graphics AND less memory than this: below the minimum
        public double BelowVramGb = 2;               // a dedicated card with less video memory: below the minimum
        public int BelowCores = 4;
        public double LowRamGb = 16;                 // less memory than this: low
        public double LowVramGb = 4;                 // less video memory on a dedicated card: low
        public int LowCores = 6;
        public double LowSystemFreeGb = 10;          // less free space on Windows' drive: low (no room for the page file)
        public bool IntegratedIsLow = true;          // integrated graphics: low

        static double Num(IDictionary<string, object> d, string k, double def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            double x;
            if (v is string) return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }
        static bool Flag(IDictionary<string, object> d, string k, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes" ? true : s == "false" || s == "0" || s == "no" ? false : def;
        }
        // the manifest's "machine" object (names as below); missing or unreadable values keep the defaults
        public static MachineRules From(IDictionary<string, object> d)
        {
            var r = new MachineRules();
            r.BelowRamGb = Num(d, "below_ram_gb", r.BelowRamGb);
            r.IntegratedBelowRamGb = Num(d, "integrated_below_ram_gb", r.IntegratedBelowRamGb);
            r.BelowVramGb = Num(d, "below_vram_gb", r.BelowVramGb);
            r.BelowCores = (int)Num(d, "below_cores", r.BelowCores);
            r.LowRamGb = Num(d, "low_ram_gb", r.LowRamGb);
            r.LowVramGb = Num(d, "low_vram_gb", r.LowVramGb);
            r.LowCores = (int)Num(d, "low_cores", r.LowCores);
            r.LowSystemFreeGb = Num(d, "low_system_free_gb", r.LowSystemFreeGb);
            r.IntegratedIsLow = Flag(d, "integrated_is_low", r.IntegratedIsLow);
            return r;
        }
    }

    static class MachineClass
    {
        public const string OK = "OK", LOW = "LOW", BELOW = "BELOW MINIMUM";

        static string Gb(double v) { return v.ToString(v < 10 && Math.Abs(v - Math.Round(v)) > 0.05 ? "0.0" : "0", CultureInfo.InvariantCulture) + " GB"; }

        // the class, and the reasons in plain words (the worst first)
        public static string Classify(MachineFacts f, MachineRules r, List<string> reasons)
        {
            var below = new List<string>();
            var low = new List<string>();
            if (f.RamGb > 0 && f.RamGb < r.BelowRamGb) below.Add("only " + Gb(f.RamGb) + " of memory (RAM)");
            if (f.GpuKnown && f.Integrated && f.RamGb > 0 && f.RamGb < r.IntegratedBelowRamGb)
                below.Add("integrated graphics (" + f.Gpu + ") sharing " + Gb(f.RamGb) + " of memory");
            if (f.GpuKnown && !f.Integrated && f.VramGb < r.BelowVramGb) below.Add("a graphics card with only " + Gb(f.VramGb) + " of video memory");
            if (f.Cores > 0 && f.Cores < r.BelowCores) below.Add("only " + f.Cores + " processor threads");
            if (!f.GpuKnown) low.Add("the graphics card could not be read");
            if (f.RamGb > 0 && f.RamGb < r.LowRamGb && f.RamGb >= r.BelowRamGb) low.Add(Gb(f.RamGb) + " of memory (RAM)");
            if (f.GpuKnown && f.Integrated && r.IntegratedIsLow && !(f.RamGb > 0 && f.RamGb < r.IntegratedBelowRamGb))
                low.Add("integrated graphics (" + f.Gpu + ")");
            if (f.GpuKnown && !f.Integrated && f.VramGb >= r.BelowVramGb && f.VramGb < r.LowVramGb) low.Add(Gb(f.VramGb) + " of video memory");
            if (f.Cores >= r.BelowCores && f.Cores < r.LowCores) low.Add(f.Cores + " processor threads");
            if (f.SystemFreeGb >= 0 && f.SystemFreeGb < r.LowSystemFreeGb) low.Add("only " + Gb(f.SystemFreeGb) + " free on the Windows drive");
            if (reasons != null) { reasons.AddRange(below); reasons.AddRange(low); }
            return below.Count > 0 ? BELOW : low.Count > 0 ? LOW : OK;
        }

        // integrated graphics? (the PCI vendor id, the adapter's name, its dedicated memory). Intel's are integrated except Arc cards; AMD's
        // APUs by name (Radeon Graphics / Vega n Graphics / 680M-890M); anything else with under 1 GB of its own memory.
        public static bool IsIntegrated(string name, uint vendor, double dedicatedGb)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (vendor == 0x8086) return !(n.Contains(" arc") || n.StartsWith("arc") || dedicatedGb >= 2);
            if (vendor == 0x1002)
            {
                if (n.Contains("radeon(tm) graphics") || n.Contains("radeon graphics") || System.Text.RegularExpressions.Regex.IsMatch(n, @"vega \d+ graphics") ||
                    System.Text.RegularExpressions.Regex.IsMatch(n, @"radeon(\(tm\))? (6|7|8)\d0m\b")) return true;
                return dedicatedGb < 1;
            }
            if (vendor == 0x10de) return false;                          // NVIDIA
            if (vendor == 0x5143 || vendor == 0x13b5) return true;       // Qualcomm / ARM
            return dedicatedGb < 1;
        }

        // the one line shown in OPTIONS and written to problem reports
        public static string Line(string cls, List<string> reasons)
        {
            if (cls == OK || reasons == null || reasons.Count == 0) return cls;
            return cls + " - " + string.Join(", ", reasons.ToArray());
        }

        // the lowest preset into the game's settings text: every scalability group 0, 3D resolution at most 75 %, a 60 fps limit when there
        // was none (an unlimited frame rate keeps integrated graphics at full heat); the window, keys and everything else stay as they are
        public static string LowSpecSettings(string ini)
        {
            var groups = new[] { "ViewDistance", "AntiAliasing", "Shadow", "PostProcess", "Texture", "Effects", "Foliage", "Shading" };
            var want = new Dictionary<string, string>();
            foreach (var g in groups) want["sg." + g + "Quality"] = "0";
            var lines = new List<string>(ini.Replace("\r\n", "\n").Split('\n'));
            if (lines.Count > 0 && lines[lines.Count - 1] == "") lines.RemoveAt(lines.Count - 1);
            int sgAt = -1; string section = "";
            double res = 100;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[")) { section = t; if (t == "[ScalabilityGroups]") sgAt = i; continue; }
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string k = t.Substring(0, eq), v = t.Substring(eq + 1);
                if (section == "[ScalabilityGroups]" && want.ContainsKey(k)) { lines[i] = k + "=" + want[k]; want.Remove(k); }
                else if (section == "[ScalabilityGroups]" && k == "sg.ResolutionQuality")
                {
                    double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out res);
                    if (res <= 0 || res > 75) lines[i] = "sg.ResolutionQuality=75.000000";
                    want["#res"] = "done";
                }
                else if (section == "[/Script/War.WarGameUserSettings]" && k == "FrameRateLimit")
                {
                    double fps; double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fps);
                    if (fps <= 0 || fps > 60) lines[i] = "FrameRateLimit=60.000000";
                }
            }
            var add = new List<string>();
            foreach (var kv in want) if (kv.Key != "#res") add.Add(kv.Key + "=" + kv.Value);
            if (!want.ContainsKey("#res")) add.Add("sg.ResolutionQuality=75.000000");
            if (sgAt < 0) { if (lines.Count > 0) lines.Add(""); lines.Add("[ScalabilityGroups]"); lines.AddRange(add); }
            else lines.InsertRange(sgAt + 1, add);
            return string.Join("\r\n", lines.ToArray()) + "\r\n";
        }
    }
}
