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
using System.Text.RegularExpressions;

namespace FoxholeModLauncher
{
    // one file of a mod on Nexus (modFiles: uri's name part, display name, version, category, date)
    class NexusFileEntry
    {
        public string FileName = "", Title = "", Version = "", Category = "";
        public long Date;
        public bool Vague;                                               // (PickUpdate: "newer files on Nexus" - which one is not known)
    }

    // what a Nexus download's file name says
    class NexusFileName
    {
        public string Name = "", Version = "";
        public int ModId;
        public long FileTime;                                            // (0 = the name has no upload time)
    }

    // an installed mod's Nexus file (for the Downloads offer - Decide)
    class InstalledNexus
    {
        public string Id = "", Name = "";
        public int ModId;
        public long FileTime;
    }

    // what to do with a Nexus-named file that landed in Downloads
    class OfferDecision
    {
        public string Kind = "ignore";                                   // "update" | "new" | "ignore"
        public string TargetId = "", Why = "";
        public bool Older;
    }

    static class ModUpdateRules
    {
        // (2026-11-18 review C1 / C2) ONLY the full form "<name>-<id>-<version>-<upload time>" is a Nexus name: an id-and-version-only
        // pattern matched ordinary files ("backup-2026-11-18" -> mod 2026). The name itself may hold "-<digits>-" ("Mk-3-Retexture-45-2-0-..."):
        // every "-<digits>-" is a candidate split; one whose (id, time) Nexus lists wins, else the leftmost one whose version looks numeric,
        // else the leftmost one.
        static readonly Regex Tail = new Regex(@"^(.+)-(\d{9,11})$");
        static readonly Regex NumericVersion = new Regex(@"^[vV]?\d+(-\d+)*([a-zA-Z]{1,2}\d*)?$");
        static readonly Regex Copy = new Regex(@"\s*\(\d{1,3}\)$");
        public const long FIRST_UPLOAD = 1388534400L;                    // (2014-01-01: before any Foxhole mod on Nexus)
        public const long FUTURE_SLACK = 86400L;                         // (an upload time over a day ahead of this PC's clock is not real)

        public static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
        public static bool PlausibleTime(long t, long now) { return t >= FIRST_UPLOAD && t <= now + FUTURE_SLACK; }

        // a file name WITHOUT its extension -> its parts, or null when it is not a Nexus name. listed(id, time): true when cached Nexus data
        // lists that upload time as a file of that mod (null: no data).
        public static NexusFileName Parse(string baseName) { return Parse(baseName, null, UnixNow()); }
        public static NexusFileName Parse(string baseName, Func<int, long, bool> listed, long now)
        {
            var all = ParseAll(baseName, now);
            if (all.Count == 0) return null;
            if (listed != null) foreach (var c in all) if (listed(c.ModId, c.FileTime)) return c;
            foreach (var c in all) if (NumericVersion.IsMatch(c.Version.Replace('.', '-'))) return c;
            return all[0];
        }
        // every reading of the name, leftmost id first
        public static List<NexusFileName> ParseAll(string baseName, long now)
        {
            var r = new List<NexusFileName>();
            string b = Copy.Replace((baseName ?? "").Trim(), "");
            var m = Tail.Match(b);
            if (!m.Success) return r;
            long t;
            if (!long.TryParse(m.Groups[2].Value, out t) || !PlausibleTime(t, now)) return r;
            string head = m.Groups[1].Value;                              // "<name>-<id>-<version>"
            // (every dash: regex matches would not overlap - "-1-0-" holds two candidates)
            for (int i = 1; i < head.Length; i++)
            {
                if (head[i] != '-') continue;
                int j = i + 1;
                while (j < head.Length && char.IsDigit(head[j])) j++;
                if (j == i + 1 || j - i - 1 > 9 || j >= head.Length - 1 || head[j] != '-') continue;
                string name = head.Substring(0, i).Trim(), ver = head.Substring(j + 1).Trim('-');
                int id;
                if (name == "" || ver == "" || !int.TryParse(head.Substring(i + 1, j - i - 1), out id) || id <= 0) continue;
                if (!Regex.IsMatch(ver, @"^[0-9A-Za-z][0-9A-Za-z.-]*$")) continue;
                r.Add(new NexusFileName { Name = name, ModId = id, Version = ver.Replace('-', '.'), FileTime = t });
            }
            return r;
        }

        // version order: "1.10" > "1.9", "U48" > "U47", "v2" == "2", "1.0" == "1", "1.0" > "1.0-beta", "1.0a" > "1.0"
        // -> < 0, 0, > 0 (two empty / unknown versions are equal)
        public static int CompareVersions(string a, string b)
        {
            var x = Tokens(a); var y = Tokens(b);
            int n = Math.Max(x.Count, y.Count);
            for (int i = 0; i < n; i++)
            {
                if (i >= x.Count) return -Rest(y, i);                          // (b is longer: its rest decides)
                if (i >= y.Count) return Rest(x, i);
                bool nx = char.IsDigit(x[i][0]), ny = char.IsDigit(y[i][0]);
                int c;
                if (nx && ny) c = CompareNumbers(x[i], y[i]);
                else if (nx != ny) c = nx ? 1 : -1;                           // (1.0.1 > 1.0.beta)
                else c = string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c < 0 ? -1 : 1;
            }
            return 0;
        }
        public static bool IsNewer(string remote, string installed)
        {
            if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(installed)) return false;
            return CompareVersions(remote, installed) > 0;
        }
        static List<string> Tokens(string v)
        {
            v = (v ?? "").Trim();
            if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1])) v = v.Substring(1);
            var r = new List<string>();
            foreach (Match m in Regex.Matches(v, @"\d+|[A-Za-z]+")) r.Add(m.Value);
            return r;
        }
        static int CompareNumbers(string a, string b)
        {
            a = a.TrimStart('0'); b = b.TrimStart('0');
            if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
            return string.CompareOrdinal(a, b);
        }
        // the longer version's rest from token 'from': 0 = only zeros ("1.0" == "1"), -1 = a pre-release mark after any zeros ("1.0-beta" <
        // "1.0", and "1.0.0-beta" < "1.0" - review C5), 1 = more
        static int Rest(List<string> t, int from)
        {
            int i = from;
            while (i < t.Count && char.IsDigit(t[i][0]) && t[i].TrimStart('0') == "") i++;
            if (i >= t.Count) return 0;
            string s = t[i].ToLowerInvariant();
            if (s == "alpha" || s == "beta" || s == "rc" || s == "pre" || s == "preview" || s == "dev" || s == "test") return -1;
            return 1;
        }

        static bool Live(string category)
        {
            return category == "MAIN" || category == "UPDATE" || category == "OPTIONAL" || category == "MISCELLANEOUS";
        }
        static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", ""); }
        static bool Similar(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            if (x.Length < 3 || y.Length < 3) return false;
            return x.Contains(y) || y.Contains(x);
        }
        static NexusFileEntry Newest(IEnumerable<NexusFileEntry> l)
        {
            NexusFileEntry best = null;
            foreach (var f in l) if (best == null || f.Date > best.Date) best = f;
            return best;
        }

        static bool MainOrUpdate(string category) { return category == "MAIN" || category == "UPDATE"; }

        // the Nexus file that updates the installed one, or null.
        //   fileTime / fileName / version: the installed download's (from its Nexus file name); files: the mod's files on Nexus (modFiles);
        //   modVersion: the mod page's version (not used any more - review C2: no badge without the installed file's upload time).
        // A NEWER file must be a live one (main / update / optional / miscellaneous - never archived / old / removed). Preferred: the same
        // file name as the installed one. When the installed file is still a live file and only OTHER files are newer, that is another
        // variant of the mod (e.g. Colonial / Warden versions), not an update. When the installed file was archived / removed: the newer
        // MAIN / UPDATE file with a similar name (review C3: never another variant or an optional add-on); else, when newer main / update
        // files exist, a VAGUE answer ("newer files on Nexus" - no version named, Update only opens the mod's files).
        public static NexusFileEntry PickUpdate(long fileTime, string fileName, string version, IList<NexusFileEntry> files, string modVersion)
        {
            if (fileTime <= 0 || files == null) return null;
            NexusFileEntry inst = null;
            var newer = new List<NexusFileEntry>();
            foreach (var f in files)
            {
                if (f.Date == fileTime && (inst == null || Norm(f.FileName) == Norm(fileName))) inst = f;
                if (f.Date > fileTime && Live(f.Category)) newer.Add(f);
            }
            if (newer.Count == 0) return null;
            string name = inst != null && inst.FileName != "" ? inst.FileName : fileName;
            var same = newer.FindAll(f => Norm(f.FileName) == Norm(name) && Norm(name) != "");
            if (same.Count > 0) return Newest(same);
            if (inst != null && Live(inst.Category)) return null;
            var similar = newer.FindAll(f => MainOrUpdate(f.Category) && (Similar(f.FileName, name) || Similar(f.Title, name)));
            if (similar.Count > 0) return Newest(similar);
            var main = Newest(newer.FindAll(f => MainOrUpdate(f.Category)));
            if (main == null) return null;
            return new NexusFileEntry { Category = main.Category, Date = main.Date, Vague = true };
        }

        // (review C4 / U1) a Nexus-named file in Downloads: the UPDATE of an installed mod only when its mod id matches AND its file name is
        // the same as / similar to that mod's installed file, AND - where Nexus' file list of that mod is cached - Nexus lists that upload
        // time for it; a file of a known mod that fails this is offered as a NEW mod (never as a replacement of another one). The installed
        // file itself is ignored; upload times in the future are refused.
        //   listed(modId, time): true / false from the cached file list, null = nothing cached for that mod
        public static OfferDecision Decide(NexusFileName dl, IList<InstalledNexus> installed, Func<int, long, bool?> listed, long now)
        {
            var d = new OfferDecision();
            if (dl == null || dl.ModId <= 0 || !PlausibleTime(dl.FileTime, now)) { d.Why = "not a Nexus download (or its upload time is not plausible)"; return d; }
            InstalledNexus same = null, similar = null;
            foreach (var i in installed)
            {
                if (i.ModId != dl.ModId) continue;
                if (i.FileTime == dl.FileTime && Norm(i.Name) == Norm(dl.Name)) { d.Why = "the installed file itself"; d.TargetId = i.Id; return d; }
                if (same == null && Norm(i.Name) != "" && Norm(i.Name) == Norm(dl.Name)) same = i;
                if (similar == null && Similar(i.Name, dl.Name)) similar = i;
            }
            var t = same ?? similar;
            d.Kind = "new";
            if (t == null) { d.Why = "no installed file of that mod has this file name"; return d; }
            bool? ok = listed != null ? listed(dl.ModId, dl.FileTime) : null;
            if (ok == false) { d.Why = "Nexus does not list this file for mod " + dl.ModId; return d; }
            if (t.FileTime == dl.FileTime) { d.Kind = "ignore"; d.TargetId = t.Id; d.Why = "the installed file itself"; return d; }
            d.Kind = "update"; d.TargetId = t.Id; d.Older = t.FileTime > 0 && dl.FileTime < t.FileTime;
            return d;
        }
    }
}
