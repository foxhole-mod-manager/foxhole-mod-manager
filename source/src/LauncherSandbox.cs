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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FoxholeModLauncher
{
    class SandboxFail : Exception { public SandboxFail(string m) : base(m) { } }

    partial class Launcher
    {
        internal static string AppDataPath { get { return DataDir; } }   // (the app's data folder - Launcher.cs; tests: FOX_LAUNCHER_DATA_DIR)
        // (tests, no window) - see the file header
        static bool SandboxCommandLine(string[] args)
        {
            if (args.Length < 2 || !args[0].StartsWith("--sandbox-")) return false;
            AttachConsole(-1);
            Headless = true;
            Sandbox.Log = s => Console.WriteLine(s);
            Environment.ExitCode = 0;
            try
            {
                if (args[0] == "--sandbox-revert") { string note = Sandbox.Revert(args[1]); if (note != "") Console.WriteLine("NOTE: " + note); }
                // (tests, review A) what the app's start does with a session record left in its data folder: "marker: none" / "marker: kept" (a
                // sandbox's, the restore follows) / "marker: dropped (server kit)" (exit 4 - the record goes, the game is not touched)
                else if (args[0] == "--sandbox-marker")
                {
                    string mk = Path.Combine(DataDir, "offline_session.json");
                    if (!File.Exists(mk)) Console.WriteLine("marker: none");
                    else if (Sandbox.KitNotOurs(args[1])) { File.Delete(mk); Console.WriteLine("marker: dropped (server kit)"); Environment.ExitCode = 4; }
                    else Console.WriteLine("marker: kept");
                }
                // (tests) the Live War guard and the restore's no-state-folder check: prints the reason or "none"; exit 3 = blocked / changed
                else if (args[0] == "--sandbox-livecheck") { string w = Sandbox.LiveWarBlock(args[1]); Console.WriteLine("live war: " + (w ?? "none")); if (w != null) Environment.ExitCode = 3; }
                else if (args[0] == "--sandbox-changed") { string w = Sandbox.ChangedWithoutState(args[1]); Console.WriteLine("changed: " + (w ?? "none")); if (w != null) Environment.ExitCode = 3; }
                else if (args[0] == "--sandbox-leftovers") Console.WriteLine("leftovers: " + Sandbox.HasLeftovers(args[1]) + " | " + Sandbox.LeftoverText(args[1]));
                // (tests) what the sidebar shows: "trace: none" / "trace: restore | <why>" (exit 3) / "trace: kit | <why>" (exit 4)
                else if (args[0] == "--sandbox-trace")
                {
                    bool restorable; string w = Sandbox.Trace(args[1], File.Exists(Path.Combine(DataDir, "offline_session.json")), out restorable);
                    Console.WriteLine("trace: " + (w == null ? "none" : (restorable ? "restore | " : "kit | ") + w));
                    if (w != null) Environment.ExitCode = restorable ? 3 : 4;
                }
                else { Console.WriteLine("unknown sandbox command"); Environment.ExitCode = 2; }
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Environment.ExitCode = 1; }
            return true;
        }
    }

    class SandboxPaths
    {
        public string Game, Win64, Exe, Pak, State, Backup, Server, Remote, Info;
        public SandboxPaths(string game, string pakRel)
        {
            Game = Path.GetFullPath(game).TrimEnd('\\');
            Win64 = Path.Combine(Game, Sandbox.WIN64_REL);
            Exe = Path.Combine(Win64, Sandbox.EXE_NAME);
            Pak = Path.GetFullPath(Path.Combine(Game, pakRel.Replace('/', '\\')));
            if (!Pak.StartsWith(Game + "\\", StringComparison.OrdinalIgnoreCase)) throw new SandboxFail("bad game pak path");
            State = Path.Combine(Game, "_FoxholeOffline");
            Backup = Path.Combine(State, "backup");
            Server = Path.Combine(State, "server");
            Remote = Path.Combine(State, "remote");
            Info = Path.Combine(State, "install.json");
        }
    }

    static partial class Sandbox
    {
        public const string EXE_NAME = "War-Win64-Shipping.exe", WIN64_REL = "War\\Binaries\\Win64";
        static readonly string[] Win64Items = { "dwmapi.dll", "UE4SS.dll", "UE4SS-settings.ini", "steam_appid.txt", "Mods" };
        static readonly string[] Win64Generated = { "UE4SS.log", "CXXHeaderDump", "UHTHeaderDump", "UE4SS_ObjectDump.txt", "FoxClient.dll", "FoxClient.log",
            "camo", "camo.txt", "camo_cmd.txt", "fc_commands.txt", "fc_console_in.txt", "fc_debug.txt", "voice.txt",
            "voice_dump.wav", "voice_mutes.txt", "server_password.txt", "admin_key.txt", "live_rank.txt", "autojoin_skip.txt", "addresses-*.txt", "netwatch.txt",
        };
        public static Action<string> Log = s => { };                       // (the launcher's Say)

        // (restore only - no kit is shipped) the fingerprints the 1.0.0 sandbox kit's patch.json held, plus the exe it patched on the game
        // build that was live at its release (26058282); every exe a 1.0.0 app patched is also in the game's install.json and in the app's own record
        // (sandbox_exe_hashes.json in its data folder)
        static readonly Dictionary<string, object> Patch = new Dictionary<string, object> {
            { "exe", new Dictionary<string, object> {
                { "vanilla_sha256", "ED99E371A3742AF48BEE4B19D9E0CE4E58595F556523E39B8CEDC98B62E90ABF" },
                { "patched_sha256", "E05AF76120BA854EED939D00B1504BB0A114645815A2C7434623777F73EBAC6C" },
                { "older_patched_sha256", new object[] { "B2433656A044665C5FCF1BD22952FE7AFA21B8463BA9BD6EDB9217F5C50E0AB8",
                                                         "2277BA520E0495E6D1D18285A5190E92263D8B3501D94677431890B688B80925" } },
                { "known_vanilla_sha256", new object[] { "6CC174C5BC26242E4AD191871E35A67D9BAAD603E5AB88512F8701AA6CAE81E3" } },
                { "known_patched_sha256", new object[] { "2D3DD7F4EB37C5E461D490A5B2964E8AF86720999F8BE9898E2C497A376A2232" } } } },
            { "pak", new Dictionary<string, object> { { "path", "War/Content/Paks/War-WindowsNoEditor.pak" } } } };
        static Dictionary<string, object> PatchPart(string k) { return Patch[k] as Dictionary<string, object>; }
        static string PatchStr(string part, string k) { var d = PatchPart(part); return d != null && d.ContainsKey(k) && d[k] != null ? d[k].ToString() : ""; }
        static List<string> PatchList(string part, string k)
        {
            var r = new List<string>(); var d = PatchPart(part);
            if (d == null || !d.ContainsKey(k) || d[k] == null) return r;
            var arr = d[k] as object[];
            if (arr != null) foreach (var x in arr) r.Add(x.ToString()); else r.Add(d[k].ToString());
            return r;
        }
        public static SandboxPaths PathsOf(string game) { return new SandboxPaths(game, PatchStr("pak", "path")); }

        // ---------------------------------------------------------------------------------------------------- small helpers
        static string Sha256File(string path)
        {
            using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }
        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        static byte[] ReadRegion(string path, long offset, int size)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[size]; fs.Position = offset; int n = 0, r;
                while (n < size && (r = fs.Read(buf, n, size - n)) > 0) n += r;
                return buf;
            }
        }
        static void WriteRegion(string path, long offset, byte[] data)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { fs.Position = offset; fs.Write(data, 0, data.Length); fs.Flush(true); }
        }
        // (review C5) a link (file or folder, also a dangling one) is removed AS the link - its attributes are never set (that could reach the
        // target) and its target is never entered
        static void DeleteAny(string p)
        {
            if (!EntryExists(p)) return;
            var at = File.GetAttributes(p);                                // (reports the link itself, not its target)
            if ((at & FileAttributes.ReparsePoint) != 0) { if ((at & FileAttributes.Directory) != 0) Directory.Delete(p, false); else File.Delete(p); return; }
            if ((at & FileAttributes.Directory) != 0) { DeleteTree(p); return; }
            DeletePlainFile(p, at);
        }
        static void DeletePlainFile(string f, FileAttributes at)
        {
            if ((at & FileAttributes.ReparsePoint) == 0 && (at & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0) File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        static void DeleteTree(string d)                                  // (never follows a link out of the folder)
        {
            if (!EntryExists(d)) return;
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(d, false); return; }
            foreach (var f in Directory.GetFiles(d)) DeletePlainFile(f, File.GetAttributes(f));
            foreach (var s in Directory.GetDirectories(d)) DeleteTree(s);
            Directory.Delete(d, false);
        }
        // a Win64 item name, or a pattern with * (addresses-*.txt) -> the matching paths
        static IEnumerable<string> Matches(string dir, string item)
        {
            if (!Directory.Exists(dir)) yield break;
            if (item.Contains("*")) { foreach (var e in Directory.GetFileSystemEntries(dir, item)) yield return e; yield break; }
            string p = Path.Combine(dir, item);
            if (File.Exists(p) || Directory.Exists(p)) yield return p;
        }
        // anything at that path - a file, a folder, a link (also a dangling one) - WITHOUT following a link (GetFileAttributesW reports the
        // link itself)
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string path);
        static bool EntryExists(string path) { return GetFileAttributesW(path) != 0xFFFFFFFF; }
        static bool IsDir(string path) { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        // every folder from root (exclusive) down to path's parent is a plain folder or not there yet - no link, no file in the way (a move
        // or a CreateDirectory through a planted link would land somewhere else)
        static bool PlainParents(string root, string path)
        {
            if (!EntryExists(root) || IsLink(root) || !IsDir(root)) return false;
            string d = Path.GetDirectoryName(path);
            while (d != null && d.Length > root.Length)
            {
                if (EntryExists(d) && (IsLink(d) || !IsDir(d))) return false;
                d = Path.GetDirectoryName(d);
            }
            return d != null && Same(d.TrimEnd('\\'), root.TrimEnd('\\'));
        }
        static List<string> SafeFiles(string dir)
        {
            var r = new List<string>();
            if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return r;
            var todo = new Stack<string>(); todo.Push(dir);
            while (todo.Count > 0)
            {
                string d = todo.Pop();
                foreach (var f in Directory.GetFiles(d)) if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0) r.Add(f);
                foreach (var s in Directory.GetDirectories(d)) if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) todo.Push(s);
            }
            return r;
        }
        // (R3) written next to it, flushed, then swapped in: a power cut leaves the old or the new file, never half of one
        static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { fs.Write(data, 0, data.Length); fs.Flush(true); }
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        static void SaveRecord(SandboxPaths p, Dictionary<string, object> o) { WriteAtomic(p.Info, Encoding.ASCII.GetBytes(new JavaScriptSerializer().Serialize(o))); }

        // ---------------------------------------------------------------------------------------------------- state
        static Dictionary<string, object> Record(SandboxPaths p)
        {
            try { return File.Exists(p.Info) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(p.Info)) as Dictionary<string, object> : null; }
            catch { return null; }
        }
        static string RecStr(Dictionary<string, object> r, string k) { return r != null && r.ContainsKey(k) && r[k] != null ? r[k].ToString() : ""; }
        static List<string> RecList(Dictionary<string, object> r, string k)
        {
            var l = new List<string>();
            if (r == null || !r.ContainsKey(k) || r[k] == null) return l;
            var a = r[k] as object[]; if (a != null) foreach (var x in a) l.Add(x.ToString()); else if (r[k] is string) l.Add((string)r[k]);
            return l;
        }
        public static string ExeState(SandboxPaths p)
        {
            if (!File.Exists(p.Exe)) return "missing";
            string h = Sha256File(p.Exe);
            if (Same(h, PatchStr("exe", "vanilla_sha256"))) return "vanilla";
            foreach (var x in PatchedShas()) if (Same(h, x)) return "patched";
            var rec = Record(p);
            if (rec != null && Same(h, RecStr(rec, "ExePatchedSha"))) return "patched";
            if (KnownHashes("patched").Any(x => Same(x, h))) return "patched";       // (review B: the app's own record, kept outside the game)
            if (rec != null && Same(h, RecStr(rec, "ExeVanillaSha"))) return "vanilla-new";
            if (KnownHashes("vanilla").Any(x => Same(x, h))) return "vanilla-new";
            if (PatchList("exe", "known_vanilla_sha256").Any(x => Same(x, h))) return "vanilla-new";
            // (no exe patcher in this build: an exe nobody recorded - e.g. a Steam update - stays "unknown"; the restore leaves it alone)
            return "unknown";
        }
        // every patched-exe fingerprint the app knows from its data (not the game's own record)
        static List<string> PatchedShas()
        {
            return PatchList("exe", "patched_sha256").Concat(PatchList("exe", "older_patched_sha256")).Concat(PatchList("exe", "known_patched_sha256")).ToList();
        }
        public static string PakState(SandboxPaths p)
        {
            if (!File.Exists(p.Pak)) return "missing";
            try { var loc = FhoPakIni.Locate(p.Pak); return FhoPakIni.State(ReadRegion(p.Pak, loc[0], (int)loc[1])); } catch { return "unknown"; }
        }

        // (review B) the exe hashes this app produced / started from, kept in its OWN data folder (%LOCALAPPDATA%\FoxholeModManager) - they
        // survive a revert and a deleted _FoxholeOffline folder, so a patched exe is still recognised as patched when the game's own record
        // is gone. {"patched": [sha...], "vanilla": [sha...]}; written at install time BEFORE the patched exe is.
        static string AppDataDir { get { return Launcher.AppDataPath.TrimEnd('\\'); } }   // (%LOCALAPPDATA%\FoxholeModManager - Launcher.cs DataDir)
        static string KnownHashFile { get { return Path.Combine(AppDataDir, "sandbox_exe_hashes.json"); } }
        static Dictionary<string, object> ReadKnown()
        {
            try { return File.Exists(KnownHashFile) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(KnownHashFile)) as Dictionary<string, object> : null; }
            catch { return null; }
        }
        static List<string> KnownHashes(string kind)
        {
            return RecList(ReadKnown(), kind).Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, "^[0-9A-Fa-f]{64}$")).ToList();
        }
        // the exe's SHA-256, cached by path + size + last write time (the Live War button must not read 80 MB on every click)
        static readonly Dictionary<string, string> shaCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string CachedSha(string file)
        {
            var fi = new FileInfo(file);
            string key = Path.GetFullPath(file) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            lock (shaCache)
            {
                string h;
                if (shaCache.TryGetValue(key, out h)) return h;
                h = Sha256File(file);
                shaCache[key] = h;
                return h;
            }
        }
        static readonly string[] DefaultPakRel = { "War", "Content", "Paks", "War-WindowsNoEditor.pak" };
        // (review B) the LIVE WAR guard: a reason when the game is POSITIVELY changed - the pak's network settings read "patched", or the exe
        // is one of the known patched builds (patch.json + this app's own record) - else null. "Unknown" never blocks (a Steam update makes
        // every exe unknown to an old app). No kit / no patch.json: only the app's own record is used.
        public static string LiveWarBlock(string game)
        {
            string full = Path.GetFullPath(game).TrimEnd('\\');
            string exe = Path.Combine(full, WIN64_REL, EXE_NAME), pak = Path.Combine(new[] { full }.Concat(DefaultPakRel).ToArray());
            var patchedShas = KnownHashes("patched");
            try
            {
                var p = PathsOf(game);
                pak = p.Pak;
                patchedShas.AddRange(PatchedShas());
            }
            catch { }
            const string fix = " - press \"Restore the game\" first; if that does not help, run Steam's \"Verify integrity of game files\" for Foxhole.";
            try
            {
                if (File.Exists(pak))
                {
                    var loc = FhoPakIni.Locate(pak);
                    if (FhoPakIni.State(ReadRegion(pak, loc[0], (int)loc[1])) == "patched") return "Your Foxhole is still changed for the offline sandbox (the game pak's network settings)" + fix;
                }
            }
            catch { }
            try
            {
                if (File.Exists(exe) && patchedShas.Count > 0)
                {
                    string h = CachedSha(exe);
                    if (patchedShas.Any(x => Same(x, h))) return "Your Foxhole is still changed for the offline sandbox (the game exe is the patched one)" + fix;
                }
            }
            catch { }
            return null;
        }
        // (review A1) the game checked WITHOUT the sandbox's state folder: null = not changed (exe not patched - unknown only with an original pak -, pak not patched),
        // else what is changed. A check that fails counts as changed.
        public static string ChangedWithoutState(string game)
        {
            try
            {
                var p = PathsOf(game);
                var parts = new List<string>();
                string e = ExeState(p), k = PakState(p);
                if (e == "patched" || (e == "unknown" && k != "vanilla")) parts.Add("game exe '" + e + "'");
                if (k == "patched") parts.Add("game pak 'patched'");
                return parts.Count == 0 ? null : string.Join(", ", parts);
            }
            catch (Exception ex) { return "could not check the game (" + ex.Message + ")"; }
        }
        // the game (client or the sandbox's server) running from this folder - by exe path only, never by name alone
        public static List<Process> GameProcesses(string game)
        {
            string root = Path.GetFullPath(game).TrimEnd('\\') + "\\";
            var r = new List<Process>();
            foreach (var name in new[] { "War-Win64-Shipping", "CrashReportClient" })
                foreach (var pr in Process.GetProcessesByName(name))
                {
                    string img = ImagePath(pr.Id);
                    if (img != "" && Path.GetFullPath(img).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { r.Add(pr); continue; }
                    pr.Dispose();
                }
            return r;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder buf, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return "";
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : ""; }
            finally { CloseHandle(h); }
        }
        // can this user write the game folder (Program Files needs administrator rights)?
        public static bool CanWrite(SandboxPaths p)
        {
            try
            {
                string t = Path.Combine(p.Win64, ".fmm_write_test");
                File.WriteAllText(t, "x"); File.Delete(t);
                using (new FileStream(p.Pak, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                Directory.CreateDirectory(p.State);
                return true;
            }
            catch { return false; }
        }

        // the server copy's links removed; a read-only flag (shared with the game's own file through the link) is put back on the game's file.
        // Every file is tried (a locked one does not stop the others); IOException at the end when any is left.
        static void DeleteServerCopy(string server)
        {
            if (!EntryExists(server)) return;
            if (!IsDir(server)) { RemoveCopyFile(server); return; }                  // (a file - or a file link - of that name: only that entry)
            if (IsLink(server)) { Directory.Delete(server, false); return; }           // (a link: only the link)
            int failed = 0; string first = "";
            foreach (var f in SafeFiles(server))
            {
                try { RemoveCopyFile(f); }
                catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) { if (failed++ == 0) first = f.Substring(server.Length + 1) + ": " + ex.Message; } else throw; }
            }
            if (failed > 0) throw new IOException(failed + " file(s) of the local server copy could not be removed (" + first + ")");
            DeleteTree(server);
        }
        // one name of the server copy (a hard link to a game file, a link, or a plain file of ours) removed - never what a link points to
        static void RemoveCopyFile(string f)
        {
            var at = File.GetAttributes(f);
            if ((at & FileAttributes.ReparsePoint) != 0 || (at & FileAttributes.ReadOnly) == 0) { File.Delete(f); return; }
            var others = LinkPaths(f).Where(x => !Same(Path.GetFullPath(x), Path.GetFullPath(f))).ToList();
            File.SetAttributes(f, at & ~FileAttributes.ReadOnly);
            File.Delete(f);
            foreach (var o in others) try { File.SetAttributes(o, File.GetAttributes(o) | FileAttributes.ReadOnly); } catch { }
        }
        // (restore) what the app sees of a 1.0.0 sandbox in this game -> null = nothing (the game's own files, or nothing the app can tell);
        // else why, with restorable = whether Restore the game is the way back (false: a server kit of another app is set up in this game -
        // never touched here). marker = the app's offline_session.json is there (a session that did not end).
        public static string Trace(string game, bool marker, out bool restorable)
        {
            restorable = true;
            SandboxPaths p;
            try { p = PathsOf(game); } catch { restorable = false; return marker ? "an offline session of an earlier version did not end" : null; }
            if (KitNotOurs(game)) { restorable = false; return "a server kit is set up in this game - use its \"Back to official Foxhole\", or run Steam's \"Verify integrity of game files\" for Foxhole and delete that _FoxholeOffline folder"; }
            if (marker) return "an offline session of an earlier version did not end";
            if (File.Exists(p.Info))
            {
                if (IsOurs(p)) return "the offline sandbox's changes are still in the game";
                restorable = false;
                return "a server kit is set up in this game - use its \"Back to official Foxhole\", or run Steam's \"Verify integrity of game files\" for Foxhole and delete that _FoxholeOffline folder";
            }
            if (HasLeftovers(game)) return "files of the last offline sandbox wait to go back";
            string block = LiveWarBlock(game);
            if (block != null) return "the game files are still changed";
            return null;
        }
        public static bool KitNotOurs(string game)
        {
            try
            {
                var p = PathsOf(game);
                if (!File.Exists(p.Info) || IsOurs(p)) return false;
                var rec = Record(p);
                return !(RecStr(rec, "Status") == "installing" && CutOffSandboxSetup(p, rec));
            }
            catch { return false; }
        }
        static bool CutOffSandboxSetup(SandboxPaths p, Dictionary<string, object> rec)
        {
            try
            {
                string mk = Path.Combine(Launcher.AppDataPath, "offline_session.json");
                if (!File.Exists(mk) || new FileInfo(mk).Length > 64 * 1024) return false;
                var m = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(mk)) as Dictionary<string, object>;
                if (m == null) return false;
                string g = RecStr(m, "game"), kind = RecStr(m, "kind");
                if (g == "" || kind != "" || !Same(Path.GetFullPath(g).TrimEnd('\\'), p.Game)) return false;
                DateTime started, at;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (!DateTime.TryParseExact(RecStr(m, "started"), "yyyy-MM-dd HH:mm:ss", ci, System.Globalization.DateTimeStyles.None, out started)) return false;
                if (!DateTime.TryParseExact(RecStr(rec, "InstalledAt"), "s", ci, System.Globalization.DateTimeStyles.None, out at)) return false;
                double s = (at - started).TotalSeconds;
                return s >= -60 && s <= 20 * 60;
            }
            catch { return false; }
        }
        // (review of 5db297e8, 1 - the second guard) what a SERVER KIT keeps in its state folder and a sandbox restore never deletes: world
        // saves (remote\world with anything in it), server instances (inst\), world backups (backups\) -> their names in the state folder
        static List<string> KeptKitData(SandboxPaths p)
        {
            var r = new List<string>();
            try
            {
                string w = Path.Combine(p.Remote, "world");
                if (EntryExists(w) && (IsLink(w) || !IsDir(w) || Directory.GetFileSystemEntries(w).Length > 0)) r.Add("remote");
            }
            catch { r.Add("remote"); }
            foreach (var n in new[] { "inst", "backups" }) if (EntryExists(Path.Combine(p.State, n))) r.Add(n);
            return r;
        }
        public static bool IsOurs(SandboxPaths p)
        {
            string rc = Path.Combine(p.Win64, "Mods", "RemoteConsole", "Scripts", "main.lua");
            try { if (File.Exists(rc) && !IsLink(rc) && new FileInfo(rc).Length < 1 << 20 && File.ReadAllText(rc).Contains("loaded (sandbox)")) return true; } catch { }
            return false;
        }
        public static int StopServerCopy(string game)
        {
            if (KitNotOurs(game)) return 0;                                // (a server kit's server is never ours to stop - review A)
            var p = PathsOf(game);
            string root = p.Server + "\\";
            int n = 0;
            foreach (var pr in GameProcesses(game))
            {
                try
                {
                    string img = ImagePath(pr.Id);
                    if (img != "" && Path.GetFullPath(img).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { pr.Kill(); pr.WaitForExit(15000); n++; }
                }
                catch { }
                pr.Dispose();
            }
            return n;
        }


        static void Refusals(SandboxPaths p, string verb)
        {
            string nothing = " - the app will not " + verb + " this game. Nothing was changed.";
            if (EntryExists(p.Pak) && IsLink(p.Pak)) throw new SandboxFail("The game pak is a link to another file" + nothing);
            if (!PlainParents(p.Game, p.Pak)) throw new SandboxFail("A folder of the game pak's path (War\\Content\\Paks) is a link or a file" + nothing);
            foreach (var link in LinkPaths(p.Pak))
                if (!link.StartsWith(p.Game + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new SandboxFail("The game pak is shared with another folder (" + link + ")" + nothing);
            if (EntryExists(p.State) && (IsLink(p.State) || !IsDir(p.State)))
                throw new SandboxFail("The game's _FoxholeOffline entry is not a plain folder (a link or a file) - the app leaves it alone" + nothing);
            // (the backup folders: never read or moved through a link; the remote / server folders only matter to a set-up - Revert removes
            // them as entries, a link as the link)
            var plain = verb == "restore" ? new[] { p.Backup, Path.Combine(p.Backup, "Win64") } : new[] { p.Backup, Path.Combine(p.Backup, "Win64"), p.Remote, p.Server };
            foreach (var d in plain)
                if (EntryExists(d) && (IsLink(d) || !IsDir(d)))
                    throw new SandboxFail("The sandbox's " + d.Substring(p.Game.Length + 1) + " is not a plain folder (a link or a file)" + nothing);
        }
        public const string LEFTOVER_NOTE = "sandbox_leftovers.txt";
        static readonly string[] Stores = { "content_aside", "foreign_mods" };
        public static string Revert(string game)
        {
            var p = PathsOf(game);
            if (!File.Exists(p.Exe)) throw new SandboxFail("Not a Foxhole folder: " + game);
            if (KitNotOurs(game)) throw new SandboxFail("A server kit is set up in this game (its install record, without the sandbox's files) - the app " +
                "leaves it alone. Nothing was changed. Use the kit's \"Back to official Foxhole\", or run Steam's \"Verify integrity of game files\" " +
                "for Foxhole and delete that _FoxholeOffline folder.");     // (review A: before anything is stopped or read for a restore)
            if (GameProcesses(game).Count > 0) throw new SandboxFail("Foxhole is running. Close it first.");
            Refusals(p, "restore");
            var info = Record(p);
            var problems = new List<string>();
            string paks = Path.Combine(p.Game, "War", "Content", "Paks");   // (no mod pak of ours in the public edition - nothing of the player's is removed)
            // 1. the game exe (the server copy's exe is a hard link of it: it gets the original bytes too, and goes in step 5)
            string exeState = ExeState(p), exeBackup = Path.Combine(p.Backup, EXE_NAME);
            string bsha = File.Exists(exeBackup) ? Sha256File(exeBackup) : "";
            bool backupOk = bsha != "" && (Same(bsha, PatchStr("exe", "vanilla_sha256")) || (info != null && Same(bsha, RecStr(info, "ExeVanillaSha"))) ||
                                           PatchList("exe", "known_vanilla_sha256").Any(x => Same(x, bsha)));
            bool putBack = exeState == "patched";
            if (putBack && backupOk) { Log("Restoring the original game exe..."); File.Copy(exeBackup, p.Exe, true); exeState = ExeState(p); }
            else if (exeState == "patched") problems.Add("no usable backup of the game exe - use Steam's 'Verify integrity of game files'");
            if (exeState == "unknown") Log("  the game exe is not one this app knows (a Steam update?) - left as it is; if Foxhole does not start, Steam's 'Verify integrity of game files' repairs it");
            else if (exeState != "vanilla" && exeState != "vanilla-new") problems.Add("game exe is '" + exeState + "'");
            // 2. DefaultEngine.ini inside the pak. (review C1) restored whenever the backup is valid and the region differs from it in a way only
            // this app's write explains (every byte = the backup's or the offline version's): a torn write reads 'unknown' or even 'vanilla' -
            // a pak Steam replaced since (other settings) is left alone
            string pakState = PakState(p), iniBackup = Path.Combine(p.Backup, "DefaultEngine.ini.vanilla");
            if (pakState != "missing")
            {
                try
                {
                    var loc = FhoPakIni.Locate(p.Pak);
                    byte[] bak = File.Exists(iniBackup) ? File.ReadAllBytes(iniBackup) : null;
                    bool bakOk = bak != null && bak.Length == loc[1] && FhoPakIni.State(bak) == "vanilla";
                    var cur = ReadRegion(p.Pak, loc[0], (int)loc[1]);
                    if (bakOk && !cur.SequenceEqual(bak))
                    {
                        byte[] off = null;
                        try { off = FhoPakIni.MakeOffline(bak); } catch { }
                        bool ours = off != null && off.Length == bak.Length && Enumerable.Range(0, cur.Length).All(i => cur[i] == bak[i] || cur[i] == off[i]);
                        if (ours) { Log("Restoring the original network settings inside the game pak..."); WriteRegion(p.Pak, loc[0], bak); }
                        else Log("  the game pak's settings differ from the app's backup in other places (a Steam update?) - left as they are");
                    }
                    else if (!bakOk && pakState == "patched") problems.Add("no usable backup of the game pak's settings - use Steam's 'Verify integrity of game files'");
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                    problems.Add("could not restore the game pak's settings (" + ex.Message + ")");
                }
                pakState = PakState(p);
            }
            if (pakState != "vanilla") problems.Add("game pak is '" + pakState + "'");
            // 3. UE4SS / mods in Win64, then what was there before. (review A2) The BACKUP decides, not only the record: an item whose original
            // waits in backup\Win64 is put back when its place is free, recorded or not; an item recorded as moved whose backup is not there
            // (the install recorded it but crashed before the move, or an earlier restore put it back but could not update the record) is
            // the player's own and stays - ticked off, never deleted.
            var moved = RecList(info, "MovedWin64Items").Where(i => Win64Items.Contains(i)).ToList();   // (R5: only names of our own list)
            string bw = Path.Combine(p.Backup, "Win64");
            Func<string, bool> inBackup = item => Win64Items.Contains(item) && EntryExists(Path.Combine(bw, item));
            bool leftoverRun = info == null && File.Exists(Path.Combine(p.State, LEFTOVER_NOTE));
            if (info != null)
            {
                var pre = RecList(info, "PreExistingWin64Items");
                Log("Removing UE4SS and the offline mods...");
                foreach (var item in Win64Items.Concat(Win64Generated))
                {
                    if (pre.Contains(item) && !inBackup(item)) continue;           // (the player's own, never moved)
                    foreach (var m in Matches(p.Win64, item).ToList())
                        try { DeleteAny(m); } catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) problems.Add("could not remove " + Path.GetFileName(m) + " (" + ex.Message + ")"); else throw; }
                }
                string logic = Path.Combine(paks, "LogicMods");                // (UE4SS's blueprint folder - only while EMPTY)
                if (Directory.Exists(logic) && !IsLink(logic) && Directory.GetFileSystemEntries(logic).Length == 0) Directory.Delete(logic);
            }
            else if (!leftoverRun && File.Exists(Path.Combine(p.Win64, "UE4SS.dll"))) problems.Add("install record missing, so UE4SS files in War\\Binaries\\Win64 were left in place");
            var win64Held = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action tick = () =>
            {
                if (info == null) return;
                info["MovedWin64Items"] = moved.ToArray();
                try { SaveRecord(p, info); }
                catch (Exception ex) { Log("  could not update the install record (" + ex.Message + ") - harmless: the next restore sees from the backup what is back"); }
            };
            foreach (var item in Win64Items)
            {
                string b = Path.Combine(bw, item), dst = Path.Combine(p.Win64, item);
                if (!EntryExists(b))
                {
                    if (moved.Remove(item))
                    {
                        if (!EntryExists(dst)) Log("  note: no backup of an earlier " + item + " is there - nothing to put back");
                        tick();
                    }
                    continue;
                }
                if (EntryExists(dst)) { win64Held[item] = "a file of that name is in War\\Binaries\\Win64 now"; continue; }
                try { MoveEntry(b, dst); Log("  put back: " + item); }
                catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) { win64Held[item] = ex.Message; continue; } throw; }
                if (moved.Remove(item)) tick();                                // (R3: ticked off - a later revert never deletes it again)
            }
            var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in Stores)
                try { RestoreStore(p, Path.Combine(p.State, st), st == "content_aside" ? "aside.txt" : "moved.txt", reasons); }
                catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) reasons[st] = ex.Message; else throw; }
            // 5. the local server copy - last, best-effort (hard links only: the game's files stay)
            string serverLeft = "";
            if (EntryExists(p.Server))
            {
                Log("Removing the local server copy...");
                try { DeleteServerCopy(p.Server); }
                catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) { serverLeft = ex.Message; Log("  " + ex.Message); } else throw; }
            }
            if (problems.Count > 0)
                throw new SandboxFail("Some files could not be restored: " + string.Join("; ", problems) + ". The backup folder was kept (" + p.State + "). Steam's 'Verify integrity of game files' repairs the game." +
                                      (serverLeft != "" ? " (The local server copy is not fully removed either - Restore tries again.)" : ""));
            // (review C3) one text for the return value, sandbox_leftovers.txt and HasLeftovers: what is really still there
            string note = LeftoverText(game, reasons, win64Held, serverLeft);
            // (review of 5db297e8, 1 - second guard) world saves, server instances or world backups in the state folder are never deleted:
            // the folder stays with them (only the rest of the sandbox's files go) and the restore says so
            var kept = KeptKitData(p);
            string keptText = kept.Count == 0 ? "" : "Left as they are in " + p.State + ": " + string.Join(", ", kept.Select(k => k == "remote" ? "remote\\world (world saves)" :
                k == "inst" ? "inst (server instances)" : "backups (world backups)")) + " - a server's data, never deleted by the app; delete that folder yourself if you do not need it.";
            if (note == "" && !Win64BackupLeft(p))                          // (review A2c: never while backup\Win64 holds anything)
            {
                if (kept.Count == 0)
                {
                    if (Directory.Exists(p.State)) DeleteTree(p.State);
                    Log("Foxhole is back to its original files.");
                    return "";
                }
                if (File.Exists(p.Info)) File.Delete(p.Info);
                foreach (var e in Directory.GetFileSystemEntries(p.State))
                {
                    string n = Path.GetFileName(e);
                    if (kept.Any(x => Same(x, n))) continue;
                    try { DeleteAny(e); } catch (Exception ex) { Log("  could not remove " + n + " from the sandbox folder (" + ex.Message + ")"); }
                }
                Log("Foxhole is back to its original files.");
                Log("  " + keptText);
                return keptText;
            }
            // the game is original - the record goes, only the leftovers stay (their own folders + the note)
            if (File.Exists(p.Info)) File.Delete(p.Info);
            foreach (var e in Directory.GetFileSystemEntries(p.State))
            {
                string n = Path.GetFileName(e);
                if (Stores.Any(x => Same(x, n)) || Same(n, "server") || Same(n, LEFTOVER_NOTE) || kept.Any(x => Same(x, n))) continue;
                try
                {
                    if (Same(n, "backup") && !IsLink(e) && IsDir(e) && Win64BackupLeft(p))       // (the backup's other files go, backup\Win64 stays)
                    {
                        foreach (var c in Directory.GetFileSystemEntries(e)) if (!Same(Path.GetFileName(c), "Win64")) DeleteAny(c);
                        continue;
                    }
                    DeleteAny(e);
                }
                catch (Exception ex) { Log("  could not remove " + n + " from the sandbox folder (" + ex.Message + ")"); }
            }
            if (note == "") note = LeftoverText(game, reasons, win64Held, serverLeft);
            if (keptText != "") note = (note + " " + keptText).Trim();
            try
            {
                File.WriteAllText(Path.Combine(p.State, LEFTOVER_NOTE), "Left over from the offline sandbox of Foxhole Mod Manager (" + DateTime.Now.ToString("s") + ").\r\n" +
                    "Your game itself is back to its original files.\r\n\r\n" + note + "\r\n", Encoding.UTF8);
            }
            catch (Exception ex) { Log("  could not write " + LEFTOVER_NOTE + " (" + ex.Message + ") - the app still finds the leftovers"); }
            Log("Foxhole is back to its original files.");
            Log("  " + note);
            return note;
        }
        static bool Win64BackupLeft(SandboxPaths p)
        {
            string bw = Path.Combine(p.Backup, "Win64");
            try { return EntryExists(bw) && (IsLink(bw) || !IsDir(bw) || Directory.GetFileSystemEntries(bw).Length > 0); } catch { return true; }
        }
        // the state folder holds only leftovers of an earlier revert: no install record, and the note there or anything LeftoverText reports
        // (review C3 - a note that could not be written does not hide the leftovers)
        public static bool HasLeftovers(string game)
        {
            try
            {
                var p = PathsOf(game);
                if (File.Exists(p.Info) || !EntryExists(p.State)) return false;
                return File.Exists(Path.Combine(p.State, LEFTOVER_NOTE)) || LeftoverText(game) != "";
            }
            catch { return false; }
        }
        // why one entry of a store cannot go back into War\Content\Paks now (looked at only - nothing changed)
        static string WhyHeld(SandboxPaths p, string rel)
        {
            string paks = Path.Combine(p.Game, "War", "Content", "Paks"), dst = Path.Combine(paks, rel);
            if (EntryExists(paks) && (IsLink(paks) || !IsDir(paks))) return "War\\Content\\Paks is a link or a file";
            if (EntryExists(dst)) return "a file of the same name is in War\\Content\\Paks now";
            if (EntryExists(paks) && !PlainParents(paks, dst)) return "a link or a file is in the way";
            return "it could not be moved - Restore the game tries again";
        }
        // (review C2/C3) what is still left over, in words for the player ("" = nothing), with the real reason per item: reasons = what the
        // last restore saw (rel path or store name -> reason, e.g. an IO error); without one the reason is looked up now
        public static string LeftoverText(string game, Dictionary<string, string> reasons = null, Dictionary<string, string> win64Held = null, string serverLeft = null)
        {
            var p = PathsOf(game);
            var parts = new List<string>();
            var items = new List<string>();
            Func<string, string, string> why = (key, fallback) => reasons != null && reasons.ContainsKey(key) ? reasons[key] : fallback;
            foreach (var st in Stores)
            {
                string d = Path.Combine(p.State, st);
                if (!EntryExists(d)) continue;
                if (IsLink(d) || !IsDir(d)) { items.Add(st + " (not a plain folder - a link or a file; the app leaves it alone)"); continue; }
                try
                {
                    var rels = LinkEntries(d).Concat(SafeFiles(d)).Select(f => f.Substring(d.Length + 1)).Where(r => !Same(r, "aside.txt") && !Same(r, "moved.txt")).ToList();
                    foreach (var r in rels) items.Add(r + " (" + why(r, WhyHeld(p, r)) + ")");
                    if (rels.Count == 0 && reasons != null && reasons.ContainsKey(st)) items.Add(st + " (" + reasons[st] + ")");
                }
                catch (Exception ex) { items.Add(st + " (" + why(st, ex.Message) + ")"); }
            }
            if (items.Count > 0)
                parts.Add(items.Count + " of your own mod file(s) could not be put back into War\\Content\\Paks: " + string.Join("; ", items.Take(5)) + (items.Count > 5 ? "; ..." : "") +
                          ". They wait in " + p.State + " - sort out what is in the way (keep the version you want), then press Restore the game.");
            string bw = Path.Combine(p.Backup, "Win64");
            var w = new List<string>();
            if (EntryExists(bw))
            {
                if (IsLink(bw) || !IsDir(bw)) w.Add("backup\\Win64 (not a plain folder)");
                else
                    try
                    {
                        foreach (var e in Directory.GetFileSystemEntries(bw))
                        {
                            string n = Path.GetFileName(e);
                            string fb = EntryExists(Path.Combine(p.Win64, n)) ? "a file of that name is in War\\Binaries\\Win64 now" : "it could not be moved - Restore the game tries again";
                            w.Add(n + " (" + (win64Held != null && win64Held.ContainsKey(n) ? win64Held[n] : fb) + ")");
                        }
                    }
                    catch (Exception ex) { w.Add("backup\\Win64 (" + ex.Message + ")"); }
            }
            if (w.Count > 0)
                parts.Add(w.Count + " of your earlier file(s) in War\\Binaries\\Win64 could not be put back: " + string.Join("; ", w.Take(5)) + (w.Count > 5 ? "; ..." : "") +
                          ". They wait in " + bw + " - move away what is in the way, then press Restore the game.");
            if (EntryExists(p.Server))
                parts.Add("The sandbox's local server copy (" + p.Server + ", links only - no game data is lost) could not be removed" +
                          (!string.IsNullOrEmpty(serverLeft) ? " (" + serverLeft + ")" : "") + "; Restore the game tries again.");
            return string.Join(" ", parts);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFileNameW(string file, uint flags, ref uint len, StringBuilder name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextFileNameW(IntPtr h, ref uint len, StringBuilder name);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
        // every path of a file's hard links (full paths)
        static List<string> LinkPaths(string file)
        {
            var r = new List<string>();
            if (!File.Exists(file)) return r;
            string vol = Path.GetPathRoot(Path.GetFullPath(file)).TrimEnd('\\');
            var sb = new StringBuilder(1024); uint len = (uint)sb.Capacity;
            IntPtr h = FindFirstFileNameW(file, 0, ref len, sb);
            if (h == new IntPtr(-1)) return r;
            try { do { r.Add(vol + sb.ToString()); sb.Clear(); len = (uint)sb.Capacity; } while (FindNextFileNameW(h, ref len, sb)); }
            finally { FindClose(h); }
            return r;
        }
        // (2026-11-18 review) links: a pak (or a folder of paks) that is a symbolic link / junction is never followed or read - the game would
        // follow it, so the LINK ITSELF is set aside (moved on the same volume: its target is not touched) and put back by the restore
        static bool IsLink(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        static List<string> LinkEntries(string dir)                       // (every link under dir - files and folders - without following any)
        {
            var r = new List<string>();
            if (!Directory.Exists(dir) || IsLink(dir)) return r;
            var todo = new Stack<string>(); todo.Push(dir);
            while (todo.Count > 0)
            {
                string d = todo.Pop();
                foreach (var f in Directory.GetFiles(d)) if (IsLink(f)) r.Add(f);
                foreach (var s in Directory.GetDirectories(d)) if (IsLink(s)) r.Add(s); else todo.Push(s);
            }
            return r;
        }
        static void MoveEntry(string from, string to)
        {
            if ((File.GetAttributes(from) & FileAttributes.Directory) != 0) Directory.Move(from, to); else File.Move(from, to);
        }
        // the player's files in a store put back into War\Content\Paks -> the ones still waiting there (relative path + why). Never follows a
        // link (links go back AS links), never deletes or overwrites anything in the game: a destination that exists in any form (a file, a
        // folder, a link, a dangling link) holds the store's entry back - except a plain file with the same bytes (then only the store's copy
        // goes). A move that fails (a link or a file in the folder chain, access denied) holds that entry back too; the others still go.
        static void RestoreStore(SandboxPaths p, string store, string note, Dictionary<string, string> held)
        {
            string paks = Path.Combine(p.Game, "War", "Content", "Paks");
            if (!EntryExists(store)) return;
            if (IsLink(store) || !IsDir(store)) { held[Path.GetFileName(store)] = "not a plain folder - a link or a file; the app leaves it alone"; return; }
            bool paksOk = !EntryExists(paks) || (!IsLink(paks) && IsDir(paks));
            Func<string, string, bool> tryMove = (from, rel) =>
            {
                string dst = Path.Combine(paks, rel);
                if (!paksOk) { held[rel] = "War\\Content\\Paks is a link or a file"; return false; }
                if (EntryExists(dst)) { held[rel] = "a file of the same name is in War\\Content\\Paks now"; return false; }
                try
                {
                    Directory.CreateDirectory(paks);
                    if (!PlainParents(paks, dst)) { held[rel] = "a link or a file is in the way"; return false; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (!PlainParents(paks, dst) || EntryExists(dst)) { held[rel] = "a link or a file is in the way"; return false; }
                    MoveEntry(from, dst);
                    return true;
                }
                catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) { held[rel] = ex.Message; return false; } throw; }
            };
            foreach (var l in LinkEntries(store))                          // (links set aside: put back as links, never followed)
            {
                string rel = l.Substring(store.Length + 1);
                if (EntryExists(Path.Combine(paks, rel))) { held[rel] = "a file of the same name is in War\\Content\\Paks now"; continue; }
                if (tryMove(l, rel)) Log("  put back (link): " + rel);
            }
            foreach (var f in SafeFiles(store))
            {
                string rel = f.Substring(store.Length + 1);
                if (Same(rel, note)) continue;
                string dst = Path.Combine(paks, rel);
                if (EntryExists(dst))
                {
                    bool plainFile = paksOk && PlainParents(paks, dst) && !IsLink(dst) && !IsDir(dst);
                    if (plainFile && new FileInfo(dst).Length == new FileInfo(f).Length && Same(Sha256File(dst), Sha256File(f))) { File.Delete(f); continue; }   // (the same file)
                    held[rel] = "a file of the same name is in War\\Content\\Paks now"; continue;
                }
                if (tryMove(f, rel)) Log("  put back: " + rel);
            }
            if (!LinkEntries(store).Any() && !SafeFiles(store).Any(f => !Same(f.Substring(store.Length + 1), note))) try { DeleteTree(store); } catch { }
        }
    }
}
