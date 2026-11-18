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
        // (tests, no window) --sandbox-install <game> | --sandbox-revert <game> | --sandbox-stop <game> | --sandbox-play <game> <map> <0|1>
        static bool SandboxCommandLine(string[] args)
        {
            if (args.Length < 2 || !args[0].StartsWith("--sandbox-")) return false;
            AttachConsole(-1);
            Headless = true;
            Sandbox.Log = s => Console.WriteLine(s);
            Environment.ExitCode = 0;
            try
            {
                if (args[0] == "--sandbox-install") Sandbox.Install(args[1]);
                else if (args[0] == "--sandbox-revert") { string note = Sandbox.Revert(args[1]); if (note != "") Console.WriteLine("NOTE: " + note); }
                else if (args[0] == "--sandbox-stop") Console.WriteLine("stopped " + Sandbox.Stop(args[1]) + " process(es)");
                else if (args[0] == "--sandbox-play" && args.Length >= 4) Sandbox.Play(args[1], args[2], args[3]);
                // (tests) the Live War guard and the restore's no-state-folder check: prints the reason or "none"; exit 3 = blocked / changed
                else if (args[0] == "--sandbox-livecheck") { string w = Sandbox.LiveWarBlock(args[1]); Console.WriteLine("live war: " + (w ?? "none")); if (w != null) Environment.ExitCode = 3; }
                else if (args[0] == "--sandbox-changed") { string w = Sandbox.ChangedWithoutState(args[1]); Console.WriteLine("changed: " + (w ?? "none")); if (w != null) Environment.ExitCode = 3; }
                else if (args[0] == "--sandbox-leftovers") Console.WriteLine("leftovers: " + Sandbox.HasLeftovers(args[1]) + " | " + Sandbox.LeftoverText(args[1]));
                else { Console.WriteLine("unknown sandbox command"); Environment.ExitCode = 2; }
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Environment.ExitCode = 1; }
            return true;
        }
    }

    class SandboxPaths
    {
        public string Game, Win64, Exe, Pak, State, Backup, Server, Remote, Info, Fox;
        public SandboxPaths(string game, string pakRel)
        {
            Game = Path.GetFullPath(game).TrimEnd('\\');
            Win64 = Path.Combine(Game, Sandbox.WIN64_REL);
            Exe = Path.Combine(Win64, Sandbox.EXE_NAME);
            Pak = Path.GetFullPath(Path.Combine(Game, pakRel.Replace('/', '\\')));
            if (!Pak.StartsWith(Game + "\\", StringComparison.OrdinalIgnoreCase)) throw new SandboxFail("bad pak path in patch.json");
            State = Path.Combine(Game, "_FoxholeOffline");
            Backup = Path.Combine(State, "backup");
            Server = Path.Combine(State, "server");
            Remote = Path.Combine(State, "remote");
            Info = Path.Combine(State, "install.json");
            Fox = Path.Combine(State, "foxserver");
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

        static string Kit { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kit"); } }
        static string Payload { get { return Path.Combine(Kit, "payload"); } }
        static Dictionary<string, object> patch;
        static Dictionary<string, object> Patch
        {
            get
            {
                if (patch == null) patch = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(Payload, "patch.json"))) as Dictionary<string, object>;
                return patch;
            }
        }
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
        static string Sha256Bytes(byte[] b) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", ""); }
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
        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var s in Directory.GetDirectories(from)) CopyTree(s, Path.Combine(to, Path.GetFileName(s)));
        }
        // a Win64 item name, or a pattern with * (addresses-*.txt) -> the matching paths
        static IEnumerable<string> Matches(string dir, string item)
        {
            if (!Directory.Exists(dir)) yield break;
            if (item.Contains("*")) { foreach (var e in Directory.GetFileSystemEntries(dir, item)) yield return e; yield break; }
            string p = Path.Combine(dir, item);
            if (File.Exists(p) || Directory.Exists(p)) yield return p;
        }
        static bool Exists(string dir, string item) { return Matches(dir, item).Any(); }
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateHardLinkW(string link, string target, IntPtr sec);
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
        static void CopyDurable(string src, string dst)
        {
            File.Copy(src, dst, true);
            using (var fs = new FileStream(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) fs.Flush(true);
        }
        static void SaveRecord(SandboxPaths p, Dictionary<string, object> o) { WriteAtomic(p.Info, Encoding.ASCII.GetBytes(new JavaScriptSerializer().Serialize(o))); }

        // ---------------------------------------------------------------------------------------------------- state
        static string PatcherHints(bool known) { return known ? PatchStr("exe", "cave_hints") : ""; }
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
            foreach (var x in PatchList("exe", "patched_sha256").Concat(PatchList("exe", "older_patched_sha256"))) if (Same(h, x)) return "patched";
            var rec = Record(p);
            if (rec != null && Same(h, RecStr(rec, "ExePatchedSha"))) return "patched";
            if (KnownHashes("patched").Any(x => Same(x, h))) return "patched";       // (review B: the app's own record, kept outside the game)
            if (rec != null && Same(h, RecStr(rec, "ExeVanillaSha"))) return "vanilla-new";
            if (KnownHashes("vanilla").Any(x => Same(x, h))) return "vanilla-new";
            if (FhoExePatcher.Apply(File.ReadAllBytes(p.Exe), "") == "") return "vanilla-new";
            return "unknown";
        }
        public static string PakState(SandboxPaths p)
        {
            if (!File.Exists(p.Pak)) return "missing";
            try { var loc = FhoPakIni.Locate(p.Pak); return FhoPakIni.State(ReadRegion(p.Pak, loc[0], (int)loc[1])); } catch { return "unknown"; }
        }

        // (review B) the exe hashes this app produced / started from, kept in its OWN data folder (%LOCALAPPDATA%\FoxholeModManager) - they
        // survive a revert and a deleted _FoxholeOffline folder, so a patched exe is still recognised as patched when the game's own record
        // is gone. {"patched": [sha...], "vanilla": [sha...]}; written at install time BEFORE the patched exe is.
        static string AppDataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxholeModManager"); } }
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
        static void RememberExeHashes(string vanilla, string patched)    // (throws when it cannot be written: the install stops before the exe changes)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in new[] { new KeyValuePair<string, string>("vanilla", vanilla), new KeyValuePair<string, string>("patched", patched) })
            {
                var l = KnownHashes(kv.Key);
                if (!l.Any(x => Same(x, kv.Value))) l.Add(kv.Value.ToUpperInvariant());
                if (l.Count > 64) l = l.Skip(l.Count - 64).ToList();
                d[kv.Key] = l.ToArray();
            }
            Directory.CreateDirectory(AppDataDir);
            WriteAtomic(KnownHashFile, Encoding.ASCII.GetBytes(new JavaScriptSerializer().Serialize(d)));
            if (!KnownHashes("patched").Any(x => Same(x, patched))) throw new SandboxFail("Could not record the patched exe's fingerprint in " + AppDataDir + ". Nothing was changed.");
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
                patchedShas.AddRange(PatchList("exe", "patched_sha256").Concat(PatchList("exe", "older_patched_sha256")));
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

        public static void Install(string game)
        {
            var p = PathsOf(game);
            if (!File.Exists(p.Exe)) throw new SandboxFail("Not a Foxhole folder: " + game);
            if (GameProcesses(game).Count > 0) throw new SandboxFail("Foxhole is running. Close it first.");
            // (review A3) the refusals Revert has, BEFORE anything is written: a revert must always be able to undo what an install did
            Refusals(p, "set up");
            string bw = Path.Combine(p.Backup, "Win64");
            var rec0 = Record(p);
            var moved0 = RecList(rec0, "MovedWin64Items");
            foreach (var item in Win64Items)                               // (an earlier backup of a Win64 item next to a file of that name: whose? never guessed)
                if (EntryExists(Path.Combine(bw, item)) && EntryExists(Path.Combine(p.Win64, item)) && !moved0.Contains(item))
                    throw new SandboxFail("An earlier backup of " + item + " waits in " + bw + " and War\\Binaries\\Win64 has one of that name too - press Restore the game first. Nothing was changed.");
            Log("Checking game files (this reads the 80 MB game exe)...");
            string exeState = ExeState(p), pakState = PakState(p);
            string exeBackup = Path.Combine(p.Backup, EXE_NAME);
            string vanillaSha = PatchStr("exe", "vanilla_sha256");
            // (review C4) an UNKNOWN exe is never replaced by an old backup here (a Steam update would be overwritten by an older build) -
            // only Revert puts a backup back, and only over an exe this app patched or could not read
            int recVersion = 1; int.TryParse(RecStr(rec0, "ExePatcherVersion"), out recVersion); if (recVersion == 0) recVersion = 1;
            if (exeState == "patched" && recVersion < FhoExePatcher.Version && File.Exists(exeBackup))
            {
                string b = Sha256File(exeBackup);
                if (Same(b, vanillaSha) || (rec0 != null && Same(b, RecStr(rec0, "ExeVanillaSha")))) { File.Copy(exeBackup, p.Exe, true); exeState = ExeState(p); }
            }
            Log("  game exe: " + exeState + "   game pak: " + pakState);
            if (exeState == "unknown" || pakState == "unknown" || pakState == "missing")
                throw new SandboxFail("This Foxhole version is not one the patches know. Nothing was changed. If you changed the game files yourself, run Steam's 'Verify integrity of game files' and try again.");
            // the state folder + record, written before any change (so a revert can always clean up)
            Directory.CreateDirectory(p.Backup); Directory.CreateDirectory(p.Remote);
            var info = rec0;
            var moved = RecList(info, "MovedWin64Items");
            var pre = info != null ? RecList(info, "PreExistingWin64Items") : Win64Items.Concat(Win64Generated).Where(i => Exists(p.Win64, i)).ToList();
            string vSha = RecStr(info, "ExeVanillaSha"), pSha = RecStr(info, "ExePatchedSha");
            int pv = info != null && RecStr(info, "ExePatcherVersion") != "" ? int.Parse(RecStr(info, "ExePatcherVersion")) : 1;
            Action<string> record = status =>
            {
                var o = new Dictionary<string, object> { { "Status", status }, { "GameVersion", Patch.ContainsKey("game_version") ? Patch["game_version"] : "" },
                    { "GameDir", p.Game }, { "InstalledAt", DateTime.Now.ToString("s") }, { "MovedWin64Items", moved.ToArray() }, { "PreExistingWin64Items", pre.ToArray() } };
                if (vSha != "") { o["ExeVanillaSha"] = vSha; o["ExePatchedSha"] = pSha; }
                o["ExePatcherVersion"] = pv;
                SaveRecord(p, o);
            };
            record("installing");
            // 1. the game exe (patch places found by signature)
            if (exeState == "vanilla" || exeState == "vanilla-new")
            {
                Log("Backing up and patching the game exe...");
                string orig = Sha256File(p.Exe);
                CopyDurable(p.Exe, exeBackup);
                if (!Same(Sha256File(exeBackup), orig)) throw new SandboxFail("Backup of the game exe failed verification.");
                var bytes = File.ReadAllBytes(exeBackup);
                string err = FhoExePatcher.Apply(bytes, PatcherHints(exeState == "vanilla"));
                if (err != "") throw new SandboxFail("Could not patch the game exe: " + err + ". Nothing was changed.");
                string nsha = Sha256Bytes(bytes);
                if (exeState == "vanilla" && !PatchList("exe", "patched_sha256").Any(x => Same(x, nsha))) throw new SandboxFail("The patched exe is not the tested one. Nothing was changed.");
                vSha = orig; pSha = nsha; pv = FhoExePatcher.Version;      // (R3: recorded first - a crash while writing still has a trusted backup)
                RememberExeHashes(orig, nsha);                             // (review B: the app's own record - survives a deleted state folder)
                record("installing");
                File.WriteAllBytes(p.Exe, bytes);
                if (!Same(Sha256File(p.Exe), nsha)) { File.Copy(exeBackup, p.Exe, true); throw new SandboxFail("Patched exe failed verification; the original was put back."); }
                vSha = orig; pSha = nsha; pv = FhoExePatcher.Version;
                record("installing");
            }
            // 2. DefaultEngine.ini inside the main pak (same size, patched in place; only those bytes are backed up)
            string iniBackup = Path.Combine(p.Backup, "DefaultEngine.ini.vanilla");
            if (pakState == "vanilla")
            {
                Log("Backing up and patching the network settings inside the game pak...");
                var loc = FhoPakIni.Locate(p.Pak);
                var vanilla = ReadRegion(p.Pak, loc[0], (int)loc[1]);
                WriteAtomic(iniBackup, vanilla);
                WriteRegion(p.Pak, loc[0], FhoPakIni.MakeOffline(vanilla));
                if (PakState(p) != "patched") { WriteRegion(p.Pak, loc[0], vanilla); throw new SandboxFail("Patched pak failed verification; the original bytes were put back."); }
            }
            // 3. UE4SS + the client mods + FoxClient in Win64 (what was there before goes to the backup)
            Log("Installing UE4SS and the client mods...");
            Directory.CreateDirectory(bw);
            // (review A2b) RECORDED FIRST, then moved: a crash between leaves "recorded, not moved" (Revert: the original is still in place -
            // ticked off, never deleted), never "moved, not recorded". A backup is never overwritten.
            foreach (var item in Win64Items)
            {
                if (!pre.Contains(item)) continue;                          // (not there before the sandbox: ours)
                string src = Path.Combine(p.Win64, item), dst = Path.Combine(bw, item);
                if (EntryExists(dst)) { if (!moved.Contains(item)) { moved.Add(item); record("installing"); } continue; }   // (already in the backup)
                if (!EntryExists(src)) continue;
                if (!moved.Contains(item)) { moved.Add(item); record("installing"); }
                MoveEntry(src, dst);
            }
            foreach (var f in new[] { "dwmapi.dll", "UE4SS.dll", "UE4SS-settings.ini" }) File.Copy(Path.Combine(Payload, "ue4ss", f), Path.Combine(p.Win64, f), true);
            File.WriteAllText(Path.Combine(p.Win64, "steam_appid.txt"), "505460", Encoding.ASCII);
            CopyModFiles(p);
            CopyFoxClient(p);
            // 4. the server copy: hard links to the (patched) game files - no extra disk space
            Log("Creating the local server copy (hard links, no extra disk space)...");
            if (Directory.Exists(p.Server)) DeleteServerCopy(p.Server);
            var skip = new[] { WIN64_REL + "\\", "War\\Content\\Paks\\", "War\\Saved\\" };
            var files = new List<string>();
            foreach (var top in new[] { "Engine", "War" })
            {
                string d = Path.Combine(p.Game, top);
                if (!Directory.Exists(d)) continue;
                foreach (var f in SafeFiles(d))
                {
                    string rel = f.Substring(p.Game.Length + 1);
                    if (!skip.Any(s => rel.StartsWith(s, StringComparison.OrdinalIgnoreCase))) files.Add(f);
                }
            }
            files.Add(p.Exe); files.Add(p.Pak);
            foreach (var f in files)
            {
                string dst = Path.Combine(p.Server, f.Substring(p.Game.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (!CreateHardLinkW(dst, f, IntPtr.Zero)) throw new SandboxFail("Could not create a hard link (the Foxhole drive must be NTFS): error " + Marshal.GetLastWin32Error());
            }
            ClearServerUe4ss(Path.Combine(p.Server, WIN64_REL));
            record("installed");
            Log("Set up for the offline sandbox.");
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
        static void CopyModFiles(SandboxPaths p)
        {
            File.Copy(Path.Combine(Payload, "ue4ss", "UE4SS-settings.ini"), Path.Combine(p.Win64, "UE4SS-settings.ini"), true);
            string mods = Path.Combine(p.Win64, "Mods");
            if (Directory.Exists(mods)) DeleteTree(mods);
            CopyTree(Path.Combine(Payload, "Mods-client"), mods);
            string remote = p.Remote.Replace("\\", "\\\\");
            foreach (var lua in SafeFiles(mods).Where(x => x.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
            {
                string t = File.ReadAllText(lua);
                if (t.Contains("@@REMOTE@@")) File.WriteAllText(lua, t.Replace("@@REMOTE@@", remote), new UTF8Encoding(false));
            }
        }
        static void CopyFoxClient(SandboxPaths p)
        {
            string src = Path.Combine(Payload, "FoxClient.dll");
            if (!File.Exists(src)) return;
            for (int i = 1; ; i++)
            {
                try { File.Copy(src, Path.Combine(p.Win64, "FoxClient.dll"), true); break; }
                catch { if (i >= 15) throw; Thread.Sleep(1000); }
            }
        }
        static void ClearServerUe4ss(string win64)
        {
            foreach (var item in new[] { "dwmapi.dll", "UE4SS.dll", "UE4SS-settings.ini", "Mods" }.Concat(Win64Generated))
                foreach (var m in Matches(win64, item).ToList()) DeleteAny(m);
            File.WriteAllText(Path.Combine(win64, "steam_appid.txt"), "505460", Encoding.ASCII);
        }

        const int GAME_PORT = 7777, BEACON_PORT = 15000;
        const string PROXY_NAME = "X3DAudio1_7.dll";
        static string AppDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        static void WriteAscii(string path, string text) { File.WriteAllText(path, text, new ASCIIEncoding()); }   // (no BOM, no newline added)
        static bool FileHas(string path, string text)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) return r.ReadToEnd().Contains(text);
            }
            catch { return false; }
        }
        static bool WaitFor(Func<bool> cond, int seconds, Process mustLive)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                if (cond()) return true;
                if (mustLive != null && mustLive.HasExited) return false;
                Thread.Sleep(500);
            }
            return cond();
        }
        static string NewSecret(int n)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder();
            foreach (var x in b) sb.Append(chars[x % chars.Length]);
            return sb.ToString();
        }
        [StructLayout(LayoutKind.Sequential)] struct BY_HANDLE_FILE_INFORMATION { public uint Attr, CLo, CHi, ALo, AHi, WLo, WHi, Vol, SizeHi, SizeLo, Links, IdxHi, IdxLo; }   // (FILETIMEs = 2 x DWORD: 4-byte aligned)
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION i);
        static uint LinkCount(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            { BY_HANDLE_FILE_INFORMATION i; return GetFileInformationByHandle(fs.SafeFileHandle, out i) ? i.Links : 0; }
        }
        static void PlaceOwnFile(string src, string dst, SandboxPaths p)
        {
            string full = Path.GetFullPath(dst);
            if (!full.StartsWith(p.State + "\\", StringComparison.OrdinalIgnoreCase)) throw new SandboxFail("refused to write outside the sandbox folder: " + dst);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            if (File.Exists(full)) { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); }   // (our own file - never a game link here)
            File.Copy(src, full);
            if (LinkCount(full) != 1) throw new SandboxFail("unexpected hard link at " + full);
        }

        static string AddressCacheName(string exe)
        {
            try
            {
                var h = new byte[4096];
                using (var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) f.Read(h, 0, h.Length);
                int nt = BitConverter.ToInt32(h, 0x3c);
                if (nt < 0 || nt + 0x18 + 0x3c > h.Length) return "";
                return string.Format("addresses-{0:x8}-{1:x8}.txt", BitConverter.ToUInt32(h, nt + 8), BitConverter.ToUInt32(h, nt + 0x18 + 0x38));
            }
            catch { return ""; }
        }
        // (2026-11-18 review) the game also loads .pak files from %LOCALAPPDATA%\Foxhole\Saved\Paks - outside the game folder, so the sandbox
        // cannot set them aside (it never moves the player's files outside the game folder): a sandbox start is refused while any is there
        // (a link there too - its target is never read). -> null = nothing there, else the message for the player.
        public static string SavedPaksProblem()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foxhole", "Saved", "Paks");
            try
            {
                if (!EntryExists(dir)) return null;
                var found = new List<string>();
                if (IsLink(dir) || !IsDir(dir)) found.Add("Paks itself is a link or a file");
                else
                {
                    found.AddRange(LinkEntries(dir).Select(x => x.Substring(dir.Length + 1) + " (a link)"));
                    found.AddRange(SafeFiles(dir).Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).Select(f => f.Substring(dir.Length + 1)));
                }
                if (found.Count == 0) return null;
                return "Foxhole's own mod folder " + dir + " holds " + found.Count + " pak file(s) or link(s) (" + string.Join(", ", found.Take(5)) + (found.Count > 5 ? ", ..." : "") +
                       "). The game would load them in the sandbox too, and the app does not move files outside the game folder - move them out of " +
                       "that folder yourself, then start the sandbox again. Nothing was changed.";
            }
            catch (Exception ex) { return "Could not check " + dir + " (" + ex.Message + ") - nothing was changed."; }
        }
        // the whole start: server copy refreshed, the server started (FoxServer through the stand-in X3DAudio1_7.dll), the player's game
        // started and connected. Returns the player's game process.
        public static Process Play(string game, string map, string faction)
        {
            var p = PathsOf(game);
            if (!File.Exists(p.Info) || RecStr(Record(p), "Status") != "installed") throw new SandboxFail("The sandbox is not set up in this game yet.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(map, "^[A-Za-z0-9_]{1,64}$")) throw new SandboxFail("bad map name");
            if (faction != "0" && faction != "1") faction = "1";
            string saved = SavedPaksProblem();
            if (saved != null) throw new SandboxFail(saved);
            if (GameProcesses(game).Count > 0) Stop(game);                 // (a sandbox left from a crash)
            string sWin64 = Path.Combine(p.Server, WIN64_REL);
            if (!File.Exists(Path.Combine(sWin64, EXE_NAME))) throw new SandboxFail("The sandbox's server copy is missing - set it up again (Restore the game, then Play offline).");
            // Steam first (the game needs it running and signed in; admins.txt reads its signed-in account)
            if (Process.GetProcessesByName("steam").Length == 0)
            {
                Log("Starting Steam (Foxhole needs it running)...");
                try { Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); } catch { }
                for (int i = 0; i < 45 && Process.GetProcessesByName("steamwebhelper").Length == 0; i++) Thread.Sleep(2000);
                Thread.Sleep(10000);
            }
            ClearServerUe4ss(sWin64);
            CopyModFiles(p);
            CopyFoxClient(p);
            SetAsideModContent(p);
            ScrubServerPaks(p);
            Directory.CreateDirectory(p.Remote); Directory.CreateDirectory(p.Fox);
            foreach (var n in new[] { "server_cmd.txt", "cmd.txt", "cmd_client.txt", "foxserver_ready.txt", "foxserver_spawn.txt", "merge_ready.txt", "layout_done.txt", "compat_paks.txt", "merge.txt" })
                if (File.Exists(Path.Combine(p.Remote, n))) File.Delete(Path.Combine(p.Remote, n));
            foreach (var f in Directory.GetFiles(p.Remote, "mapdata_*_*.txt")) File.Delete(f);
            WriteAscii(Path.Combine(p.Remote, "session.txt"), "faction=" + faction + "\nmap=" + map + "\ndeploy_on_join=0");   // (no backend: spawned at once)
            try { File.AppendAllText(Path.Combine(p.Remote, "server_out.txt"), "\r\n==== " + DateTime.Now.ToString("s") + " new session: " + map + ", offline sandbox\r\n", new ASCIIEncoding()); } catch { }
            WriteAscii(Path.Combine(p.Remote, "join_host.txt"), "127.0.0.1:" + GAME_PORT);
            WriteAscii(Path.Combine(p.Remote, "instances.txt"), map + " " + GAME_PORT + " " + BEACON_PORT + " " + p.Remote);
            string admins = Path.Combine(p.Remote, "admins.txt");
            bool named = File.Exists(admins) && File.ReadAllLines(admins).Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*\d{17}\s*$"));
            if (!named)
            {
                var lines = File.Exists(admins) ? File.ReadAllLines(admins).ToList() : new List<string> { "# SteamID64 of each server admin, one per line" };
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                    {
                        object v = k != null ? k.GetValue("ActiveUser") : null;
                        long acc; if (v != null && long.TryParse(v.ToString(), out acc) && acc > 0) lines.Add((76561197960265728L + acc).ToString());
                    }
                }
                catch { }
                File.WriteAllText(admins, string.Join("\r\n", lines) + "\r\n", new ASCIIEncoding());
            }
            foreach (var s in new[] { new KeyValuePair<string, int>("server_password.txt", 20), new KeyValuePair<string, int>("admin_key.txt", 32) })
            {
                string f = Path.Combine(p.Remote, s.Key);
                if (!File.Exists(f) || File.ReadAllText(f).Trim() == "") WriteAscii(f, NewSecret(s.Value));
                File.Copy(f, Path.Combine(p.Win64, s.Key), true);
            }
            // FoxServer's address cache for this game build (else a 27 s scan that a headless start does not survive)
            string cache = Path.Combine(Payload, "server_cache");
            if (Directory.Exists(cache))
                foreach (var f in Directory.GetFiles(cache, "addresses-*.txt"))
                { string d = Path.Combine(p.Fox, Path.GetFileName(f)); if (!File.Exists(d)) File.Copy(f, d); }
            // FoxServer.dll + the stand-in that loads it (both our own files; the stand-in only in the server copy)
            string fs = Path.Combine(AppDir, "FoxServer", "bin", "sandbox", "FoxServer.dll"), px = Path.Combine(AppDir, "FoxServer", "bin", "sandbox", PROXY_NAME);
            if (!File.Exists(fs) || !File.Exists(px)) throw new SandboxFail("The sandbox server files are missing from this app's folder (FoxServer).");
            try { PlaceOwnFile(fs, Path.Combine(p.Fox, "FoxServer.dll"), p); } catch (IOException) { throw new SandboxFail("A sandbox server is still running - close it first."); }
            PlaceOwnFile(px, Path.Combine(sWin64, PROXY_NAME), p);
            // (2026-11-18) a game build without FoxServer's address cache (a Steam update after this release): its 27 s signature scan does
            // not survive a headless start - this first start draws a small server window instead; FoxServer writes the cache, later
            // starts are headless again
            string cacheName = AddressCacheName(Path.Combine(sWin64, EXE_NAME));
            bool headless = cacheName == "" || File.Exists(Path.Combine(p.Fox, cacheName));
            if (!headless) Log("  a new game build: this first start shows a small server window (it learns the new build once)");
            string mapPath = map.StartsWith("HomeRegion") ? "/Game/Maps/" + map : "/Game/Maps/Master/" + map;
            string bootFile = Path.Combine(p.Remote, "foxserver_boot.txt"), goFile = Path.Combine(p.Remote, "foxserver_go.txt");
            Process srv = null;
            if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == GAME_PORT))
                throw new SandboxFail("Port " + GAME_PORT + " is in use by another program (another Foxhole server?) - close it first.");
            Log("Starting the " + map + " server... (about a minute)");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (File.Exists(bootFile)) File.Delete(bootFile);           // (before the start: FoxServer watches for the go file at once)
                if (File.Exists(goFile)) File.Delete(goFile);
                DateTime t0 = DateTime.Now;
                string srvLog = Path.Combine(p.Remote, "server_war-" + t0.ToString("MMdd-HHmmss") + ".log");
                string args = "-log \"-ABSLOG=" + srvLog + "\" -nosound -windowed -ResX=640 -ResY=360 \"-ExecCmds=t.MaxFPS " + (headless ? 60 : 30) + "\" -port=" + GAME_PORT +
                              " -BeaconPort=" + BEACON_PORT + (headless ? " -nullrhi" : "") + " -multihome=127.0.0.1";
                srv = Process.Start(new ProcessStartInfo(Path.Combine(sWin64, EXE_NAME), args) { UseShellExecute = true, WorkingDirectory = sWin64, WindowStyle = ProcessWindowStyle.Minimized });
                bool menu = WaitFor(() => File.Exists(srvLog) && File.GetLastWriteTime(srvLog) > t0 && FileHas(srvLog, "DisconnectWarServiceExternal"), 240, srv);
                if (menu) { Thread.Sleep(5000); if (!srv.HasExited) break; }
                if (!menu && !srv.HasExited) { try { srv.Kill(); } catch { } throw new SandboxFail("Timed out after 240 s waiting for the " + map + " server to reach its main menu."); }
                if (attempt == 3) throw new SandboxFail("The " + map + " server closed 3 times while starting (crash files: %LOCALAPPDATA%\\Foxhole\\Saved\\Crashes).");
                Log("  the server closed while starting (attempt " + attempt + " of 3); trying again...");
                Thread.Sleep(3000);
            }
            WriteAscii(goFile, "go");
            if (!WaitFor(() => File.Exists(bootFile), 120, srv)) { Stop(game); throw new SandboxFail("FoxServer did not prepare the server (no foxserver_boot.txt)."); }
            string boot = File.ReadAllText(bootFile).Trim();
            if (boot != "ok") { Stop(game); throw new SandboxFail("FoxServer could not prepare the server (" + boot + "). This game build may not be supported yet."); }
            WriteAscii(Path.Combine(p.Remote, "cmd.txt"), "open " + mapPath + "?listen");
            Func<bool> listening = () => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == GAME_PORT);
            if (!WaitFor(listening, 240, srv)) { Stop(game); throw new SandboxFail("The " + map + " server did not open the map."); }
            Log(map + " server is up (this PC only).");
            // the player's game: to its main menu (UE4SS's console line), then connected
            Process cli = null;
            string ue4ssLog = Path.Combine(p.Win64, "UE4SS.log");
            string gameLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foxhole", "Saved", "Logs", "War.log");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                DateTime t1 = DateTime.Now;
                cli = Process.Start(new ProcessStartInfo(p.Exe, "-log") { UseShellExecute = true, WorkingDirectory = p.Win64 });   // (no handles of ours inherited)
                Log("Starting your game...");
                bool ready = false, stuck = false; long lastLen = -1, lastMem = 0; DateTime lastGrow = DateTime.Now;
                for (int i = 0; i < 300 && !ready; i++)
                {
                    Thread.Sleep(2000);
                    if (cli.HasExited) break;
                    ready = File.Exists(ue4ssLog) && File.GetLastWriteTime(ue4ssLog) > t1 && FileHas(ue4ssLog, "ConsoleKey[1]");
                    long len = File.Exists(gameLog) ? new FileInfo(gameLog).Length : 0;
                    cli.Refresh(); long mem = cli.WorkingSet64;
                    if (len != lastLen || Math.Abs(mem - lastMem) > 8L << 20) { lastLen = len; lastMem = mem; lastGrow = DateTime.Now; }
                    else if (!ready && (DateTime.Now - lastGrow).TotalSeconds > 240) { stuck = true; break; }
                }
                if (ready) { Thread.Sleep(3000); break; }
                if (!cli.HasExited) { try { cli.Kill(); } catch { } }
                if (attempt == 3) { Stop(game); throw new SandboxFail(stuck ? "Your game stopped loading - try again." : "Your game closed while starting."); }
                Log("  your game did not reach its menu (attempt " + attempt + " of 3); trying again...");
            }
            WriteAscii(Path.Combine(p.Remote, "cmd_client.txt"), "open 127.0.0.1:" + GAME_PORT + "?FactionId=" + faction);
            Log("Connecting to your sandbox - you are spawned in a few seconds.");
            return cli;
        }

        public static int Stop(string game)
        {
            // (a killed game can start its crash reporter a moment later: rounds until nothing of this folder is left, up to ~30 s)
            int n = 0;
            for (int round = 0; round < 6; round++)
            {
                int k = StopOnce(game);
                n += k;
                if (k == 0) { Thread.Sleep(1500); if (GameProcesses(game).Count == 0) break; }
            }
            return n;
        }
        static int StopOnce(string game)
        {
            var list = GameProcesses(game);
            foreach (var pr in list)                                      // (FoxServer writes its journals first - its drain event)
            {
                try
                {
                    using (var ask = EventWaitHandle.OpenExisting("Local\\FoxServerDrain-" + pr.Id))
                    using (var done = EventWaitHandle.OpenExisting("Local\\FoxServerDrained-" + pr.Id)) { ask.Set(); done.WaitOne(5000); }
                }
                catch { }
            }
            foreach (var pr in list) { try { pr.Kill(); } catch { } }
            for (int i = 0; i < 60 && list.Any(x => { try { return !x.HasExited; } catch { return false; } }); i++) Thread.Sleep(500);
            Thread.Sleep(1000);
            foreach (var pr in list) pr.Dispose();
            return list.Count;
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
            if (GameProcesses(game).Count > 0) throw new SandboxFail("Foxhole is running. Close it first.");
            Refusals(p, "restore");
            var info = Record(p);
            var problems = new List<string>();
            string paks = Path.Combine(p.Game, "War", "Content", "Paks");   // (no mod pak of ours in the public edition - nothing of the player's is removed)
            // 1. the game exe (the server copy's exe is a hard link of it: it gets the original bytes too, and goes in step 5)
            string exeState = ExeState(p), exeBackup = Path.Combine(p.Backup, EXE_NAME);
            string bsha = File.Exists(exeBackup) ? Sha256File(exeBackup) : "";
            bool backupOk = bsha != "" && (Same(bsha, PatchStr("exe", "vanilla_sha256")) || (info != null && Same(bsha, RecStr(info, "ExeVanillaSha"))));
            if ((exeState == "unknown" || exeState == "patched") && backupOk) { Log("Restoring the original game exe..."); File.Copy(exeBackup, p.Exe, true); exeState = ExeState(p); }
            else if (exeState == "patched") problems.Add("no usable backup of the game exe - use Steam's 'Verify integrity of game files'");
            if (exeState != "vanilla" && exeState != "vanilla-new") problems.Add("game exe is '" + exeState + "'");
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
            if (note == "" && !Win64BackupLeft(p))                          // (review A2c: never while backup\Win64 holds anything)
            {
                if (Directory.Exists(p.State)) DeleteTree(p.State);
                Log("Foxhole is back to its original files.");
                return "";
            }
            // the game is original - the record goes, only the leftovers stay (their own folders + the note)
            if (File.Exists(p.Info)) File.Delete(p.Info);
            foreach (var e in Directory.GetFileSystemEntries(p.State))
            {
                string n = Path.GetFileName(e);
                if (Stores.Any(x => Same(x, n)) || Same(n, "server") || Same(n, LEFTOVER_NOTE)) continue;
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
        static readonly string[] KitModPakFiles = { "War-WindowsNoEditor_1_P.pak", "War-WindowsNoEditor_1_P.txt", "War-WindowsNoEditor_1_P.pak.kit",
                                                    "War-WindowsNoEditor_1_P.pak.stamp", "War-WindowsNoEditor_1_P.pak.sha256" };
        static bool AddsContent(string pak)
        {
            try
            {
                string mount; int v;
                foreach (var f in Launcher.PakPaths(pak, out mount, out v))
                {
                    string full = "/" + (mount + "/" + f).Replace('\\', '/');
                    while (full.Contains("//")) full = full.Replace("//", "/");
                    if (full.IndexOf("/Content/Mods/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
            catch { return true; }                                         // (R6: unreadable = not trusted: set aside)
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
        static void SetAsideModContent(SandboxPaths p)
        {
            string paks = Path.Combine(p.Game, "War", "Content", "Paks"), store = Path.Combine(p.State, "content_aside");
            if (!Directory.Exists(paks)) return;
            if (IsLink(paks)) throw new SandboxFail("The game's War\\Content\\Paks folder is a link to another folder - the sandbox cannot check what it holds. Nothing was started.");
            var move = new List<string>();
            foreach (var l in LinkEntries(paks))
            {
                if (Same(Path.GetFullPath(l), Path.GetFullPath(p.Pak))) throw new SandboxFail("The game pak is a link to another file - the sandbox cannot run on it. Nothing was started.");
                move.Add(l);
            }
            foreach (var f in SafeFiles(paks))
            {
                string name = Path.GetFileName(f);
                if (Same(Path.GetFullPath(f), p.Pak)) continue;            // (the base game pak itself - by its full path, not by name at any depth)
                if (KitModPakFiles.Any(k => Same(k, name))) { move.Add(f); continue; }
                if (f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && AddsContent(f))
                {
                    move.Add(f);
                    foreach (var ext in new[] { ".sig", ".utoc", ".ucas" })      // (its companions)
                    { string c = Path.ChangeExtension(f, ext); if (File.Exists(c)) move.Add(c); }
                }
            }
            move = move.Distinct().ToList();
            if (move.Count == 0) return;
            // (2026-11-18 review) the store only ever receives: anything already at a destination (a held file of an earlier session, a folder,
            // a link - also a dangling one) is the PLAYER's - never deleted or overwritten; a link or file in the store's folder chain refuses too.
            // Checked for every entry BEFORE the first move.
            if (EntryExists(store) && (IsLink(store) || !IsDir(store)))
                throw new SandboxFail("The sandbox's " + store + " is not a plain folder - nothing was started. Restore the game, then try again.");
            foreach (var f in move)
            {
                string rel = f.Substring(paks.Length + 1), dst = Path.Combine(store, rel);
                if (EntryExists(dst) || (EntryExists(store) && !PlainParents(store, dst)))
                    throw new SandboxFail("Your mod file " + rel + " cannot be set aside: " + store + " already holds one of that name from an earlier session. " +
                                          "Press Restore the game (or sort out that folder yourself), then start the sandbox again. Nothing was started.");
            }
            foreach (var f in move)
            {
                string rel = f.Substring(paks.Length + 1), dst = Path.Combine(store, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (!PlainParents(store, dst)) throw new SandboxFail("The sandbox's " + store + " changed while setting files aside - nothing more was moved.");
                MoveEntry(f, dst);
                File.AppendAllText(Path.Combine(store, "aside.txt"), DateTime.Now.ToString("s") + "  " + rel + "\r\n", new ASCIIEncoding());
                Log("  set aside for the sandbox (added content): " + rel);
            }
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
        // (2026-11-18 review) the sandbox's SERVER copy loads the base game pak only: anything else in its pak folders (an extra pak, a link, a
        // folder) is removed from the copy - only the copy's own entry (a hard link's name, or the link itself; never a link's target)
        static void ScrubServerPaks(SandboxPaths p)
        {
            string basePak = Path.GetFullPath(Path.Combine(p.Server, p.Pak.Substring(p.Game.Length + 1)));
            foreach (var rel in new[] { "War\\Content\\Paks", "Engine\\Content\\Paks", "War\\Saved\\Paks" })
            {
                string d = Path.Combine(p.Server, rel);
                if (!EntryExists(d)) continue;
                if (IsLink(d)) throw new SandboxFail("The sandbox's server copy is damaged (" + rel + " is a link) - set it up again (Restore the game, then Play offline).");
                if (!IsDir(d))                                             // (a plain file where the folder belongs: only that name of the copy)
                {
                    RemoveCopyFile(d);
                    Log("  removed from the sandbox server copy: " + rel + " (a file)");
                    continue;
                }
                foreach (var e in Directory.GetFileSystemEntries(d))
                {
                    bool link = IsLink(e), dir = IsDir(e), isBase = Same(Path.GetFullPath(e), basePak);
                    if (isBase && !link && !dir) continue;
                    if (isBase) throw new SandboxFail("The sandbox's server copy is damaged (its game pak is a link) - set it up again (Restore the game, then Play offline).");
                    if (link) { if (dir) Directory.Delete(e, false); else File.Delete(e); }
                    else if (dir) DeleteServerCopy(e);
                    else RemoveCopyFile(e);                                // (a hard link: a read-only flag is shared with the other names - put back)
                    Log("  removed from the sandbox server copy: " + rel + "\\" + Path.GetFileName(e));
                }
            }
        }
    }
}
