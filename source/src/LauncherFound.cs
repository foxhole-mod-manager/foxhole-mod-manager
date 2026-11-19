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
using System.Threading;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher
    {
        class FoundItem
        {
            public string Kind = "", Key = "", Rel = "", Path = "", Name = "", Verdict = "", Why = "";
            public long Size;
            public bool CanTake;
            public readonly List<string> Notes = new List<string>(), Companions = new List<string>();
        }
        static readonly string[] FoundKitNames = { "War-WindowsNoEditor_1_P.pak", "War-WindowsNoEditor_1_P.txt", "War-WindowsNoEditor_1_P.pak.kit",
                                                   "War-WindowsNoEditor_1_P.pak.stamp", "War-WindowsNoEditor_1_P.pak.sha256", "War-WindowsNoEditor_1_P.sig" };
        static readonly string[] PakCompanionExts = { ".sig", ".utoc", ".ucas" };
        // DLL names mod loaders use next to a game exe (proxy DLLs Windows loads from the exe's folder first)
        static readonly string[] LoaderDllNames = { "dwmapi.dll", "UE4SS.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll", "version.dll",
                                                    "dinput8.dll", "dxgi.dll", "d3d11.dll", "d3d9.dll", "winmm.dll", "winhttp.dll", "wininet.dll" };
        // the files a server kit sets up next to the exe (its own revert removes them)
        static readonly string[] KitWin64Names = { "dwmapi.dll", "UE4SS.dll", "FoxClient.dll" };

        List<FoundItem> foundCache = new List<FoundItem>();
        bool foundScanning, foundAgain;
        int foundCount;                                                  // (found paks - the sidebar's "+ N found")

        static bool IsLinkEntry(string p) { try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0; } catch { return true; } }
        static bool IsGamePakName(string name) { return string.Equals(name, "War-WindowsNoEditor.pak", StringComparison.OrdinalIgnoreCase); }
        static bool IsKitName(string name) { return Array.Exists(FoundKitNames, k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)); }
        static string FoundKey(string kind, string rel)
        {
            using (var h = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(kind + "|" + rel.ToLowerInvariant()))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }
        // a pak's file name made readable: "War-WindowsNoEditor_BetterLighting_P" -> "BetterLighting"
        static string PakDisplayName(string baseName)
        {
            string n = baseName;
            if (n.StartsWith("War-WindowsNoEditor_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(20);
            if (n.EndsWith("_P", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 2);
            n = n.Replace('_', ' ').Trim();
            // (review of 1.1.2, 6) nothing readable left ("War-WindowsNoEditor_2_P" -> "2"): the file's own name says more
            bool letters = false; foreach (char ch in n) if (char.IsLetter(ch)) { letters = true; break; }
            return letters ? n : baseName;
        }
        static string SizeText(long n) { return n >= 1 << 30 ? (n / (double)(1 << 30)).ToString("0.0") + " GB" : n >= 1 << 20 ? (n / (double)(1 << 20)).ToString("0.0") + " MB" : Math.Max(1, n >> 10) + " KB"; }

        static void WalkPaks(string dir, string rel, int depth, List<string> files)
        {
            string[] fs, ds;
            try { fs = Directory.GetFiles(dir); ds = Directory.GetDirectories(dir); } catch { return; }
            foreach (var f in fs) if (!IsLinkEntry(f)) files.Add((rel == "" ? "" : rel + "/") + Path.GetFileName(f));
            if (depth >= 4) return;
            foreach (var d in ds) if (!IsLinkEntry(d)) WalkPaks(d, (rel == "" ? "" : rel + "/") + Path.GetFileName(d), depth + 1, files);
        }

        // (review of 1.1.2, 8) a pak's War/Content-relative files, cached by (path, size, write time): Rescan / every mod operation reads only
        // paks that changed. A pak that could not be read is cached with its error (thrown again). The caller gets its own copy.
        static readonly Dictionary<string, KeyValuePair<HashSet<string>, string>> pakContentCache = new Dictionary<string, KeyValuePair<HashSet<string>, string>>(StringComparer.OrdinalIgnoreCase);
        static HashSet<string> PakContentCached(string path)
        {
            var fi = new FileInfo(path);
            string key = Path.GetFullPath(path) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            KeyValuePair<HashSet<string>, string> hit;
            lock (pakContentCache)
                if (!pakContentCache.TryGetValue(key, out hit))
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string err = null;
                    try { string mo; int v; foreach (var p in PakPaths(path, out mo, out v)) { string c = ContentRel(mo, p); if (c != "") set.Add(c); } }
                    catch (Exception ex) { err = ex.Message; }
                    if (pakContentCache.Count > 512) pakContentCache.Clear();
                    hit = new KeyValuePair<HashSet<string>, string>(set, err);
                    pakContentCache[key] = hit;
                }
            if (hit.Value != null) throw new Exception(hit.Value);
            return new HashSet<string>(hit.Key, StringComparer.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------------------------- detection (worker / command line)
        List<FoundItem> FoundScan() { lock (cosLock) return FoundScanLocked(); }
        List<FoundItem> FoundScanLocked()
        {
            var r = new List<FoundItem>();
            string game = ModsGameDir(), paks = ModsPaks();
            if (game == "" || paks == "" || !Directory.Exists(paks) || IsLinkEntry(paks)) return r;
            var inst = CosInstalled();
            var files = new List<string>();
            WalkPaks(paks, "", 0, files);
            var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            // (review of 1.1.2, 3) the same path form as installed.json's keys (PaksFile, LauncherMods.cs); a name that is no safe path is left out
            Func<string, string> full = rel => { try { return PaksFile(paks, rel); } catch { return ""; } };
            Func<string, bool> recorded = rel => { string fp = full(rel); return fp == "" || inst.ContainsKey(fp.ToLowerInvariant()); };
            var paksFound = new List<FoundItem>();
            foreach (var rel in files)
            {
                string name = Path.GetFileName(rel), ext = Path.GetExtension(name).ToLowerInvariant();
                if (IsKitName(name) || recorded(rel)) continue;
                if (ext == ".pak")
                {
                    if (IsGamePakName(name)) continue;                    // (the game itself - or a copy of it, at any depth)
                    var f = new FoundItem { Kind = "pak", Rel = rel, Path = full(rel), Name = PakDisplayName(Path.GetFileNameWithoutExtension(name)), CanTake = true };
                    try { f.Size = new FileInfo(f.Path).Length; } catch { }
                    string stem = rel.Substring(0, rel.Length - 4);
                    foreach (var ce in PakCompanionExts)
                        if (present.Contains(stem + ce) && !recorded(stem + ce)) f.Companions.Add(Path.GetFileName(stem + ce));
                    paksFound.Add(f);
                }
                else if (ext == ".zip" || ext == ".7z" || ext == ".rar")
                {
                    var f = new FoundItem { Kind = "archive", Rel = rel, Path = full(rel), Name = name, Verdict = "ARCHIVE" };
                    try { f.Size = new FileInfo(f.Path).Length; } catch { }
                    f.Notes.Add("an archive in the game's Paks folder - Foxhole ignores archives, so this mod does nothing there");
                    if (ext == ".zip") { f.CanTake = true; f.Notes.Add("Add to the app unpacks it safely and keeps its .pak / settings files (the archive itself is left where it is)"); }
                    else if (ext == ".7z") { f.Why = NO_7Z; f.Notes.Add(NO_7Z); }
                    else { f.Why = "Unpack the .rar first, then add the .pak."; f.Notes.Add("the app does not open .rar archives - unpack it first, then add the .pak"); }
                    r.Add(f);
                }
            }
            // the verdicts: the same check as added mods - against the game pak, the server's own pak, the switched-on mods and every other
            // found pak (those are in the game, so they load too)
            if (paksFound.Count > 0)
            {
                HashSet<string> baseSet = null, ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { baseSet = BaseIndex(); } catch { }
                try
                {
                    string op = Path.Combine(paks, OUR_MOD_PAK);
                    if (File.Exists(op)) ours = PakContentCached(op);
                }
                catch { }
                var onSets = new List<KeyValuePair<string, HashSet<string>>>();
                foreach (var m in CosMods())
                {
                    if (!Yes(m, "enabled")) continue;
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rel in StrList(m, "paks"))
                        try { set.UnionWith(PakContentCached(Path.Combine(CosDir, Str(m, "id"), "files", rel))); }
                        catch { }
                    onSets.Add(new KeyValuePair<string, HashSet<string>>(Str(m, "name"), set));
                }
                var sets = new Dictionary<FoundItem, HashSet<string>>();
                var errs = new Dictionary<FoundItem, string>();
                foreach (var f in paksFound)
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try { set = PakContentCached(f.Path); }
                    catch (Exception ex) { errs[f] = ex.Message; }
                    sets[f] = set;
                }
                foreach (var f in paksFound)
                {
                    var others = new List<KeyValuePair<string, HashSet<string>>>(onSets);
                    foreach (var o in paksFound) if (o != f) others.Add(new KeyValuePair<string, HashSet<string>>(o.Name + " (found)", sets[o]));
                    string err; errs.TryGetValue(f, out err);
                    string folder = f.Rel.Contains("/") ? f.Rel.Substring(0, f.Rel.LastIndexOf('/')) : "";
                    f.Verdict = PakVerdict(sets[f], err, baseSet, ours, others, folder, f.Notes);
                    if (f.Companions.Count > 0) f.Notes.Add("comes with " + string.Join(", ", f.Companions.ToArray()));
                }
                r.InsertRange(0, paksFound);
            }
            // loaders: any DLL next to the game exe (a plain install has none there)
            string win64 = Path.Combine(game, "War", "Binaries", "Win64");
            if (Directory.Exists(win64))
            {
                var dlls = new List<string>(); bool loader = false;
                try { foreach (var d in Directory.GetFiles(win64, "*.dll")) dlls.Add(Path.GetFileName(d)); } catch { }
                dlls.Sort(StringComparer.OrdinalIgnoreCase);
                if (dlls.Count > 0)
                {
                    bool kit = File.Exists(Path.Combine(game, "_FoxholeOffline", "install.json"));
                    var f = new FoundItem { Kind = "loader", Rel = "War/Binaries/Win64", Path = win64, Verdict = "WARNING" };
                    foreach (var d in dlls)
                    {
                        bool known = Array.Exists(LoaderDllNames, x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase));
                        loader |= known;
                        bool byKit = kit && Array.Exists(KitWin64Names, x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase));
                        f.Notes.Add(d + (known ? " - a mod loader's file" : " - not a file of the game") + (byKit ? " (set up by a server kit installed in this game)" : ""));
                    }
                    f.Name = (loader ? "A mod loader is installed in your game" : "Extra DLL files next to the game") + " (" + string.Join(", ", dlls.ToArray()) + ")";
                    f.Notes.Insert(0, "it loads code into Foxhole - official servers may not allow it. The app only shows it: nothing here is changed or deleted.");
                    if (kit) f.Notes.Add("a server kit is set up in this game - its own \"Back to official Foxhole\" takes its files out");
                    r.Add(f);
                }
            }
            foreach (var f in r) f.Key = FoundKey(f.Kind, f.Rel);
            return r;
        }

        // a fresh scan on a worker; the page shows the last result until it is done
        void FoundRefresh()
        {
            if (Headless || shotState != "") return;
            if (foundScanning) { foundAgain = true; return; }
            foundScanning = true;
            new Thread(() =>
            {
                List<FoundItem> r = null;
                try { r = FoundScan(); } catch (Exception ex) { Say("mods found in the game: " + ex.Message); }
                Ui(() =>
                {
                    foundScanning = false;
                    if (r != null) { foundCache = r; foundCount = r.FindAll(x => x.Kind == "pak").Count; }
                    Redraw();
                    if (foundAgain) { foundAgain = false; FoundRefresh(); }
                });
            }) { IsBackground = true }.Start();
        }

        // ---------------------------------------------------------------------------------------------------- take over
        // a found pak becomes a managed mod that is ON: one copy into the store (the game's file stays where it is - no copy back), recorded
        // with its hash in installed.json (OFF / Remove then delete exactly that file). Refused while Foxhole runs or the file is in use.
        string CosTakeOver(string rel) { lock (cosLock) return CosTakeOverLocked(rel); }
        string CosTakeOverLocked(string rel)
        {
            string paks = ModsPaks();
            if (paks == "" || !Directory.Exists(paks)) throw new Exception("Foxhole was not found (its War\\Content\\Paks folder)");
            rel = SafeRel(rel);
            string full = PaksFile(paks, rel), name = Path.GetFileName(full);   // (installed.json's key form - LauncherMods.cs PaksFile)
            string sub = rel.Contains("/") ? rel.Substring(0, rel.LastIndexOf('/')) : "";
            if (!File.Exists(full)) throw new Exception(rel + " is not in the game any more");
            if (!name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) throw new Exception("only a .pak can be managed by the app");
            if (IsKitName(name) || IsGamePakName(name))
                throw new Exception(name + " belongs to the game or a server kit - the app never manages it");
            string top = Path.GetFullPath(paks).TrimEnd('\\');
            for (string d = Path.GetFullPath(full); d != null && !string.Equals(d.TrimEnd('\\'), top, StringComparison.OrdinalIgnoreCase); d = Path.GetDirectoryName(d))
                if (IsLinkEntry(d)) throw new Exception(rel + " is reached through a link - left alone");
            var inst = CosInstalled();
            if (inst.ContainsKey(full.ToLowerInvariant())) throw new Exception(name + " is already managed by the app");
            if (ModsGameRunning()) throw new Exception("Foxhole is running - close it first, then take over " + name);
            var comps = new List<string>();
            foreach (var ce in PakCompanionExts)
            {
                string c = Path.ChangeExtension(full, ce);
                if (File.Exists(c) && !IsLinkEntry(c) && !inst.ContainsKey(c.ToLowerInvariant())) comps.Add(c);
            }
            var all = new List<string> { full }; all.AddRange(comps);
            foreach (var f in all)
            {
                try { using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)) { } }
                catch (IOException) { throw new Exception(Path.GetFileName(f) + " is in use (is Foxhole running?) - close it and try again"); }
            }
            if (new FileInfo(full).Length > ARCHIVE_MAX) throw new Exception("the file is larger than " + (ARCHIVE_MAX >> 30) + " GB - not a cosmetic mod");
            string hash = Sha256(full);
            foreach (var old in CosMods())
            {
                if (Str(old, "sha256") == hash) throw new Exception("this mod is already in the app (" + Str(old, "name") + ") - switch that one on, and delete this copy from the game if you like");
                foreach (var orel in PakFiles(old))
                {
                    string ot; try { ot = PakTarget(paks, old, orel); } catch { continue; }
                    if (string.Equals(ot, full, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("the app's mod " + Str(old, "name") + " uses the same file name (" + name + ") - remove one of the two first");
                }
            }
            string baseName = Path.GetFileNameWithoutExtension(name), dname = PakDisplayName(baseName), nexus = "";
            var nf = ParseNexus(baseName);
            if (nf != null) { dname = nf.Name; nexus = NEXUS_URL + nf.ModId; }
            string id = NewModId(dname, hash), root = Path.Combine(CosDir, id), files = Path.Combine(root, "files");
            DeleteTree(root);
            Directory.CreateDirectory(files);
            try
            {
                var hashes = new Dictionary<string, string>();
                foreach (var f in all)
                {
                    string dst = Path.Combine(files, Path.GetFileName(f));
                    CopyPlain(f, dst);
                    string h = Sha256(dst);
                    if (Sha256(f) != h || (f == full && h != hash)) throw new Exception(Path.GetFileName(f) + " changed while it was copied - try again");
                    hashes[f] = h;
                }
                var m = new Dictionary<string, object>();
                m["id"] = id; m["name"] = dname; m["source"] = name; m["nexus"] = nexus; m["sha256"] = hash;
                if (nf != null) m["nexus_file"] = NexusFileRecord(nf);
                m["added"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); m["enabled"] = true;
                m["taken_over"] = m["added"]; m["found_at"] = rel; m["paks_dir"] = sub;
                m["paks"] = new List<string> { name };
                m["companions"] = comps.ConvertAll(c => Path.GetFileName(c));
                m["configs"] = new List<string>(); m["ignored"] = new List<string>();
                SaveMod(m);
                foreach (var kv in hashes) inst[kv.Key.ToLowerInvariant()] = kv.Value;
                WriteJson(CosInstalledPath, inst);
            }
            catch { try { DeleteTree(root); } catch { } throw; }
            CosCheckAll();
            Say("cosmetic mod taken over from the game: " + dname + " (ON, " + rel + ")");
            return id;
        }

        // ---------------------------------------------------------------------------------------------------- command line
        void FoundPrint()
        {
            foreach (var f in FoundScan())
            {
                Console.WriteLine("found " + f.Kind + "  " + f.Rel + "  " + f.Verdict + "  " + (f.CanTake ? (f.Kind == "pak" ? "take-over" : "add") : "read-only") + "  " + f.Name);
                foreach (var n in f.Notes) Console.WriteLine("      " + n);
            }
        }

        // ---------------------------------------------------------------------------------------------------- UI
        FoundItem FoundByKey(string key) { foreach (var f in foundCache) if (f.Key == key) return f; return null; }
        // Installed's clicks of this section ("mod:" ids): rescan, take:<key>, addfound:<key>, found:<key> (the explanation)
        bool FoundClick(string h)
        {
            if (h == "rescan") { FoundRefresh(); modsLine = "Looking for mods in your game..."; Redraw(); return true; }
            FoundItem f = null;
            foreach (var pre in new[] { "take:", "addfound:", "found:" }) if (h.StartsWith(pre)) f = FoundByKey(h.Substring(pre.Length));
            if (!h.StartsWith("take:") && !h.StartsWith("addfound:") && !h.StartsWith("found:")) return false;
            if (f == null) return true;
            if (h.StartsWith("found:"))
            {
                string head = f.Kind == "pak" ? "Found in your game: " + f.Rel + "\n\nPut into Foxhole by hand or by another tool. \"Manage with the app\" keeps it ON and lets the app " +
                                                "switch it off and on, check it, update or remove it. Nothing changes before you press it." :
                              f.Kind == "archive" ? "Found in your game: " + f.Rel : f.Name;
                MessageBox.Show(this, head + "\n\n" + (f.Verdict != "" ? f.Verdict + "\n" : "") + string.Join("\n", f.Notes.ToArray()), "Found in your game");
                return true;
            }
            if (!f.CanTake) return true;
            string rel = f.Rel, path = f.Path;
            if (h.StartsWith("take:"))
            {
                if (MessageBox.Show(this, "Manage " + f.Name + " with the app?\n\nIt stays ON in your game (" + rel + "). The app keeps a copy, so you can switch it off and on, " +
                    "update or remove it like the mods you added here.", "Found in your game", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK && ModsFreeOrSay())
                    ModsRun(() => CosTakeOver(rel));
            }
            else if (ModsFreeOrSay()) ModsRun(() => CosAdd(path, false));
            return true;
        }

        // the rows of Installed: the managed mods, then (when any) the section's title row and the found items
        List<object> InstalledRows()
        {
            var r = new List<object>();
            foreach (var m in cosCache) r.Add(m);
            if (foundCache.Count > 0) { r.Add("found-head"); foreach (var f in foundCache) r.Add(f); }
            return r;
        }

        void DrawFoundHead(Graphics g, Rectangle r)
        {
            Glyph(g, "search", new Rectangle(r.X + 4, r.Y + 22, 18, 18), CAccent);
            TxtH(g, "FOUND IN YOUR GAME", 15, CText, new Rectangle(r.X + 32, r.Y + 12, 400, 24), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 2);
            Txt(g, "Put into Foxhole by hand or by another tool - nothing changes until you press a button.", 12, false, CMuted,
                new Rectangle(r.X + 32, r.Y + 36, r.Width - 180, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            Btn(g, "mod:rescan", foundScanning ? "Scanning..." : "Rescan", new Rectangle(r.Right - 120, r.Y + 16, 120, 32), 2, !foundScanning, "refresh");
        }

        void DrawFoundRow(Graphics g, FoundItem f, Rectangle r, bool free)
        {
            bool loader = f.Kind == "loader";
            Fill(g, r, hover == "mod:found:" + f.Key ? CSurf2 : CSurf, 10);
            Edge(g, r, loader ? Color.FromArgb(120, CWarn.R, CWarn.G, CWarn.B) : CLine, 10);
            int bx = r.Right - 14;
            if (f.Kind == "pak") { bx -= 186; Btn(g, "mod:take:" + f.Key, "Manage with the app", new Rectangle(bx, r.Y + 14, 186, 34), 1, free && f.CanTake); bx -= 10; }
            else if (f.Kind == "archive" && f.CanTake) { bx -= 140; Btn(g, "mod:addfound:" + f.Key, "Add to the app", new Rectangle(bx, r.Y + 14, 140, 34), 1, free, "plus"); bx -= 10; }
            if (f.Verdict != "") bx -= Badge(g, f.Verdict, bx, r.Y + 19, f.Kind == "archive" ? CMuted : loader ? CWarn : VerdictTone(f.Verdict), true) + 10;
            hits["mod:found:" + f.Key] = new Rectangle(r.X, r.Y, bx - r.X, r.Height);
            var tr = new Rectangle(r.X + 12, r.Y + 13, INSTALLED_THUMB_W, INSTALLED_THUMB_H);
            Fill(g, tr, CSurf2, 6);
            Glyph(g, loader ? "warn" : f.Kind == "archive" ? "download" : "box", new Rectangle(tr.X + (tr.Width - 20) / 2, tr.Y + (tr.Height - 20) / 2, 20, 20), loader ? CWarn : CMuted);
            int tw = bx - 12 - (r.X + 88);
            string tag = loader ? "LOADER" : "FOUND";
            int tagW = TextWidth(tag, 12, true, false) + 18;
            int nw = Math.Min(TextWidth(f.Name, 15, true, false) + 4, tw - tagW - 10);
            Txt(g, f.Name, 15, true, CText, new Rectangle(r.X + 88, r.Y + 10, Math.Max(40, nw), 22), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            Badge(g, tag, r.X + 88 + Math.Max(40, nw) + 10, r.Y + 9, loader ? CWarn : CAccent);
            string line;
            if (f.Kind == "pak")
            {
                string first = f.Notes.Count > 1 && f.Notes[0].StartsWith("replaces") ? f.Notes[1] : f.Notes.Count > 0 ? f.Notes[0] : "";
                line = "Paks\\" + f.Rel.Replace('/', '\\') + "  -  " + SizeText(f.Size) + (first != "" ? "  -  " + first : "");
            }
            else if (f.Kind == "archive") line = f.Why != "" ? "The game ignores archives in Paks. " + f.Why : "The game ignores archives in Paks - add it through the app (" + SizeText(f.Size) + ").";
            else line = "It loads code into Foxhole - official servers may not allow it. Shown only: the app never changes it.";
            Txt(g, line, 12, false, loader ? CWarn : CMuted, new Rectangle(r.X + 88, r.Y + 35, tw, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
        }

        // (--shot <png> installed-found: two managed mods and the found section with each kind; nothing is read from or written to the game)
        void ShotInstalledFound()
        {
            Func<string, string, string, bool, string, Dictionary<string, object>> mk = (id, name, src, on, verdict) => new Dictionary<string, object> {
                { "id", id }, { "name", name }, { "source", src }, { "enabled", on }, { "verdict", verdict }, { "notes", new List<string> { "replaces 214 game file(s)" } } };
            cosCache = new List<Dictionary<string, object>> { mk("clear-water-4d5e6f", "Clear Water", "clearwater.pak", true, "OK") };
            installedCount = cosCache.Count;
            modUpdates.Clear(); updatesCount = 0;
            Func<string, string, string, long, string, FoundItem> fi = (kind, rel, name, size, verdict) => new FoundItem { Kind = kind, Rel = rel, Name = name, Size = size, Verdict = verdict, CanTake = kind != "loader" };
            var a = fi("pak", "War-WindowsNoEditor_BetterLighting_P.pak", "BetterLighting", 48 << 20, "OK");
            a.Notes.Add("replaces 37 game file(s)");
            var b = fi("pak", "~mods/War-WindowsNoEditor_TerrainColours.pak", "TerrainColours", 210 << 20, VERDICT_EDITION);
            b.Notes.Add("replaces 12 game file(s)"); b.Notes.Add("3 of them outside the folders official Foxhole accepts from mods");
            var c = fi("pak", "LogicMods/MapMarkers.pak", "MapMarkers", 2 << 20, "NEEDS A MOD LOADER");
            c.Notes.Add("in the LogicMods folder - a blueprint mod that only a mod loader (UE4SS) runs");
            var d = fi("archive", "Foxhole LOD Fix-186-1-1772814482.7z", "Foxhole LOD Fix-186-1-1772814482.7z", 3 << 10, "ARCHIVE");
            d.CanTake = false; d.Why = NO_7Z;
            var e = fi("loader", "War/Binaries/Win64", "A mod loader is installed in your game (dwmapi.dll, UE4SS.dll)", 0, "WARNING");
            foundCache = new List<FoundItem> { a, b, d, e };               // (MapMarkers left out: every kind on one page)
            foreach (var f in foundCache) f.Key = FoundKey(f.Kind, f.Rel);
            foundCount = 2;
            hover = "mod:take:" + a.Key;
            statusText = "Ready.";
        }
    }
}
