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
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// (the version resource: LauncherAssemblyInfo.cs)

namespace FoxholeModLauncher
{
    partial class Launcher : Form
    {
        static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        static string dataDir;
        static string DataDir { get { return dataDir ?? (dataDir = FindDataDir()); } }
        static string FindDataDir()
        {
            string app = AppDomain.CurrentDomain.BaseDirectory;
            // (tests and the check's smoke start: FOX_LAUNCHER_DATA_DIR = a throw-away folder - never the player's app data)
            string over = Environment.GetEnvironmentVariable("FOX_LAUNCHER_DATA_DIR");
            if (!string.IsNullOrEmpty(over) && Path.IsPathRooted(over))
            {
                try { Directory.CreateDirectory(over); return Path.GetFullPath(over).TrimEnd('\\') + "\\"; } catch { }
            }
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxholeModManager");
            try
            {
                Directory.CreateDirectory(d);
                foreach (var name in new[] { "launcher_options.json", "offline_session.json", "launcher.log" })
                {
                    string from = Path.Combine(app, name), to = Path.Combine(d, name);
                    if (File.Exists(from) && !File.Exists(to)) { File.Copy(from, to); try { File.Delete(from); } catch { } }
                }
                foreach (var name in new[] { "mods", "lowspec_backup" })
                {
                    string from = Path.Combine(app, name), to = Path.Combine(d, name);
                    if (Directory.Exists(from) && !Directory.Exists(to)) { CopyTree(from, to); try { Directory.Delete(from, true); } catch { } }
                }
                return d + "\\";
            }
            catch { return app; }                                        // (no app-data folder: as before)
        }
        static void CopyTree(string from, string to)                     // (also across drives - Directory.Move cannot)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
            foreach (var sub in Directory.GetDirectories(from))
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
        static readonly string Kit = Path.Combine(Dir, "kit");
        static readonly string StatePath = Path.Combine(Dir, "launcher_state.json");
        Dictionary<string, object> cfg = new Dictionary<string, object>();
        Dictionary<string, object> state = new Dictionary<string, object>();
        string address = "";

        static Dictionary<string, object> ReadJson(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, object>();
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path)) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }
        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        // ---- downloads: a Drive file id (anyone with the link), an http address or a local file
        void Fetch(string src, string dst, long size) { Fetch(src, dst, size, true); }
        void Fetch(string src, string dst, long size, bool report) { Fetch(src, dst, size, report, null); }
        // (referer: a third-party tool's official download page that refuses links from elsewhere - LauncherMapEditor.cs runtime)
        void Fetch(string src, string dst, long size, bool report, string referer)
        {
            string part = dst + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (File.Exists(src)) { File.Copy(src, part, true); }
            else
            {
                var req = (HttpWebRequest)WebRequest.Create(src);
                req.UserAgent = "FoxholeModLauncher/1.0";
                if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
                req.AllowAutoRedirect = true;
                req.Timeout = 60000; req.ReadWriteTimeout = 120000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((resp.ContentType ?? "").StartsWith("text/html") && size > 0)
                        throw new Exception("the download host answered with a web page (Google's daily download limit?) - try again later");
                    using (var s = resp.GetResponseStream())
                    using (var f = File.Create(part))
                    {
                        var buf = new byte[1 << 16]; long got = 0; int n; int lastPct = -1;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            f.Write(buf, 0, n); got += n;
                            int pct = size > 0 ? (int)(got * 100 / size) : 0;
                            if (report && pct != lastPct) { lastPct = pct; Progress(Math.Min(100, Math.Max(0, pct))); }
                        }
                    }
                }
            }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(part, dst);
        }
        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }
        // (a hash per path, remembered with the file's size and time - the 450 MB pak is not read again on every start)
        string LocalHash(string path)
        {
            if (!File.Exists(path)) return "";
            var fi = new FileInfo(path);
            string key = path.Substring(Dir.Length).Replace('\\', '/');
            string seen = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            object o;
            var hashes = state.TryGetValue("hashes", out o) ? (Dictionary<string, object>)o : new Dictionary<string, object>();
            state["hashes"] = hashes;
            if (hashes.TryGetValue(key, out o))
            {
                var e = (Dictionary<string, object>)o;
                if (Str(e, "seen") == seen) return Str(e, "sha256");
            }
            string h = Sha256(path);
            hashes[key] = new Dictionary<string, object> { { "seen", seen }, { "sha256", h } };
            return h;
        }



        static bool Yes(Dictionary<string, object> d, string k) { return Str(d, k).ToLowerInvariant() == "true"; }

    }
}
