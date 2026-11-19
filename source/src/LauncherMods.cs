// Foxhole Mod Manager (unofficial)
// Copyright (C) 2026 Foxhole Mod Manager contributors
// SPDX-License-Identifier: GPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
// version 3 as published by the Free Software Foundation. This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License (LICENSE) for more details.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;

using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher
    {
        static readonly string ModsRoot = Path.Combine(DataDir, "mods");                        // (public: in app data - Launcher.cs DataDir)
        static readonly string CosDir = Path.Combine(ModsRoot, "cosmetic");
        static readonly string CosInstalledPath = Path.Combine(CosDir, "installed.json");
        static readonly string SevenZr = Path.Combine(ModsRoot, "tools", "7zr.exe");
        const string SEVENZR_URL = "https://www.7-zip.org/a/7zr.exe";                 // 7-Zip 26.04 console (Igor Pavlov, LGPL / public domain)
        const string SEVENZR_SHA = "256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282";
        const long SEVENZR_SIZE = 602624;
        const string NEXUS_URL = "https://www.nexusmods.com/foxhole/mods/";
        const string OUR_MOD_PAK = "War-WindowsNoEditor_1_P.pak";
        // the folders the unpatched game keeps from extra paks (its start-up filter; War\Content-relative, read from the exe 2026-11-18)
        static readonly string[] OfficialFolders = { "Audio/", "Fonts/", "FX/", "Localization/", "Slate/", "Textures/", "Materials/Vehicles/",
            "Materials/Weapons/", "Meshes/Character/", "Meshes/Environment/Props/", "Meshes/Pickups/", "Meshes/Resources/", "Meshes/Vehicles/",
            "Meshes/UI/", "Meshes/Weapons/", "Blueprints/Character/BPCharactersRenderInfo", "Blueprints/BPDefaultCalloutComponent" };
        static readonly string[] ConfigNames = { "Scalability.ini", "Engine.ini", "Game.ini", "GameUserSettings.ini", "Input.ini", "DeviceProfiles.ini" };

        bool modsOpen;
        int modsPage;
        string modsLine = "";
        string modsGameOverride = "", modsConfigOverride = "", modsBasePakOverride = "";   // (tests: --mods-game / --mods-config / --mods-basepak)
        FileSystemWatcher modsWatch;
        readonly HashSet<string> modsOffered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------------------------------------------- places
        string ModsGameDir() { return modsGameOverride != "" ? modsGameOverride : MeGameDir(); }
        string ModsPaks() { string g = ModsGameDir(); return g == "" ? "" : Path.Combine(g, "War", "Content", "Paks"); }
        string ModsConfig()
        {
            if (modsConfigOverride != "") return modsConfigOverride;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foxhole", "Saved", "Config", "WindowsNoEditor");
        }

        // (mod files can come read-only - the LOD fix's Scalability.ini in its .7z: copied as is, OFF could not delete it)
        static void CopyPlain(string src, string dst)
        {
            if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
            File.Copy(src, dst, true);
            File.SetAttributes(dst, FileAttributes.Normal);
        }
        static void DeletePlain(string p) { if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); } }
        static void DeleteTree(string d)
        {
            if (!Directory.Exists(d)) return;
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(d, false); return; }   // (the link only)
            foreach (var f in Directory.GetFiles(d)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            foreach (var sub in Directory.GetDirectories(d)) DeleteTree(sub);
            Directory.Delete(d, false);
        }
        // (review: two threads saving the same file shared "<file>.tmp" - now a temp name of its own and one writer at a time)
        static readonly object jsonWriteLock = new object();
        static void WriteJson(string path, object o)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o));
            try
            {
                lock (jsonWriteLock)
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        // (review S6) every change to the mod store - add / on / off / remove / check / update / roll back - one at a time (the UI thread
        // never takes it: it only reads)
        static readonly object cosLock = new object();
        // Foxhole (the copy the mods go into) running - its paks are locked; an update / roll back is refused up front (review S2)
        bool ModsGameRunning()
        {
            string game = ModsGameDir();
            if (game == "") return false;
            string exe;
            try { exe = Path.GetFullPath(Path.Combine(game, "War", "Binaries", "Win64", "War-Win64-Shipping.exe")); } catch { return false; }
            foreach (var p in Process.GetProcessesByName("War-Win64-Shipping"))
            {
                try { if (string.Equals(Path.GetFullPath(p.MainModule.FileName), exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }
        // (UI thread, review S3) the store may change now: not while a launcher update / Play / Restore runs (buttons off) or the offline
        // sandbox is on (it set the player's mod files aside); else a line says why
        bool ModsFree() { return buttonsOn && !offlineBusy && !busy; }
        const string VERDICT_PARTLY = "WON'T LOAD ONLINE";
        const string VERDICT_EDITION = VERDICT_PARTLY;
        bool ModsFreeOrSay()
        {
            if (ModsFree()) return true;
            modsLine = offlineBusy ? "The game is being restored - mods change once that has finished." : "Busy - try again when the current step has finished.";
            Redraw();
            return false;
        }
        static List<string> StrList(Dictionary<string, object> d, string k)
        {
            var r = new List<string>();
            object v;
            if (d.TryGetValue(k, out v) && v is IEnumerable && !(v is string)) foreach (var x in (IEnumerable)v) r.Add(x.ToString());
            return r;
        }
        static Dictionary<string, object> CosInstalled() { var d = ReadJson(CosInstalledPath); return d ?? new Dictionary<string, object>(); }

        // every added mod, oldest first
        static List<Dictionary<string, object>> CosMods()
        {
            var r = new List<Dictionary<string, object>>();
            if (!Directory.Exists(CosDir)) return r;
            foreach (var d in Directory.GetDirectories(CosDir))
            {
                var m = ReadJson(Path.Combine(d, "mod.json"));
                if (m != null && Str(m, "id") != "") r.Add(m);
            }
            r.Sort((a, b) => string.CompareOrdinal(Str(a, "added"), Str(b, "added")));
            return r;
        }
        static Dictionary<string, object> CosMod(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-z0-9][a-z0-9-]{0,79}$")) throw new Exception("no such mod: " + id);
            string f = Path.Combine(CosDir, id, "mod.json");
            var m = File.Exists(f) ? ReadJson(f) : null;
            if (m == null || Str(m, "id") != id) throw new Exception("no such mod: " + id);
            return m;
        }
        static void SaveMod(Dictionary<string, object> m)
        {
            string id = Str(m, "id");
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,79}$")) throw new Exception("bad mod record");
            WriteJson(Path.Combine(CosDir, id, "mod.json"), m);
        }

        // ---------------------------------------------------------------------------------------------------- add
        // a .pak, .zip or .7z -> the store; returns the new mod's id
        string CosAdd(string file, bool yes) { lock (cosLock) return CosAddLocked(file, yes); }
        string CosAddLocked(string file, bool yes)
        {
            if (!File.Exists(file)) throw new Exception("file not found: " + file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".pak" && ext != ".zip" && ext != ".7z") throw new Exception("a cosmetic mod is a .pak, .zip or .7z file");
            string baseName = Path.GetFileNameWithoutExtension(file);
            // (Nexus Mods names its files "<Name>-<mod id>-<version...>-<time>": the page is .../foxhole/mods/<mod id>; the version and the
            //  upload time are kept for update checks - LauncherModUpdates.cs, LauncherModUpdateRules.cs Parse)
            string name = baseName, nexus = "";
            var nf = ParseNexus(baseName);
            if (nf != null) { name = nf.Name; nexus = NEXUS_URL + nf.ModId; }
            if (new FileInfo(file).Length > ARCHIVE_MAX) throw new Exception("the file is larger than " + (ARCHIVE_MAX >> 30) + " GB - not a cosmetic mod");
            string hash = Sha256(file);
            foreach (var old in CosMods())
                if (Str(old, "sha256") == hash) throw new Exception("this mod is already added (" + Str(old, "name") + ")");
            string id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (id.Length > 40) id = id.Substring(0, 40).Trim('-');
            if (id == "") id = "mod";
            id += "-" + hash.Substring(0, 6);
            if (File.Exists(Path.Combine(CosDir, id, "mod.json"))) id = id.Substring(0, id.Length - 6) + hash.Substring(0, 16);   // (another file whose hash starts the same: never its folder)
            string root = Path.Combine(CosDir, id), files = Path.Combine(root, "files");
            string tmp = Path.Combine(CosDir, "_unpack-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            DeleteTree(root);
            Directory.CreateDirectory(files);
            try
            {
                var paks = new List<string>(); var configs = new List<string>(); var ignored = new List<string>();
                if (ext == ".pak") { File.Copy(file, Path.Combine(files, SafeRel(Path.GetFileName(file)))); paks.Add(Path.GetFileName(file)); }
                else
                {
                    // (unpacked into a fresh temp folder, every path checked before writing; then only .pak and the allowed settings files go into
                    //  the store - nothing else of an archive is ever kept, run or copied into the game)
                    Directory.CreateDirectory(tmp);
                    if (ext == ".zip") SafeUnzip(file, tmp); else Unpack7z(file, tmp, yes);
                    AssertNoLinks(tmp);
                    CheckUnpacked(tmp);
                    var zone = ReadZone(file);
                    foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(tmp.Length + 1).Replace('\\', '/');
                        string fn = Path.GetFileName(f);
                        bool pak = fn.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
                        bool cfg = Array.Exists(ConfigNames, c => string.Equals(c, fn, StringComparison.OrdinalIgnoreCase));
                        if (!pak && !cfg) { ignored.Add(rel); continue; }
                        string dst = InsideOrThrow(files, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Move(f, dst);
                        WriteZone(dst, zone);
                        (pak ? paks : configs).Add(rel);
                    }
                    DeleteTree(tmp);
                }
                if (paks.Count == 0 && configs.Count == 0)
                    throw new Exception("no game pak or settings file in it (" + (ignored.Count > 0 ? string.Join(", ", ignored.ToArray()) : "empty") + ")");
                var m = new Dictionary<string, object>();
                m["id"] = id; m["name"] = name; m["source"] = Path.GetFileName(file); m["nexus"] = nexus; m["sha256"] = hash;
                if (nf != null) m["nexus_file"] = NexusFileRecord(nf);
                m["added"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); m["enabled"] = false;
                m["paks"] = paks; m["configs"] = configs; m["ignored"] = ignored;
                SaveMod(m);
                CosCheckAll();
                Say("cosmetic mod added: " + name + " (" + paks.Count + " pak(s), " + configs.Count + " settings file(s))");
                return id;
            }
            catch { try { DeleteTree(root); DeleteTree(tmp); } catch { } throw; }
        }

        const long ARCHIVE_MAX = 4L << 30;          // the archive file
        const long UNPACKED_MAX = 8L << 30;         // everything it unpacks to
        const int ENTRIES_MAX = 20000;
        static readonly Regex ReservedName = new Regex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase);

        // an archive entry's path -> a safe relative path, or an exception
        static string SafeRel(string name)
        {
            string n = (name ?? "").Replace('\\', '/');
            if (n == "" || n.Length > 240 || n.StartsWith("/") || n.Contains(":") || n.IndexOf('\0') >= 0) throw new Exception("unsafe path in the archive: " + name);
            foreach (var seg in n.Split('/'))
            {
                if (seg == "" || seg == "." || seg == ".." || seg.EndsWith(".") || seg.EndsWith(" ") || ReservedName.IsMatch(seg) ||
                    seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new Exception("unsafe path in the archive: " + name);
            }
            return n;
        }
        // root + rel, checked to stay inside root (after the OS resolves it)
        static string InsideOrThrow(string root, string rel)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(root, SafeRel(rel).Replace('/', '\\')));
            if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe path in the archive: " + rel);
            return full;
        }
        // no symbolic link / junction anywhere under d (a link could point the game or the store at another place)
        static void AssertNoLinks(string d)
        {
            foreach (var e in Directory.GetFileSystemEntries(d))
            {
                var a = File.GetAttributes(e);
                if ((a & FileAttributes.ReparsePoint) != 0) throw new Exception("the archive holds a link (" + Path.GetFileName(e) + ") - refused");
                if ((a & FileAttributes.Directory) != 0) AssertNoLinks(e);
            }
        }
        // .zip, entry by entry (ZipFile.ExtractToDirectory has no zip-slip guard on .NET Framework 4.x)
        static void SafeUnzip(string zip, string dest)
        {
            using (var z = ZipFile.OpenRead(zip))
            {
                if (z.Entries.Count > ENTRIES_MAX) throw new Exception("the archive holds too many files (" + z.Entries.Count + ")");
                long total = 0;
                foreach (var e in z.Entries) total += e.Length;
                if (total > UNPACKED_MAX) throw new Exception("the archive unpacks to more than " + (UNPACKED_MAX >> 30) + " GB - refused");
                long written = 0;
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) continue;              // (a folder entry)
                    if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new Exception("the archive holds a link (" + e.FullName + ") - refused");
                    string dst = InsideOrThrow(dest, e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    using (var src = e.Open()) using (var f = File.Create(dst))
                    {
                        var buf = new byte[1 << 16]; int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            written += n;                                                  // (the real bytes, not the header's claim)
                            if (written > UNPACKED_MAX) throw new Exception("the archive unpacks to more than " + (UNPACKED_MAX >> 30) + " GB - refused");
                            f.Write(buf, 0, n);
                        }
                    }
                }
            }
        }
        static void Check7zListing(string archive)
        {
            string outp, err;
            if (Run7z("l -slt -sccUTF-8 -- \"" + archive + "\"", 60000, out outp, out err) != 0) throw new Exception("7-Zip could not read the archive");
            var lines = outp.Replace("\r\n", "\n").Split('\n');
            int at = Array.IndexOf(lines, "----------");                                       // (entries follow the archive's own block)
            if (at < 0) throw new Exception("7-Zip could not list the archive");
            long total = 0; int count = 0;
            var entry = new Dictionary<string, string>();
            var keyLine = new Regex(@"^([A-Za-z][A-Za-z ]*) = (.*)$");
            for (int i = at + 1; i <= lines.Length; i++)
            {
                string line = i < lines.Length ? lines[i] : "";
                if (line != "")
                {
                    var km = keyLine.Match(line);
                    if (!km.Success || entry.ContainsKey(km.Groups[1].Value)) throw new Exception("7-Zip's listing of the archive looks wrong - refused");
                    entry[km.Groups[1].Value] = km.Groups[2].Value;
                    continue;
                }
                if (entry.Count == 0) continue;
                string path, attr, size, enc;
                if (!entry.TryGetValue("Path", out path)) throw new Exception("7-Zip's listing of the archive looks wrong - refused");
                entry.TryGetValue("Attributes", out attr); entry.TryGetValue("Size", out size); entry.TryGetValue("Encrypted", out enc);
                attr = attr ?? "";
                count++;
                SafeRel(path);
                // (links: a POSIX "l" mode or the Windows reparse-point flag in 7-Zip's attribute text)
                if (Regex.IsMatch(attr, @"(^|\s)l[r-][w-]") || attr.Contains("L")) throw new Exception("the archive holds a link (" + path + ") - refused");
                if (enc == "+") throw new Exception("the archive is password-protected - refused");
                long n; if (!string.IsNullOrEmpty(size)) { if (!long.TryParse(size, out n) || n < 0) throw new Exception("7-Zip's listing of the archive looks wrong - refused"); total += n; }
                entry.Clear();
            }
            if (count > ENTRIES_MAX) throw new Exception("the archive holds too many files (" + count + ")");
            if (total > UNPACKED_MAX) throw new Exception("the archive unpacks to more than " + (UNPACKED_MAX >> 30) + " GB - refused");
        }
        // 7zr with stdin closed (never waits for input), both outputs read at once (no pipe deadlock), a time limit (killed after it)
        static int Run7z(string args, int ms, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo(SevenZr, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
              StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();
                var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } throw new Exception("7-Zip took too long - stopped"); }
                p.WaitForExit();
                stdout = o.Result; stderr = e.Result;
                return p.ExitCode;
            }
        }
        // after unpacking: the real files against the caps again (the listing is 7-Zip's claim; the archive could also change in between)
        static void CheckUnpacked(string d)
        {
            long total = 0; int count = 0;
            foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
            {
                if (++count > ENTRIES_MAX) throw new Exception("the archive holds too many files");
                total += new FileInfo(f).Length;
                if (total > UNPACKED_MAX) throw new Exception("the archive unpacks to more than " + (UNPACKED_MAX >> 30) + " GB - refused");
            }
        }
        // Mark of the Web (the Zone.Identifier stream Windows puts on downloads): carried from the archive to each file kept from it
        // (CreateFileW: declared in LauncherCommon.cs)
        static byte[] ReadZone(string file)
        {
            try
            {
                using (var h = CreateFileW(file + ":Zone.Identifier", 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    using (var fs = new FileStream(h, FileAccess.Read))
                    {
                        if (fs.Length <= 0 || fs.Length > 8192) return null;
                        var b = new byte[fs.Length]; int got = 0, n;
                        while (got < b.Length && (n = fs.Read(b, got, b.Length - got)) > 0) got += n;
                        return b;
                    }
                }
            }
            catch { return null; }
        }
        static void WriteZone(string file, byte[] zone)
        {
            if (zone == null) return;
            try
            {
                using (var h = CreateFileW(file + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero))
                    if (!h.IsInvalid) using (var fs = new FileStream(h, FileAccess.Write)) fs.Write(zone, 0, zone.Length);
            }
            catch { }
        }

        void Unpack7z(string archive, string dest, bool yes)
        {
            if (!File.Exists(SevenZr) || Sha256(SevenZr) != SEVENZR_SHA)
            {
                bool go = yes;
                if (!Headless && !yes)
                    Invoke((Action)(() => go = MessageBox.Show(this, "This mod is a .7z archive. Unpacking it needs 7-Zip's small console tool 7zr.exe (600 KB) " +
                        "from its official site, www.7-zip.org.\n\nDownload it now?", "Cosmetic mods", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
                if (!go) throw new Exception(".7z needs 7-Zip's 7zr.exe - not downloaded");
                Say("downloading 7zr.exe from " + SEVENZR_URL);
                Fetch(SEVENZR_URL, SevenZr + ".new", SEVENZR_SIZE, false);
                if (Sha256(SevenZr + ".new") != SEVENZR_SHA) { File.Delete(SevenZr + ".new"); throw new Exception("7zr.exe from 7-zip.org is not the pinned file (checksum) - not used"); }
                if (File.Exists(SevenZr)) File.Delete(SevenZr);
                File.Move(SevenZr + ".new", SevenZr);
            }
            Check7zListing(archive);
            string outp, err;
            if (Run7z("x -y -bd -spd -sccUTF-8 \"-o" + dest + "\" -- \"" + archive + "\"", 600000, out outp, out err) != 0)
                throw new Exception("could not unpack the archive (7-Zip: " + err.Trim() + ")");
        }

        // ---------------------------------------------------------------------------------------------------- on / off / remove
        void CosEnable(string id, bool on) { lock (cosLock) CosEnableLocked(id, on); }
        // (review S5) every file the launcher writes is in installed.json before the record says ON; a failure while switching ON takes back
        // what this call copied (the player's settings file back where one was backed up) - what cannot be taken back stays recorded and
        // the mod reads ON (OFF cleans it up); a failure while switching OFF records what was removed and the mod stays ON
        void CosEnableLocked(string id, bool on)
        {
            var m = CosMod(id);
            if (Yes(m, "enabled") == on) return;
            string paksDir = ModsPaks(), cfgDir = ModsConfig(), files = Path.Combine(CosDir, id, "files");
            if (paksDir == "" || !Directory.Exists(paksDir)) throw new Exception("Foxhole was not found (its War\\Content\\Paks folder)");
            var inst = CosInstalled();
            var done = new List<string>();
            if (on)
            {
                // (refused before anything is copied: a same-named file the launcher did not put there, or a settings file another ON mod owns)
                foreach (var rel in StrList(m, "paks"))
                {
                    string t = Path.Combine(paksDir, Path.GetFileName(rel));
                    if (string.Equals(Path.GetFileName(rel), OUR_MOD_PAK, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(rel).Equals("War-WindowsNoEditor.pak", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("its pak has the name of a game / server pak (" + Path.GetFileName(rel) + ") - not installed");
                    if (File.Exists(t) && !inst.ContainsKey(t.ToLowerInvariant())) throw new Exception("a file called " + Path.GetFileName(rel) + " is already in the game's Paks folder (not added by the launcher)");
                }
                foreach (var other in CosMods())
                    if (Str(other, "id") != id && Yes(other, "enabled"))
                        foreach (var c in StrList(m, "configs"))
                            foreach (var oc in StrList(other, "configs"))
                                if (string.Equals(Path.GetFileName(c), Path.GetFileName(oc), StringComparison.OrdinalIgnoreCase))
                                    throw new Exception(Str(other, "name") + " already changes your " + Path.GetFileName(c) + " - turn it off first");
                var wrote = new List<string>(); var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var rel in StrList(m, "paks"))
                    {
                        string t = Path.Combine(paksDir, Path.GetFileName(rel));
                        wrote.Add(t); if (!File.Exists(t)) fresh.Add(t);
                        CopyPlain(Path.Combine(files, rel), t);
                        inst[t.ToLowerInvariant()] = Sha256(t); done.Add(Path.GetFileName(rel));
                    }
                    foreach (var rel in StrList(m, "configs"))
                    {
                        string t = Path.Combine(cfgDir, Path.GetFileName(rel)), bk = Path.Combine(CosDir, id, "backup", Path.GetFileName(rel));
                        Directory.CreateDirectory(cfgDir);
                        // (review S1) an existing backup is the player's OWN file - never overwritten (a settings file changed while ON stays
                        //  in place on OFF; switching ON again must not back THAT up over the original)
                        if (File.Exists(t) && !inst.ContainsKey(t.ToLowerInvariant()) && !File.Exists(bk))
                        { Directory.CreateDirectory(Path.GetDirectoryName(bk)); CopyPlain(t, bk); }
                        wrote.Add(t); if (!File.Exists(t)) fresh.Add(t);
                        CopyPlain(Path.Combine(files, rel), t);
                        inst[t.ToLowerInvariant()] = Sha256(t); done.Add(Path.GetFileName(rel));
                    }
                }
                catch (Exception ex)
                {
                    bool left = false;
                    foreach (var t in wrote)
                    {
                        string k = t.ToLowerInvariant();
                        try
                        {
                            // (only a file this call wrote: recorded with its hash, or one that was not there before - a copy cut short)
                            if (File.Exists(t) && ((inst.ContainsKey(k) && Sha256(t) == Str(inst, k)) || fresh.Contains(t))) DeletePlain(t);
                            inst.Remove(k);
                            string bk = Path.Combine(CosDir, id, "backup", Path.GetFileName(t));
                            if (!File.Exists(t) && File.Exists(bk)) { CopyPlain(bk, t); DeletePlain(bk); }   // (the player's own file back)
                        }
                        catch { left = true; try { if (File.Exists(t)) inst[k] = Sha256(t); } catch { } }
                    }
                    WriteJson(CosInstalledPath, inst);
                    if (left) { m["enabled"] = true; SaveMod(m); CosCheckAll(); throw new Exception(ex.Message + " - partly ON; switch it OFF to clean up"); }
                    throw new Exception(ex.Message + " - it stays OFF");
                }
            }
            else
            {
                try
                {
                    foreach (var rel in StrList(m, "paks"))
                    {
                        string t = Path.Combine(paksDir, Path.GetFileName(rel)), k = t.ToLowerInvariant();
                        if (File.Exists(t) && inst.ContainsKey(k) && Sha256(t) == Str(inst, k)) DeletePlain(t);
                        else if (File.Exists(t)) Say("kept " + t + " - it changed since the launcher put it there");
                        inst.Remove(k); done.Add(Path.GetFileName(rel));
                    }
                    foreach (var rel in StrList(m, "configs"))
                    {
                        string t = Path.Combine(cfgDir, Path.GetFileName(rel)), k = t.ToLowerInvariant(), bk = Path.Combine(CosDir, id, "backup", Path.GetFileName(rel));
                        if (File.Exists(t) && inst.ContainsKey(k) && Sha256(t) == Str(inst, k))
                        {
                            DeletePlain(t);
                            if (File.Exists(bk)) { CopyPlain(bk, t); DeletePlain(bk); }              // (the player's own file back)
                        }
                        else if (File.Exists(t)) Say("kept your " + Path.GetFileName(rel) + " - it changed since the mod was turned on");
                        inst.Remove(k); done.Add(Path.GetFileName(rel));
                    }
                }
                catch (Exception ex)
                {
                    WriteJson(CosInstalledPath, inst);                       // (what was removed is no longer recorded; the rest still is)
                    throw new Exception(ex.Message + " - could not switch it all OFF, it stays ON (close Foxhole and try again)");
                }
            }
            WriteJson(CosInstalledPath, inst);
            m["enabled"] = on; SaveMod(m);
            CosCheckAll();
            Say("cosmetic mod " + Str(m, "name") + (on ? " ON: " : " OFF: ") + string.Join(", ", done.ToArray()));
        }

        void CosRemove(string id) { lock (cosLock) CosRemoveLocked(id); }
        void CosRemoveLocked(string id)
        {
            var m = CosMod(id);
            if (Yes(m, "enabled")) CosEnable(id, false);
            DeleteTree(Path.Combine(CosDir, id));
            CosCheckAll();
            Say("cosmetic mod removed: " + Str(m, "name"));
        }

        static string FStr(byte[] b, ref int q)
        {
            int n = BitConverter.ToInt32(b, q); q += 4;
            if (n < 0) { string s = Encoding.Unicode.GetString(b, q, -2 * n); q += -2 * n; return s.TrimEnd('\0'); }
            string a = Encoding.GetEncoding(28591).GetString(b, q, n); q += n; return a.TrimEnd('\0');
        }
        // (mount point, entry paths, version); throws when it is not a readable pak (or an encrypted one)
        internal static List<string> PakPaths(string path, out string mount, out int version)   // (also LauncherSandbox.cs)
        {
            using (var f = File.OpenRead(path))
            {
                long n = f.Length;
                int tl = (int)Math.Min(512, n);
                var tail = new byte[tl]; f.Seek(n - tl, SeekOrigin.Begin); f.Read(tail, 0, tl);
                int m = -1;
                for (int i = tl - 4; i >= 0; i--) if (BitConverter.ToUInt32(tail, i) == 0x5A6F12E1) { m = i; break; }
                if (m < 0 || m + 24 > tl) throw new Exception("not a pak file");
                version = BitConverter.ToInt32(tail, m + 4);
                long ioff = BitConverter.ToInt64(tail, m + 8), isize = BitConverter.ToInt64(tail, m + 16);
                if (m >= 1 && tail[m - 1] != 0) throw new Exception("its index is encrypted");
                if (ioff < 0 || isize <= 0 || ioff + isize > n || isize > 1L << 30) throw new Exception("its index is damaged");
                var ix = new byte[isize]; f.Seek(ioff, SeekOrigin.Begin);
                int got = 0; while (got < isize) { int r = f.Read(ix, got, (int)isize - got); if (r <= 0) break; got += r; }
                int q = 0;
                mount = FStr(ix, ref q);
                int count = BitConverter.ToInt32(ix, q); q += 4;
                var paths = new List<string>(count);
                for (int k = 0; k < count; k++)
                {
                    paths.Add(FStr(ix, ref q));
                    q += 24;                                              // offset, size, uncompressed size
                    uint method = BitConverter.ToUInt32(ix, q); q += 4;
                    if (version == 1) q += 8;                              // (timestamp, version 1 only)
                    q += 20;                                              // sha1
                    if (version >= 3)
                    {
                        if (method != 0) { int nb = BitConverter.ToInt32(ix, q); q += 4 + 16 * nb; }
                        q += 5;                                           // flags, compression block size
                    }
                }
                return paths;
            }
        }
        // War/Content-relative ("Textures/X.uasset") from a pak path + mount ("../../../" + "War/Content/Textures/X.uasset")
        static string ContentRel(string mount, string p)
        {
            string full = (mount.Replace('\\', '/') + p).Replace("../", "");
            int i = full.IndexOf("War/Content/", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? full.Substring(i + 12) : "";
        }
        HashSet<string> BaseIndex()
        {
            string pak = modsBasePakOverride != "" ? modsBasePakOverride : Path.Combine(ModsPaks(), "War-WindowsNoEditor.pak"), cache = Path.Combine(ModsRoot, "base_index.txt");
            if (!File.Exists(pak)) return null;
            var fi = new FileInfo(pak);
            string key = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(cache))
            {
                var lines = File.ReadAllLines(cache);
                if (lines.Length > 0 && lines[0] == key) { for (int i = 1; i < lines.Length; i++) set.Add(lines[i]); return set; }
            }
            string mount; int v;
            foreach (var p in PakPaths(pak, out mount, out v)) { string r = ContentRel(mount, p); if (r != "") set.Add(r); }
            var outl = new List<string> { key }; outl.AddRange(set);
            Directory.CreateDirectory(ModsRoot);
            File.WriteAllLines(cache, outl.ToArray());
            return set;
        }

        // ---------------------------------------------------------------------------------------------------- compatibility check
        // every mod's verdict + notes (OK / WARNING / OUR SERVERS ONLY (public: VERDICT_PARTLY) / WILL NOT LOAD); conflicts need all of them at once
        void CosCheckAll() { lock (cosLock) CosCheckAllLocked(); }
        void CosCheckAllLocked()
        {
            var mods = CosMods();
            HashSet<string> baseSet = null, ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { baseSet = BaseIndex(); } catch (Exception ex) { Say("cosmetic mods: the game's pak could not be read (" + ex.Message + ")"); }
            try
            {
                string op = Path.Combine(ModsPaks(), OUR_MOD_PAK);
                if (File.Exists(op)) { string mo; int vo; foreach (var p in PakPaths(op, out mo, out vo)) ours.Add(ContentRel(mo, p)); }
            }
            catch { }
            var filesOf = new Dictionary<string, HashSet<string>>();
            var readErr = new Dictionary<string, string>();
            foreach (var m in mods)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in StrList(m, "paks"))
                {
                    try { string mo; int v; foreach (var p in PakPaths(Path.Combine(CosDir, Str(m, "id"), "files", rel), out mo, out v)) { string r = ContentRel(mo, p); if (r != "") set.Add(r); } }
                    catch (Exception ex) { readErr[Str(m, "id")] = Path.GetFileName(rel) + ": " + ex.Message; }
                }
                filesOf[Str(m, "id")] = set;
            }
            foreach (var m in mods)
            {
                string id = Str(m, "id");
                var notes = new List<string>();
                string verdict = "OK";
                if (readErr.ContainsKey(id)) { verdict = "WILL NOT LOAD"; notes.Add("not a readable pak - " + readErr[id]); }
                var set = filesOf[id];
                if (baseSet != null && set.Count > 0)
                {
                    int replaced = 0, unofficial = 0, unknown = 0; string firstUnofficial = "", firstUnknown = "";
                    foreach (var r in set)
                    {
                        if (baseSet.Contains(r))
                        {
                            replaced++;
                            if (!Array.Exists(OfficialFolders, f => r.StartsWith(f, StringComparison.OrdinalIgnoreCase))) { unofficial++; if (firstUnofficial == "") firstUnofficial = r; }
                        }
                        else if (r.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) { unknown++; if (firstUnknown == "") firstUnknown = r; }
                    }
                    notes.Add("replaces " + replaced + " game file(s)");
                    if (unofficial > 0)
                    {
                        if (verdict == "OK") verdict = VERDICT_EDITION;   // (official play ignores those files; the private servers lift that filter)
                        notes.Add(unofficial + " of them outside the folders official Foxhole accepts from mods (e.g. " + firstUnofficial + ") - official Foxhole ignores those");
                    }
                    if (unknown > 0)
                    {
                        if (verdict == "OK") verdict = "WARNING";
                        notes.Add(unknown + " file(s) this game version does not have (e.g. " + firstUnknown + ") - made for another version?");
                    }
                }
                int clash = 0; foreach (var r in set) if (ours.Contains(r)) clash++;
                if (clash > 0) { if (verdict == "OK") verdict = "WARNING"; notes.Add(clash + " file(s) the server's own mods also change - one of the two wins"); }
                if (Yes(m, "enabled"))
                    foreach (var o in mods)
                    {
                        if (Str(o, "id") == id || !Yes(o, "enabled")) continue;
                        int same = 0; foreach (var r in set) if (filesOf[Str(o, "id")].Contains(r)) same++;
                        if (same > 0) { if (verdict == "OK") verdict = "WARNING"; notes.Add("conflict: " + same + " file(s) also changed by " + Str(o, "name") + " - only one of them shows"); }
                    }
                foreach (var c in StrList(m, "configs")) notes.Add("settings: replaces your " + Path.GetFileName(c) + " while ON (yours is kept and put back on OFF)");
                if (StrList(m, "ignored").Count > 0) notes.Add("left out (not game files): " + string.Join(", ", StrList(m, "ignored").ToArray()));
                m["verdict"] = verdict; m["notes"] = notes;
                SaveMod(m);
            }
        }

        // ---------------------------------------------------------------------------------------------------- Nexus Mods (browser route)
        void ModsBrowseNexus()
        {
            try { Process.Start(NEXUS_URL); } catch (Exception ex) { Say("could not open the browser: " + ex.Message); return; }
            nexusBrowsedAt = DateTime.UtcNow;
            ModsWatchDownloads();
            modsLine = (PublicEdition ? "The in-app browser comes once Nexus Mods approves this app - opened Nexus in your browser. " : "") +
                "Download a mod on Nexus Mods - it is offered here when the download finishes (or use ADD FILE).";
            Redraw();
        }
        // while the launcher runs after BROWSE NEXUS: a finished .7z / .zip / .pak in the Downloads folder is offered once
        void ModsWatchDownloads()
        {
            if (modsWatch != null) return;
            string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dl)) return;
            modsWatch = new FileSystemWatcher(dl) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler seen = (s, e) => ModsOfferDownload(e.FullPath);
            modsWatch.Created += seen; modsWatch.Changed += seen;
            modsWatch.Renamed += (s, e) => ModsOfferDownload(e.FullPath);
            modsWatch.EnableRaisingEvents = true;
        }
        void ModsOfferDownload(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".7z" && ext != ".zip" && ext != ".pak") return;
            var nf = ParseNexus(Path.GetFileNameWithoutExtension(path));
            if (nf == null) return;
            int nid = nf.ModId;
            bool asked;
            lock (nexusAsked) { DateTime t; asked = nexusAsked.TryGetValue(nid, out t) && DateTime.UtcNow - t < OFFER_WINDOW; }
            if (!asked && DateTime.UtcNow - nexusBrowsedAt > OFFER_WINDOW) return;
            lock (modsOffered) { if (modsOffered.Contains(path)) return; modsOffered.Add(path); }
            new Thread(() =>
            {
                long last = -1; bool done = false;                        // (finished = the size stops changing and the file opens)
                for (int i = 0; i < 120; i++)
                {
                    Thread.Sleep(1000);
                    try { long n = new FileInfo(path).Length; using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } if (n == last && n > 0) { done = true; break; } last = n; }
                    catch { last = -1; }
                }
                if (!done) return;
                OfferDecision dec;
                try { dec = ModUpdateRules.Decide(nf, InstalledNexusFiles(), ListedOrUnknown, ModUpdateRules.UnixNow()); }
                catch (Exception ex) { Say("cosmetic mods: " + ex.Message); return; }
                if (dec.Kind == "ignore") return;                         // (the installed file itself)
                if (dec.Kind == "update")
                {
                    Dictionary<string, object> inst;
                    try { inst = CosMod(dec.TargetId); } catch { return; }
                    string iid = dec.TargetId;
                    bool upd = false, free = false;
                    try
                    {
                        Invoke((Action)(() =>
                        {
                            if (!dec.Older) NoteSeenDownload(iid, nf, path);
                            // (review S3: the same guard as the row buttons / drag and drop - declined with a line; the badge stays, so Update on
                            //  the row takes the file later)
                            if (!ModsFreeOrSay()) return;
                            upd = AskUpdate(inst, nf.Version, path, dec.Older);
                            free = upd && ModsFreeOrSay();
                        }));
                    }
                    catch { return; }
                    if (!dec.Older) SaveSeen();
                    if (upd && free) ModsRun(() => CosUpdate(iid, path, false));
                    return;
                }
                bool add = false;
                try
                {
                    Invoke((Action)(() =>
                    {
                        if (!ModsFreeOrSay()) return;
                        add = MessageBox.Show(this, (asked ? "Add this download from Nexus Mods (mod " + nid + ") as a cosmetic mod?" :
                            "Add this download as a cosmetic mod?") + "\n\n" + Path.GetFileName(path) +
                            "\n\nIt is checked first; you switch it on in Installed.", "Cosmetic mods", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes &&
                            ModsFreeOrSay();
                    }));
                }
                catch { return; }                                         // (the window closed meanwhile)
                if (add) ModsRun(() => CosAdd(path, false));
            }) { IsBackground = true }.Start();
        }

        // ---------------------------------------------------------------------------------------------------- UI
        void ModsRun(Action a)
        {
            new Thread(() =>
            {
                try { a(); modsLine = ""; }
                catch (Exception ex) { modsLine = ex.Message; Say("cosmetic mods: " + ex.Message); if (browseOpen) browseNote = ex.Message; }
                Ui(() => { CountInstalled(); if (browseOpen) BrowseRefreshInstalled(); Redraw(); });
            }) { IsBackground = true }.Start();
        }

        void ModsClick(string h)
        {
            if (h == "add")
            {
                using (var d = new OpenFileDialog { Title = "Add a cosmetic mod", Filter = "Foxhole mods (*.pak;*.zip;*.7z)|*.pak;*.zip;*.7z",
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                    if (d.ShowDialog(this) == DialogResult.OK && ModsFreeOrSay()) { string f = d.FileName; ModsRun(() => CosAdd(f, false)); }
            }
            else if (h == "nexus") { if (BrowseAvailable()) Go("browse"); else ModsBrowseNexus(); }                     // (the in-app browser - LauncherBrowse.cs; Nexus in the browser where it is off)
            else if (h == "prev") { modsPage = Math.Max(0, modsPage - 1); Redraw(); }
            else if (h == "next") { modsPage++; Redraw(); }
            else if (h == "checkupdates") ModUpdatesCheck(true);                                                       // (LauncherModUpdates.cs)
            else if (h.StartsWith("update:")) ModsUpdateClick(h.Substring(7));
            else if (h.StartsWith("rollback:")) ModsRollbackClick(h.Substring(9));
            else if (h.StartsWith("on:")) { string id = h.Substring(3); var m = CosMod(id); bool to = !Yes(m, "enabled"); if (ModsFreeOrSay()) ModsRun(() => CosEnable(id, to)); }
            else if (h.StartsWith("rm:"))
            {
                string id = h.Substring(3); var m = CosMod(id);
                if (MessageBox.Show(this, "Remove " + Str(m, "name") + "?\nIts files leave the game and the launcher.", "Cosmetic mods", MessageBoxButtons.OKCancel) == DialogResult.OK && ModsFreeOrSay())
                    ModsRun(() => CosRemove(id));
            }
            else if (h.StartsWith("info:"))
            {
                var m = CosMod(h.Substring(5));
                MessageBox.Show(this, Str(m, "name") + "  -  " + Str(m, "verdict") + "\n\n" + string.Join("\n", StrList(m, "notes").ToArray()) +
                    "\n\nFile: " + Str(m, "source") + (Str(m, "nexus") != "" ? "\nNexus Mods page: " + Str(m, "nexus") : ""), "Cosmetic mod");
            }
        }

        static Color VerdictColor(string v)
        {
            if (v == "OK") return Color.FromArgb(150, 200, 120);
            if (v == "WILL NOT LOAD") return Color.FromArgb(235, 110, 90);
            return Color.FromArgb(235, 190, 100);
        }

        void SmallButton(Graphics g, string id, string text, Rectangle r, bool enabled)
        {
            if (enabled) hits[id] = r; else hits.Remove(id);
            bool hov = enabled && hover == id;
            using (var b = new SolidBrush(hov ? Color.FromArgb(200, 92, 110, 62) : Color.FromArgb(160, 40, 40, 38))) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(90, 226, 221, 206))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            Txt(g, text, 12, true, !enabled ? InkOff : hov ? Color.White : Ink, r, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 1, false);
        }


        // ---------------------------------------------------------------------------------------------------- command line
        static bool ModsCommandLine(string[] args)
        {
            var a = new List<string>(args);
            string[] cmds = { "--mods-list", "--mods-add", "--mods-on", "--mods-off", "--mods-remove", "--mods-check", "--mods-update", "--mods-rollback" };
            if (!a.Exists(x => Array.IndexOf(cmds, x) >= 0)) return false;
            AttachConsole(-1);
            Headless = true;
            Func<string, string> opt = k => { int i = a.IndexOf(k); return i >= 0 && i + 1 < a.Count ? a[i + 1] : ""; };
            var l = new Launcher();
            l.modsGameOverride = opt("--mods-game"); l.modsConfigOverride = opt("--mods-config"); l.modsBasePakOverride = opt("--mods-basepak");
            Environment.ExitCode = 0;
            try
            {
                if (a.Contains("--mods-add")) Console.WriteLine("added " + l.CosAdd(opt("--mods-add"), a.Contains("--yes")));
                if (a.Contains("--mods-on")) l.CosEnable(opt("--mods-on"), true);
                if (a.Contains("--mods-off")) l.CosEnable(opt("--mods-off"), false);
                if (a.Contains("--mods-remove")) l.CosRemove(opt("--mods-remove"));
                if (a.Contains("--mods-update")) { int i = a.IndexOf("--mods-update"); if (i + 2 >= a.Count) throw new Exception("--mods-update <id> <file>"); l.CosUpdate(a[i + 1], a[i + 2], a.Contains("--yes")); }
                if (a.Contains("--mods-rollback")) l.CosRollback(opt("--mods-rollback"));
                if (a.Contains("--mods-check")) l.CosCheckAll();
                if (a.Contains("--mods-list") || a.Contains("--mods-check"))
                    foreach (var m in CosMods())
                    {
                        Console.WriteLine(Str(m, "id") + "  " + (Yes(m, "enabled") ? "ON " : "OFF") + "  " + Str(m, "verdict") + "  " + Str(m, "name") +
                            (ModVersionText(m) != "" ? "  v" + ModVersionText(m) : "") + (m.ContainsKey("previous") ? "  [previous kept]" : ""));
                        foreach (var n in StrList(m, "notes")) Console.WriteLine("      " + n);
                    }
            }
            catch (Exception ex) { l.Say("ERROR: " + ex.Message); Environment.ExitCode = 1; }
            return true;
        }
    }
}
