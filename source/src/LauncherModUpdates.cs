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
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    // an update for one installed mod (what the Installed row shows)
    class ModUpdate
    {
        public int ModId;
        public string Version = "", Title = "", Source = "nexus", Path = "";   // (Source "download": Path = the file in Downloads)
        public long FileTime;
        public bool Vague;                                               // (newer files on Nexus, which one is not known - no version named)
    }

    // one mod as Nexus answered (the cache keeps these)
    class NexusModFiles
    {
        public int ModId;
        public string Name = "", Version = "", Status = "", Thumb = "";
        public List<NexusFileEntry> Files = new List<NexusFileEntry>();
    }

    partial class Launcher
    {
        // (a property, not a static field: its initializer read ModsRoot of another file - only right in one compile order; review)
        static string UpdatesCache { get { return Path.Combine(ModsRoot, "updates.json"); } }
        static readonly TimeSpan UPDATES_TTL = TimeSpan.FromHours(1), UPDATES_FAILED_WAIT = TimeSpan.FromMinutes(15);
        const int UPDATES_BATCH = 40;                                    // (mods per request; more installed mods = one more request each 40)
        static DateTime lastUpdatesFetch = DateTime.MinValue, updatesFailedAt = DateTime.MinValue;
        static readonly object updatesCacheLock = new object();          // (the worker and the Downloads offer both save the cache)
        // the Nexus data the file-name parser cross-checks with (review C1): swapped whole, read from any thread
        static volatile Dictionary<int, NexusModFiles> knownNexus = new Dictionary<int, NexusModFiles>();

        // UI-thread state (read while painting; filled from the cache / the worker, never by painting)
        readonly Dictionary<string, ModUpdate> modUpdates = new Dictionary<string, ModUpdate>();   // (cosmetic mod id -> update)
        Dictionary<int, NexusModFiles> updatesData = new Dictionary<int, NexusModFiles>();
        // newer downloads seen in Downloads, by the installed (cosmetic) mod they update - locked: the offer thread saves a copy
        readonly Dictionary<string, ModUpdate> updatesSeen = new Dictionary<string, ModUpdate>();
        DateTime updatesChecked = DateTime.MinValue;
        bool updatesChecking, updatesLoaded, updatesAuto;
        int updatesCount;
        // (filled by InstalledRefresh on a worker - no file system calls on the UI thread for paths read from the cache)
        HashSet<string> seenPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, string> installedThumbUrl = new Dictionary<int, string>();
        bool installedRefreshing;
        DateTime installedRefreshedAt = DateTime.MinValue;
        string installedRefreshKey = "";

        static bool UpdatesAutoAllowed() { return BrowseAllowed(); }

        // ---------------------------------------------------------------------------------------------------- a mod's Nexus file
        // is (mod id, upload time) a file Nexus lists for that mod (from the last answer; false when that mod is not cached)
        static bool Listed(int id, long t)
        {
            NexusModFiles nf;
            if (!knownNexus.TryGetValue(id, out nf)) return false;
            foreach (var f in nf.Files) if (f.Date == t) return true;
            return false;
        }
        static bool? ListedOrUnknown(int id, long t)
        {
            NexusModFiles nf;
            if (!knownNexus.TryGetValue(id, out nf) || nf.Files.Count == 0) return null;
            return Listed(id, t);
        }
        static NexusFileName ParseNexus(string baseName) { return ModUpdateRules.Parse(baseName, Listed, ModUpdateRules.UnixNow()); }

        static NexusFileName NexusOf(Dictionary<string, object> m)
        {
            var p = ParseNexus(Path.GetFileNameWithoutExtension(Str(m, "source")));
            if (p != null) return p;
            object o;
            var d = m.TryGetValue("nexus_file", out o) ? o as Dictionary<string, object> : null;
            if (d != null)
            {
                int id; long t;
                if (int.TryParse(Str(d, "mod_id"), out id) && id > 0 && long.TryParse(Str(d, "file_time"), out t) && ModUpdateRules.PlausibleTime(t, ModUpdateRules.UnixNow()))
                    return new NexusFileName { ModId = id, Version = Str(d, "version"), Name = Str(d, "file_name"), FileTime = t };
            }
            return null;
        }
        static Dictionary<string, object> NexusFileRecord(NexusFileName p)
        {
            return new Dictionary<string, object> { { "mod_id", p.ModId }, { "version", p.Version }, { "file_time", p.FileTime }, { "file_name", p.Name } };
        }
        static string ModVersionText(Dictionary<string, object> m)
        {
            var p = NexusOf(m);
            return p != null ? p.Version : "";
        }

        // ---------------------------------------------------------------------------------------------------- asking Nexus
        // the mods' files from Nexus - one request per 40 mods (today: one); throws on any failure
        static Dictionary<int, NexusModFiles> NexusUpdatesFetch(ICollection<int> ids)
        {
            var r = new Dictionary<int, NexusModFiles>();
            var ser = new JavaScriptSerializer { MaxJsonLength = (int)NexusHttp.JSON_MAX * 4 };
            var all = new List<int>(ids);
            for (int at = 0; at < all.Count; at += UPDATES_BATCH)
            {
                var part = all.GetRange(at, Math.Min(UPDATES_BATCH, all.Count - at));
                var q = new StringBuilder("query($ids:[CompositeDomainWithIdInput!]!){ legacyModsByDomain(ids:$ids, count:" + UPDATES_BATCH + "){ nodes{ modId name version status thumbnailUrl } }");
                var idList = new List<object>();
                foreach (int id in part)
                {
                    if (id <= 0) continue;                                // (ids are positive integers - never text from elsewhere)
                    q.Append(" m" + id + ": modFiles(modId:" + id + ", gameId:" + NEXUS_GAME_ID + "){ name version category date uri }");
                    idList.Add(new Dictionary<string, object> { { "gameDomain", NEXUS_GAME }, { "modId", id } });
                }
                q.Append(" }");
                string body = ser.Serialize(new Dictionary<string, object> { { "query", q.ToString() }, { "variables", new Dictionary<string, object> { { "ids", idList } } } });
                var resp = ser.DeserializeObject(Encoding.UTF8.GetString(NexusHttp.Send(NEXUS_API, NexusHttp.JSON_MAX, body))) as Dictionary<string, object>;
                var data = resp != null && resp.ContainsKey("data") ? resp["data"] as Dictionary<string, object> : null;
                if (data == null) throw new Exception("unexpected answer from Nexus Mods");
                var mods = Dig(data, "legacyModsByDomain");
                var nodes = mods != null && mods.ContainsKey("nodes") ? mods["nodes"] as object[] : null;
                foreach (int id in part) r[id] = new NexusModFiles { ModId = id };
                if (nodes != null)
                    foreach (var o in nodes)
                    {
                        var d = o as Dictionary<string, object>;
                        int id;
                        if (d == null || !int.TryParse(Str(d, "modId"), out id) || !r.ContainsKey(id)) continue;
                        r[id].Name = Clean(Str(d, "name")); r[id].Version = Clean(Str(d, "version")); r[id].Status = Clean(Str(d, "status"));
                        string th = Clean(Str(d, "thumbnailUrl"));
                        r[id].Thumb = ThumbUrlOk(th) ? th : "";                // (only an address on Nexus' image hosts is kept)
                    }
                foreach (int id in part)
                {
                    object fl;
                    if (!data.TryGetValue("m" + id, out fl) || !(fl is object[])) continue;
                    foreach (var o in (object[])fl)
                    {
                        var d = o as Dictionary<string, object>;
                        long date;
                        if (d == null || !long.TryParse(Str(d, "date"), out date)) continue;
                        int fid = id; long fdate = date;                   // (the uri read for THIS mod and date - review C1)
                        var u = ModUpdateRules.Parse(Path.GetFileNameWithoutExtension(Clean(Str(d, "uri"))), (x, t) => x == fid && t == fdate, ModUpdateRules.UnixNow());
                        r[id].Files.Add(new NexusFileEntry { FileName = u != null ? u.Name : Clean(Str(d, "name")), Title = Clean(Str(d, "name")),
                            Version = Clean(Str(d, "version")), Category = Clean(Str(d, "category")), Date = date });
                    }
                }
            }
            return r;
        }
        const int NEXUS_GAME_ID = 4287;                                  // (Foxhole on Nexus - game(domainName:"foxhole"){id}, 2026-11-18)

        // ---------------------------------------------------------------------------------------------------- the cache
        static void SaveUpdatesCache(DateTime checkedUtc, Dictionary<int, NexusModFiles> data, Dictionary<string, ModUpdate> seen)
        {
            var mods = new Dictionary<string, object>();
            foreach (var kv in data)
            {
                var files = new List<object>();
                foreach (var f in kv.Value.Files)
                    files.Add(new Dictionary<string, object> { { "file", f.FileName }, { "title", f.Title }, { "version", f.Version }, { "category", f.Category }, { "date", f.Date } });
                mods[kv.Key.ToString()] = new Dictionary<string, object> { { "name", kv.Value.Name }, { "version", kv.Value.Version }, { "status", kv.Value.Status },
                    { "thumb", kv.Value.Thumb }, { "files", files } };
            }
            var sn = new Dictionary<string, object>();
            foreach (var kv in seen)
                sn[kv.Key] = new Dictionary<string, object> { { "mod_id", kv.Value.ModId }, { "version", kv.Value.Version }, { "file_time", kv.Value.FileTime },
                    { "path", kv.Value.Path }, { "title", kv.Value.Title } };
            lock (updatesCacheLock)
                WriteJson(UpdatesCache, new Dictionary<string, object> { { "checked", checkedUtc == DateTime.MinValue ? "" : checkedUtc.ToString("o") }, { "mods", mods }, { "seen_for", sn } });
        }
        static void ReadUpdatesCache(out DateTime checkedUtc, out Dictionary<int, NexusModFiles> data, out Dictionary<string, ModUpdate> seen)
        {
            checkedUtc = DateTime.MinValue; data = new Dictionary<int, NexusModFiles>(); seen = new Dictionary<string, ModUpdate>();
            Dictionary<string, object> d;
            lock (updatesCacheLock)
            {
                if (!File.Exists(UpdatesCache) || new FileInfo(UpdatesCache).Length > NexusHttp.JSON_MAX * 2) return;
                d = ReadJson(UpdatesCache);
            }
            if (d == null) return;
            DateTime t;
            if (DateTime.TryParse(Str(d, "checked"), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) checkedUtc = t.ToUniversalTime();
            if (checkedUtc > DateTime.UtcNow.AddMinutes(5)) checkedUtc = DateTime.MinValue;   // (a clock that went back - asks again)
            object o;
            var mods = d.TryGetValue("mods", out o) ? o as Dictionary<string, object> : null;
            if (mods != null)
                foreach (var kv in mods)
                {
                    int id; var m = kv.Value as Dictionary<string, object>;
                    if (!int.TryParse(kv.Key, out id) || id <= 0 || m == null) continue;
                    var e = new NexusModFiles { ModId = id, Name = Str(m, "name"), Version = Str(m, "version"), Status = Str(m, "status"),
                        Thumb = ThumbUrlOk(Str(m, "thumb")) ? Str(m, "thumb") : "" };
                    object fl;
                    if (m.TryGetValue("files", out fl) && fl is object[])
                        foreach (var f in (object[])fl)
                        {
                            var fd = f as Dictionary<string, object>; long date;
                            if (fd != null && long.TryParse(Str(fd, "date"), out date))
                                e.Files.Add(new NexusFileEntry { FileName = Str(fd, "file"), Title = Str(fd, "title"), Version = Str(fd, "version"), Category = Str(fd, "category"), Date = date });
                        }
                    data[id] = e;
                }
            // (the older "seen" list keyed by Nexus id is ignored: it marked every variant of a mod - review C4)
            var sn = d.TryGetValue("seen_for", out o) ? o as Dictionary<string, object> : null;
            if (sn != null)
                foreach (var kv in sn)
                {
                    int id; long ft; var s = kv.Value as Dictionary<string, object>;
                    if (s == null || !System.Text.RegularExpressions.Regex.IsMatch(kv.Key, "^[a-z0-9][a-z0-9-]{0,79}$") || !int.TryParse(Str(s, "mod_id"), out id) || id <= 0) continue;
                    string path = Str(s, "path");
                    if (!InDownloads(path)) continue;                           // (only a file in the Downloads folder is ever taken from here)
                    seen[kv.Key] = new ModUpdate { ModId = id, Version = Str(s, "version"), FileTime = long.TryParse(Str(s, "file_time"), out ft) ? ft : 0,
                        Path = path, Title = Str(s, "title"), Source = "download" };
                }
        }
        static string DownloadsDir() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
        // a path straight inside the Downloads folder (by its text only - no file system call)
        static bool InDownloads(string p)
        {
            try
            {
                if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p) || p.StartsWith(@"\\")) return false;
                return string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)).TrimEnd('\\'), DownloadsDir().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        void LoadUpdates()
        {
            DateTime c; Dictionary<int, NexusModFiles> d; Dictionary<string, ModUpdate> s;
            try { ReadUpdatesCache(out c, out d, out s); } catch { c = DateTime.MinValue; d = new Dictionary<int, NexusModFiles>(); s = new Dictionary<string, ModUpdate>(); }
            updatesChecked = c; updatesData = d; knownNexus = d;
            lock (updatesSeen) { updatesSeen.Clear(); foreach (var kv in s) updatesSeen[kv.Key] = kv.Value; }
            updatesLoaded = true;
        }
        Dictionary<string, ModUpdate> SeenCopy() { lock (updatesSeen) return new Dictionary<string, ModUpdate>(updatesSeen); }

        // ---------------------------------------------------------------------------------------------------- the update state per mod
        // (UI thread; from CountInstalled - the store and the cache are read there, never while painting; paths from the cache are checked
        //  on a worker - InstalledRefresh)
        void ComputeUpdates()
        {
            if (!updatesLoaded) LoadUpdates();
            updatesAuto = UpdatesAutoAllowed();
            modUpdates.Clear();
            var seen = SeenCopy(); var present = seenPresent;
            foreach (var m in cosCache)
            {
                var u = UpdateFor(m, updatesData, seen, p => present.Contains(p));
                if (u != null) modUpdates[Str(m, "id")] = u;
            }
            updatesCount = modUpdates.Count;
        }
        static ModUpdate UpdateFor(Dictionary<string, object> m, Dictionary<int, NexusModFiles> data, Dictionary<string, ModUpdate> seen, Func<string, bool> present)
        {
            var p = NexusOf(m);
            if (p == null || p.FileTime <= 0) return null;               // (no upload time: no badge - review C2)
            ModUpdate best = null;
            NexusModFiles nf;
            if (data.TryGetValue(p.ModId, out nf))
            {
                var f = ModUpdateRules.PickUpdate(p.FileTime, p.Name, p.Version, nf.Files, nf.Version);
                if (f != null) best = new ModUpdate { ModId = p.ModId, Version = f.Version, Title = f.Title, FileTime = f.Date, Vague = f.Vague };
            }
            ModUpdate s;
            if (seen.TryGetValue(Str(m, "id"), out s) && s.ModId == p.ModId && s.FileTime > p.FileTime && present(s.Path) &&
                (best == null || best.Vague || s.FileTime >= best.FileTime))
                best = s;                                                // (the newest file is already in Downloads: Update takes it from there)
            return best;
        }

        // the Installed page's facts read from the disk on a worker: which seen downloads still exist, each Nexus mod's picture address (the
        // update data's, else the Browse list's on this PC). Runs after CountInstalled (at most every 5 s unless the mods changed).
        void InstalledRefresh()
        {
            if (shotState != "" || Headless) return;
            var ids = new List<int>();
            foreach (var m in cosCache) { var p = NexusOf(m); if (p != null && !ids.Contains(p.ModId)) ids.Add(p.ModId); }
            var seen = SeenCopy();
            var data = updatesData;
            string key = string.Join(",", ids.ConvertAll(x => x.ToString()).ToArray()) + "|" + seen.Count + "|" + updatesChecked.Ticks;
            if (installedRefreshing || (key == installedRefreshKey && DateTime.UtcNow - installedRefreshedAt < TimeSpan.FromSeconds(5))) return;
            installedRefreshing = true; installedRefreshKey = key; installedRefreshedAt = DateTime.UtcNow;
            new Thread(() =>
            {
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var urls = new Dictionary<int, string>();
                try
                {
                    foreach (var s in seen.Values) if (InDownloads(s.Path) && File.Exists(s.Path)) present.Add(s.Path);
                    foreach (var id in ids) { NexusModFiles nf; if (data.TryGetValue(id, out nf) && nf.Thumb != "") urls[id] = nf.Thumb; }
                    if (urls.Count < ids.Count)
                        foreach (var kv in BrowseListThumbs())
                            if (ids.Contains(kv.Key) && !urls.ContainsKey(kv.Key)) urls[kv.Key] = kv.Value;
                }
                catch (Exception ex) { Say("installed mods: " + ex.Message); }
                Ui(() =>
                {
                    installedRefreshing = false;
                    bool changed = !present.SetEquals(seenPresent) || urls.Count != installedThumbUrl.Count;
                    foreach (var kv in urls) { string u; if (!installedThumbUrl.TryGetValue(kv.Key, out u) || u != kv.Value) changed = true; }
                    seenPresent = present; installedThumbUrl = urls;
                    if (changed) { ComputeUpdates(); Redraw(); }
                });
            }) { IsBackground = true }.Start();
        }
        // the Browse list(s) saved on this PC -> mod id -> picture address (read again only when a list file changed)
        static Dictionary<int, string> browseThumbsCache = new Dictionary<int, string>();
        static string browseThumbsStamp = "";
        static Dictionary<int, string> BrowseListThumbs()
        {
            var stamp = new StringBuilder();
            var files = new List<string>();
            foreach (var n in new[] { "list-0.json", "list-1.json" })
            {
                string f = Path.Combine(BrowseDir, n);
                if (File.Exists(f) && new FileInfo(f).Length < NexusHttp.JSON_MAX * 8) { files.Add(f); stamp.Append(f + File.GetLastWriteTimeUtc(f).Ticks + ";"); }
            }
            if (stamp.ToString() == browseThumbsStamp) return browseThumbsCache;
            var r = new Dictionary<int, string>();
            var ser = new JavaScriptSerializer { MaxJsonLength = (int)NexusHttp.JSON_MAX * 8 };
            foreach (var f in files)
                try { foreach (var m in ReadList(ser, f)) if (m.Thumb != "" && ThumbUrlOk(m.Thumb)) r[m.Id] = m.Thumb; } catch { }
            browseThumbsCache = r; browseThumbsStamp = stamp.ToString();
            return r;
        }
        // the row's picture (UI thread, while painting: the memory cache only; a miss is queued for the picture worker). Network: the public
        // edition only while BROWSE is on - else only a picture already on this PC
        const int INSTALLED_THUMB_W = 64, INSTALLED_THUMB_H = 36;
        Bitmap InstalledThumb(int modId, out bool waiting)
        {
            waiting = false;
            string url;
            if (modId <= 0 || !installedThumbUrl.TryGetValue(modId, out url)) return null;
            ThumbWire();
            var b = ThumbFor(modId, url, INSTALLED_THUMB_W, INSTALLED_THUMB_H, BrowseAllowed());
            if (b == null) waiting = ThumbWaiting(modId, url, INSTALLED_THUMB_W, INSTALLED_THUMB_H);
            return b;
        }
        // a picture that arrived redraws the page that shows it (Browse or Installed)
        void ThumbWire() { if (ThumbArrived == null) ThumbArrived = () => Ui(() => { if (browseOpen || page == "installed") Redraw(); }); }

        // ---------------------------------------------------------------------------------------------------- checking (UI thread)
        // force = the "Check for mod updates" button; otherwise only when the last answer is over an hour old and no check failed in the last
        // 15 minutes (review N2)
        void ModUpdatesCheck(bool force)
        {
            if (!updatesLoaded) LoadUpdates();
            if (!UpdatesAutoAllowed())
            {
                if (force)
                    MessageBox.Show(this, "Update checks against Nexus Mods come once Nexus Mods approves this app (the same step that turns on the " +
                        "in-app mod browser).\n\nUntil then: a newer download of an installed mod is recognised when it lands in your Downloads " +
                        "folder, and Update on the mod opens its Files tab on Nexus.", "Mod updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (updatesChecking) return;
            var ids = new List<int>();
            foreach (var m in cosCache) { var p = NexusOf(m); if (p != null && !ids.Contains(p.ModId)) ids.Add(p.ModId); }
            if (ids.Count == 0) { if (force) { modsLine = "None of your mods came from Nexus Mods - nothing to check."; Redraw(); } return; }
            if (!force && DateTime.UtcNow - updatesChecked < UPDATES_TTL) return;
            if (!force && DateTime.UtcNow - updatesFailedAt < UPDATES_FAILED_WAIT) return;
            if (DateTime.UtcNow - lastUpdatesFetch < TimeSpan.FromSeconds(60))
            {
                if (force) { modsLine = "Checked under a minute ago."; Redraw(); }
                return;
            }
            lastUpdatesFetch = DateTime.UtcNow;
            updatesChecking = true;
            if (force) modsLine = "Asking Nexus Mods for your mods' newest versions...";
            Redraw();
            new Thread(() =>
            {
                string error = null; Dictionary<int, NexusModFiles> got = null;
                try
                {
                    got = NexusUpdatesFetch(ids);
                    SaveUpdatesCache(DateTime.UtcNow, got, SeenCopy());          // (the seen downloads as they are NOW - one may have come in meanwhile)
                }
                catch (Exception ex) { error = ex.Message; Say("mod updates: " + ex.Message); }
                Ui(() =>
                {
                    updatesChecking = false;
                    if (got != null) { updatesData = got; knownNexus = got; updatesChecked = DateTime.UtcNow; updatesFailedAt = DateTime.MinValue; }
                    else updatesFailedAt = DateTime.UtcNow;
                    CountInstalled();
                    if (force || error == null)
                        modsLine = error != null ? "Update check failed: " + error :
                                   updatesCount == 0 ? (force ? "All your Nexus mods are up to date." : modsLine) :
                                   updatesCount + " mod update(s) available - Update opens the mod's files on Nexus Mods.";
                    Redraw();
                });
            }) { IsBackground = true }.Start();
        }

        // (UI thread) a newer download of an installed mod (the Downloads offer): its badge shows even if the player says "not now"; the
        // cache is saved by the offer's worker (SaveSeen)
        void NoteSeenDownload(string cosId, NexusFileName p, string path)
        {
            lock (updatesSeen) updatesSeen[cosId] = new ModUpdate { ModId = p.ModId, Version = p.Version, FileTime = p.FileTime, Path = path, Title = p.Name, Source = "download" };
            seenPresent = new HashSet<string>(seenPresent, StringComparer.OrdinalIgnoreCase) { path };   // (it was just seen)
            CountInstalled();
            Redraw();
        }
        void SaveSeen() { try { SaveUpdatesCache(updatesChecked, updatesData, SeenCopy()); } catch (Exception ex) { Say("mod updates: " + ex.Message); } }

        // every installed Nexus file (for LauncherModUpdateRules.cs Decide)
        static List<InstalledNexus> InstalledNexusFiles()
        {
            var r = new List<InstalledNexus>();
            foreach (var m in CosMods())
            {
                var q = NexusOf(m);
                if (q != null) r.Add(new InstalledNexus { Id = Str(m, "id"), ModId = q.ModId, Name = q.Name, FileTime = q.FileTime });
            }
            return r;
        }

        // ---------------------------------------------------------------------------------------------------- update / roll back
        // the new file replaces the mod's files under the same id: checked as any new mod first (CosAdd: safe unpack, compatibility); a
        // version that would not load is refused and nothing changes; a worse verdict is asked about (review S7); ON / OFF and the player's
        // settings backups are kept; the replaced files are kept for Roll back. Refused while Foxhole runs (its paks are locked - review S2).
        // Every step after the temporary mod exists is undone on a failure (review S2 / S4 / S5): the temporary mod is removed, the files move
        // back, the record is restored, the mod is switched on again if it was - the message says the state it ends in.
        void CosUpdate(string id, string file, bool yes)
        {
            lock (cosLock)
            {
                var m = CosMod(id);
                if (ModsGameRunning()) throw new Exception("Foxhole is running - close it first, then update " + Str(m, "name"));
                string newId = CosAdd(file, yes);                        // (throws before anything changed: unreadable / unsafe / the same file)
                string root = Path.Combine(CosDir, id), newRoot = Path.Combine(CosDir, newId), files = Path.Combine(root, "files");
                string prev = Path.Combine(root, "previous"), prevOld = Path.Combine(root, "previous.old"), stage = Path.Combine(root, "previous.new");
                bool wasOn = Yes(m, "enabled");
                int step = 0;
                var mOff = m;
                try
                {
                    if (newId == id) throw new Exception("the new file was added under the mod's own id - refused");   // (never: ids hold the file's hash)
                    var nm = CosMod(newId);
                    string verdict = Str(nm, "verdict"), notes = string.Join("; ", StrList(nm, "notes").ToArray());
                    if (verdict == "WILL NOT LOAD") throw new Exception("the new version would not load (" + notes + ")");
                    if (VerdictRank(verdict) > VerdictRank(Str(m, "verdict")) && !AskWorseVerdict(m, verdict, notes, yes))
                        throw new Exception("the new version is checked as " + verdict + " (yours: " + (Str(m, "verdict") != "" ? Str(m, "verdict") : "OK") + ") - not updated" +
                            (Headless ? " (--yes accepts it)" : ""));
                    RecoverUpdateLeftovers(root);
                    if (wasOn) { CosEnable(id, false); mOff = CosMod(id); }
                    step = 1;
                    Directory.CreateDirectory(stage);
                    Directory.Move(files, Path.Combine(stage, "files")); step = 2;
                    Directory.Move(Path.Combine(newRoot, "files"), files); step = 3;
                    if (Directory.Exists(prev)) { Directory.Move(prev, prevOld); step = 4; }
                    Directory.Move(stage, prev); step = 5;
                    var m2 = new Dictionary<string, object>(mOff);
                    var keep = new Dictionary<string, object>();
                    foreach (var k in UPDATED_FIELDS) { object v; if (mOff.TryGetValue(k, out v)) keep[k] = v; }
                    m2["previous"] = keep;
                    foreach (var k in UPDATED_FIELDS) { object v; if (nm.TryGetValue(k, out v)) m2[k] = v; else m2.Remove(k); }
                    m2["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveMod(m2); step = 6;                                // (the record before anything is deleted)
                    DeletePlain(Path.Combine(newRoot, "mod.json"));      // (the temporary mod's row is gone even if its folder stays)
                    try { DeleteTree(newRoot); } catch { }
                    step = 7;
                    CosCheckAll();
                    if (wasOn) CosEnable(id, true);
                    step = 8;
                    try { DeleteTree(prevOld); } catch (Exception ex) { Say("cosmetic mods: the older kept version stays in " + prevOld + " (" + ex.Message + ")"); }
                    string to = ModVersionText(m2);
                    Say("cosmetic mod updated: " + Str(m2, "name") + (to != "" ? " -> " + to : "") + (wasOn ? " (ON)" : ""));
                }
                catch (Exception ex)
                {
                    var problems = new List<string>();
                    Action<string, Action> undo = (what, a) => { try { a(); } catch (Exception e2) { problems.Add(what + ": " + e2.Message); } };
                    if (step >= 6) undo("record", () => SaveMod(mOff));
                    if (step >= 5) undo("previous", () => Directory.Move(prev, stage));
                    if (step >= 4 && Directory.Exists(prevOld)) undo("older previous", () => Directory.Move(prevOld, prev));
                    if (step >= 3) undo("new files", () => { Directory.CreateDirectory(newRoot); Directory.Move(files, Path.Combine(newRoot, "files")); });
                    if (step >= 2) undo("files", () => Directory.Move(Path.Combine(stage, "files"), files));
                    undo("staging", () => DeleteTree(stage));
                    undo("temporary mod", () => { DeletePlain(Path.Combine(newRoot, "mod.json")); DeleteTree(newRoot); });
                    if (step >= 1 && wasOn) undo("switching it on again", () => { CosCheckAll(); CosEnable(id, true); });
                    bool on = false;
                    try { CosCheckAll(); on = Yes(CosMod(id), "enabled"); } catch { }
                    string state = " - kept the version you had (" + (on ? "ON" : "OFF") + ")";
                    if (problems.Count > 0) { Say("cosmetic mods: update undo problems: " + string.Join("; ", problems.ToArray())); state += "; could not undo everything: " + problems[0]; }
                    throw new Exception(ex.Message + state);
                }
            }
        }
        static readonly string[] UPDATED_FIELDS = { "name", "source", "nexus", "nexus_file", "sha256", "paks", "configs", "ignored" };
        static int VerdictRank(string v)
        {
            if (v == "OK" || v == "") return 0;
            if (v == "WARNING") return 1;
            if (v == "WILL NOT LOAD") return 3;
            return 2;                                                    // (SANDBOX ONLY / OUR SERVERS ONLY)
        }
        // (review S7) a worse verdict than the installed version's: asked (headless: --yes = yes, else no)
        bool AskWorseVerdict(Dictionary<string, object> m, string verdict, string notes, bool yes)
        {
            if (yes) return true;
            if (Headless) return false;
            bool go = false;
            Invoke((Action)(() => go = MessageBox.Show(this, "The new version of " + Str(m, "name") + " is checked as " + verdict + " (the one you have: " +
                (Str(m, "verdict") != "" ? Str(m, "verdict") : "OK") + ").\n\n" + notes + "\n\nUpdate anyway?", "Mod update",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes));
            return go;
        }
        // an update / roll back that stopped half-way (the PC went off): "previous.old" back as "previous" when that is missing, staging removed
        static void RecoverUpdateLeftovers(string root)
        {
            string prev = Path.Combine(root, "previous"), prevOld = Path.Combine(root, "previous.old");
            if (Directory.Exists(prevOld)) { if (Directory.Exists(prev)) DeleteTree(prevOld); else Directory.Move(prevOld, prev); }
            DeleteTree(Path.Combine(root, "previous.new"));
            DeleteTree(Path.Combine(root, "files.newer"));
        }

        // the previous version back; the newer files are dropped (the badge comes back from the next check). Undone on any failure.
        void CosRollback(string id)
        {
            lock (cosLock)
            {
                var m = CosMod(id);
                object o;
                var prevRec = m.TryGetValue("previous", out o) ? o as Dictionary<string, object> : null;
                string root = Path.Combine(CosDir, id), prev = Path.Combine(root, "previous"), files = Path.Combine(root, "files"), newer = Path.Combine(root, "files.newer");
                if (prevRec == null || !Directory.Exists(Path.Combine(prev, "files"))) throw new Exception("no previous version of " + Str(m, "name") + " is kept");
                if (ModsGameRunning()) throw new Exception("Foxhole is running - close it first, then roll back " + Str(m, "name"));
                bool wasOn = Yes(m, "enabled");
                var mOff = m;
                int step = 0;
                try
                {
                    DeleteTree(newer);
                    if (wasOn) { CosEnable(id, false); mOff = CosMod(id); }
                    step = 1;
                    Directory.Move(files, newer); step = 2;
                    Directory.Move(Path.Combine(prev, "files"), files); step = 3;
                    var m2 = new Dictionary<string, object>(mOff);
                    foreach (var k in UPDATED_FIELDS) { object v; if (prevRec.TryGetValue(k, out v)) m2[k] = v; else m2.Remove(k); }
                    m2.Remove("previous"); m2.Remove("updated");
                    SaveMod(m2); step = 4;
                    CosCheckAll();
                    if (wasOn) CosEnable(id, true);
                    step = 5;
                    try { DeleteTree(prev); DeleteTree(newer); } catch (Exception ex) { Say("cosmetic mods: left over after the roll back: " + ex.Message); }
                    Say("cosmetic mod rolled back: " + Str(m2, "name") + (ModVersionText(m2) != "" ? " -> " + ModVersionText(m2) : ""));
                }
                catch (Exception ex)
                {
                    var problems = new List<string>();
                    Action<string, Action> undo = (what, a) => { try { a(); } catch (Exception e2) { problems.Add(what + ": " + e2.Message); } };
                    if (step >= 4) undo("record", () => SaveMod(mOff));
                    if (step >= 3) undo("previous files", () => Directory.Move(files, Path.Combine(prev, "files")));
                    if (step >= 2) undo("files", () => Directory.Move(newer, files));
                    if (step >= 1 && wasOn) undo("switching it on again", () => { CosCheckAll(); CosEnable(id, true); });
                    bool on = false;
                    try { CosCheckAll(); on = Yes(CosMod(id), "enabled"); } catch { }
                    string state = " - kept the version you had (" + (on ? "ON" : "OFF") + ")";
                    if (problems.Count > 0) state += "; could not undo everything: " + problems[0];
                    throw new Exception(ex.Message + state);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------- the row's buttons (UI thread)
        void ModsUpdateClick(string id)
        {
            ModUpdate u;
            if (!modUpdates.TryGetValue(id, out u)) return;
            var m = CosMod(id);
            if (u.Source == "download" && InDownloads(u.Path) && File.Exists(u.Path))   // (the new file is already here)
            {
                if (!ModsFreeOrSay()) return;
                if (AskUpdate(m, u.Version, u.Path, false) && ModsFreeOrSay()) { string f = u.Path; ModsRun(() => CosUpdate(id, f, false)); }
                return;
            }
            lock (nexusAsked) nexusAsked[u.ModId] = DateTime.UtcNow;         // (its download is offered as this mod's update)
            ModsWatchDownloads();
            try { Process.Start(NexusPage(u.ModId, true)); } catch (Exception ex) { modsLine = "could not open the browser: " + ex.Message; Redraw(); return; }
            modsLine = u.Vague ? "Nexus opened in your browser - the mod has newer files; download the one that matches yours. It is offered here when it lands in Downloads." :
                "Nexus opened in your browser - download the new file there; it is offered here as the update when it lands in Downloads.";
            Redraw();
        }
        // (both file names shown - review C4)
        bool AskUpdate(Dictionary<string, object> m, string version, string path, bool older)
        {
            string cur = ModVersionText(m);
            return MessageBox.Show(this, "Update " + Str(m, "name") + (version != "" ? " to version " + version : "") + (older ? " (OLDER than yours)" : "") +
                (cur != "" ? " (you have " + cur + ")" : "") + "?\n\nInstalled:  " + Str(m, "source") + "\nNew file:  " + Path.GetFileName(path) +
                "\n\nIt is checked first, like any new mod. ON / OFF and your own settings files stay as they are; the " +
                "current version is kept, so Roll back on the mod returns to it.", "Mod update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
        void ModsRollbackClick(string id)
        {
            var m = CosMod(id);
            object o;
            var prevRec = m.TryGetValue("previous", out o) ? o as Dictionary<string, object> : null;
            if (prevRec == null || !ModsFreeOrSay()) return;
            var pv = ModUpdateRules.Parse(Path.GetFileNameWithoutExtension(Str(prevRec, "source")));
            if (MessageBox.Show(this, "Go back to the previous version of " + Str(m, "name") + (pv != null && pv.Version != "" ? " (" + pv.Version + ")" : "") + "?\n\n" +
                "The current files are removed; ON / OFF stays as it is.", "Roll back", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK && ModsFreeOrSay())
                ModsRun(() => CosRollback(id));
        }

        // (--shot <png> installed-update: sample mods - one with an update on Nexus, one whose new file is in Downloads and an older version
        //  kept for Roll back, one not from Nexus; nothing is read from or written to the store)
        void ShotInstalledUpdate()
        {
            Func<string, string, string, bool, string, Dictionary<string, object>> mk = (id, name, src, on, verdict) => new Dictionary<string, object> {
                { "id", id }, { "name", name }, { "source", src }, { "enabled", on }, { "verdict", verdict },
                { "notes", new List<string> { "replaces 214 game file(s)" } } };
            var a = mk("better-map-icons-1a2b3c", "Better Map Icons", "Better Map Icons-112-1-2-1760000000.7z", true, "OK");
            var b = mk("clear-water-4d5e6f", "Clear Water", "Clear Water-87-2-0-1761000000.zip", false, "OK");
            b["previous"] = new Dictionary<string, object> { { "source", "Clear Water-87-1-9-1755000000.zip" } };
            var c = mk("lod-fix-7a8b9c", "LOD fix", "lodfix.zip", true, PublicEdition ? "SANDBOX ONLY" : "OUR SERVERS ONLY");
            c["notes"] = new List<string> { "settings: replaces your Scalability.ini while ON (yours is kept and put back on OFF)" };
            cosCache = new List<Dictionary<string, object>> { a, b, c };
            installedCount = cosCache.Count;
            modUpdates.Clear();
            modUpdates["better-map-icons-1a2b3c"] = new ModUpdate { ModId = 112, Version = "1.3", FileTime = 1763000000 };
            modUpdates["clear-water-4d5e6f"] = new ModUpdate { ModId = 87, Version = "2.1", FileTime = 1763100000, Source = "download" };
            updatesCount = modUpdates.Count;
            modsLine = "2 mod update(s) available - Update opens the mod's files on Nexus Mods.";
            hover = "mod:update:better-map-icons-1a2b3c";
            statusText = "Ready.";
        }
        // (--shot <png> installed-thumbs: the same rows with pictures - small test pictures drawn here (never game art) put straight into the
        //  memory cache; Clear Water's is "still loading", the LOD fix is not from Nexus (the icon), a fourth mod has "newer files on Nexus")
        void ShotInstalledThumbs()
        {
            ShotInstalledUpdate();
            var d = new Dictionary<string, object> { { "id", "night-sky-2c3d4e" }, { "name", "Night Sky" }, { "source", "Night Sky-140-1-0-1750000000.zip" },
                { "enabled", true }, { "verdict", "OK" }, { "notes", new List<string> { "replaces 12 game file(s)" } } };
            cosCache.Add(d);
            installedCount = cosCache.Count;
            modUpdates["night-sky-2c3d4e"] = new ModUpdate { ModId = 140, FileTime = 1763200000, Vague = true };
            updatesCount = modUpdates.Count;
            modsLine = "3 mod update(s) available - Update opens the mod's files on Nexus Mods.";
            thumbNoFetch = true;
            installedThumbUrl = new Dictionary<int, string>();
            var colors = new Dictionary<int, System.Drawing.Color[]> {
                { 112, new[] { System.Drawing.Color.FromArgb(52, 92, 130), System.Drawing.Color.FromArgb(214, 176, 96) } },
                { 140, new[] { System.Drawing.Color.FromArgb(24, 30, 64), System.Drawing.Color.FromArgb(200, 210, 240) } } };
            foreach (var id in new[] { 112, 87, 140 })
            {
                string url = "https://staticdelivery.nexusmods.com/mods/4287/images/thumbnails/" + id + "/sample.png";
                installedThumbUrl[id] = url;
                if (!colors.ContainsKey(id)) continue;                   // (87: no picture yet - the placeholder)
                var bmp = new System.Drawing.Bitmap(INSTALLED_THUMB_W, INSTALLED_THUMB_H);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, INSTALLED_THUMB_W, INSTALLED_THUMB_H), colors[id][0], colors[id][1], 30f))
                {
                    g.FillRectangle(br, 0, 0, INSTALLED_THUMB_W, INSTALLED_THUMB_H);
                    using (var p = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(120, 255, 255, 255)))
                        for (int i = 0; i < 4; i++) g.FillEllipse(p, 6 + i * 14, 8 + (i % 2) * 10, 10, 10);
                }
                thumbs.Put(id + "-" + ThumbKey(url, INSTALLED_THUMB_W, INSTALLED_THUMB_H), bmp);
            }
        }

        // ---------------------------------------------------------------------------------------------------- command line
        static bool ModUpdatesCommandLine(string[] args)
        {
            var a = new List<string>(args);
            if (!a.Contains("--mod-updates-check") && !a.Contains("--mod-updates")) return false;
            AttachConsole(-1);
            Headless = true;
            NexusHttp.EnableTls();
            NexusHttp.NoNet = a.Contains("--no-net");
            Func<string, string> opt = k => { int i = a.IndexOf(k); return i >= 0 && i + 1 < a.Count ? a[i + 1] : ""; };
            Environment.ExitCode = 0;
            try
            {
                if (a.Contains("--mod-updates-check"))
                {
                    // (review N1: the public edition asks Nexus only once browse_enabled is on - its command line too)
                    if (!UpdatesAutoAllowed()) throw new Exception("Nexus requests are off in this edition until browse_enabled is on");
                    var ids = new List<int>();
                    foreach (var s in opt("--mod-updates-check").Split(','))
                    {
                        int id; if (!int.TryParse(s.Trim(), out id) || id <= 0 || id > 999999999) throw new Exception("mod ids are positive numbers: " + s);
                        if (!ids.Contains(id)) ids.Add(id);
                    }
                    var r = NexusUpdatesFetch(ids);
                    foreach (int id in ids)
                    {
                        var e = r[id];
                        Console.WriteLine(id + "  " + (e.Name != "" ? e.Name : "(not on Nexus)") + "  version " + (e.Version != "" ? e.Version : "-") + "  " + e.Status +
                            "  " + e.Files.Count + " file(s)" + (e.Thumb != "" ? "  picture " + e.Thumb : ""));
                        var fl = new List<NexusFileEntry>(e.Files); fl.Sort((x, y) => y.Date.CompareTo(x.Date));
                        for (int i = 0; i < Math.Min(5, fl.Count); i++)
                            Console.WriteLine("    " + fl[i].Category.PadRight(13) + " " + fl[i].Version.PadRight(8) + " " +
                                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(fl[i].Date).ToString("yyyy-MM-dd HH:mm") + "  " + fl[i].FileName);
                    }
                }
                if (a.Contains("--mod-updates"))
                {
                    var l = new Launcher();
                    l.LoadUpdates();
                    l.cosCache = CosMods();
                    var ids = new List<int>();
                    foreach (var m in l.cosCache) { var p = NexusOf(m); if (p != null && !ids.Contains(p.ModId)) ids.Add(p.ModId); }
                    bool net = UpdatesAutoAllowed();
                    if (!net) Console.WriteLine("Nexus checks are off in this edition until browse_enabled is on - downloads seen only");
                    else if (ids.Count > 0 && (a.Contains("--force") || DateTime.UtcNow - l.updatesChecked >= UPDATES_TTL))
                    {
                        try
                        {
                            l.updatesData = NexusUpdatesFetch(ids); l.updatesChecked = DateTime.UtcNow; knownNexus = l.updatesData;
                            SaveUpdatesCache(l.updatesChecked, l.updatesData, l.SeenCopy());
                            Console.WriteLine("asked Nexus Mods (" + ids.Count + " mod(s))");
                        }
                        catch (Exception ex) { Console.WriteLine("could not ask Nexus Mods: " + ex.Message); }
                    }
                    else Console.WriteLine("from this PC (checked " + (l.updatesChecked == DateTime.MinValue ? "never" : Age(l.updatesChecked)) + ")");
                    var seen = l.SeenCopy();
                    foreach (var m in l.cosCache)
                    {
                        var p = NexusOf(m); var u = UpdateFor(m, l.updatesData, seen, x => InDownloads(x) && File.Exists(x));
                        Console.WriteLine(Str(m, "id") + "  " + (p == null ? "not from Nexus" : "Nexus " + p.ModId + " version " + (p.Version != "" ? p.Version : "-")) +
                            (u != null ? "  UPDATE AVAILABLE " + (u.Vague ? "(newer files)" : u.Version) + " (" + u.Source + ")" : "") + (m.ContainsKey("previous") ? "  [previous kept]" : ""));
                    }
                    if (a.Contains("--thumbs"))                           // (where each row's picture would come from - nothing is loaded)
                    {
                        var browse = BrowseListThumbs();
                        foreach (var id in ids)
                        {
                            NexusModFiles nf; string url = l.updatesData.TryGetValue(id, out nf) && nf.Thumb != "" ? nf.Thumb : "";
                            if (url == "" && browse.ContainsKey(id)) url = browse[id];
                            Console.WriteLine("picture " + id + ": " + (url == "" ? "none known" : ThumbPlan(id + "-" + ThumbKey(url, INSTALLED_THUMB_W, INSTALLED_THUMB_H), net)));
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Environment.ExitCode = 1; }
            Console.WriteLine("network requests: " + NexusHttp.Attempts);
            return true;
        }
    }
}
