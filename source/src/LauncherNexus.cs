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
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace FoxholeModLauncher
{
    // ---------------------------------------------------------------------------------------------------- the HTTP rules
    static class NexusHttp
    {
        public const string APP_NAME = "Foxhole Mod Manager";
        public static readonly string AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        static readonly string[] ExactHosts = { "api.nexusmods.com", "staticdelivery.nexusmods.com" };   // (www.nexusmods.com: opened in the player's browser only)
        static readonly string[] SuffixHosts = { ".nexus-cdn.com" };
        public const long JSON_MAX = 2L << 20, IMAGE_MAX = 5L << 20;
        const int MAX_HOPS = 4;
        static DateTime busyUntil = DateTime.MinValue;               // (429 / Retry-After: no request before this)
        static readonly object gate = new object();
        // (2026-11-18 tests) --no-net: every request is counted and refused before any connection; the commands print "network requests: N"
        public static bool NoNet;
        public static int Attempts;

        public class Busy : Exception { public Busy(int s, string service = "Nexus Mods") : base(service + " asked us to wait (" + s + " s) - try again later") { } }

        public static void EnableTls()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }      // TLS 1.2
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }     // TLS 1.3 (where Windows has it)
        }

        public static bool Allowed(Uri u)
        {
            if (u == null || !u.IsAbsoluteUri || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo != "") return false;
            string h = u.IdnHost.ToLowerInvariant();
            foreach (var x in ExactHosts) if (h == x) return true;
            foreach (var x in SuffixHosts) if (h.EndsWith(x, StringComparison.Ordinal) && h.Length > x.Length) return true;
            return false;
        }

        // GET (postJson null) or POST JSON; the body read up to cap bytes, else an exception
        // (2026-11-18) the same rules for the public edition's update check, with its own exact host list (GitHub's raw files only); service =
        // the name the error texts use ("GitHub" there - never "Nexus Mods" for another host)
        public static byte[] SendTo(string url, long cap, string[] hosts, string service)
        {
            return Send(url, cap, null, u => u != null && u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo == "" &&
                                              Array.IndexOf(hosts, u.IdnHost.ToLowerInvariant()) >= 0, service);
        }
        public static byte[] Send(string url, long cap, string postJson, Func<Uri, bool> allow = null, string service = "Nexus Mods")
        {
            Interlocked.Increment(ref Attempts);
            if (NoNet) throw new Exception("no network in this run (--no-net)");
            bool nexus = allow == null;                                      // (the 429 back-off is Nexus' own - another host never blocks it)
            if (allow == null) allow = Allowed;
            if (nexus) lock (gate) if (DateTime.UtcNow < busyUntil) throw new Busy((int)(busyUntil - DateTime.UtcNow).TotalSeconds + 1, service);
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) throw new Exception("bad address");
            for (int hop = 0; hop <= MAX_HOPS; hop++)
            {
                if (!allow(u)) throw new Exception("refused: " + u.Scheme + "://" + u.Authority + " is not an allowed address (https only)");
                var req = (HttpWebRequest)WebRequest.Create(u);
                req.AllowAutoRedirect = false;
                req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                req.UserAgent = APP_NAME.Replace(" ", "") + "/" + AppVersion;
                req.Headers["Application-Name"] = APP_NAME;
                req.Headers["Application-Version"] = AppVersion;
                req.UseDefaultCredentials = false;
                req.CookieContainer = null;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                var proxy = WebRequest.GetSystemWebProxy(); proxy.Credentials = null; req.Proxy = proxy;   // (never the user's Windows login)
                if (postJson != null && hop == 0)
                {
                    req.Method = "POST"; req.ContentType = "application/json"; req.Accept = "application/json";
                    var body = Encoding.UTF8.GetBytes(postJson);
                    req.ContentLength = body.Length;
                    using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                else if (postJson != null) throw new Exception("refused: the API redirected a request");
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException we)
                {
                    resp = we.Response as HttpWebResponse;
                    if (resp == null) throw new Exception(we.Status == WebExceptionStatus.Timeout ? service + " did not answer in time" : "no connection to " + service);
                }
                using (resp)
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        string loc = resp.Headers["Location"];
                        if (string.IsNullOrEmpty(loc) || !Uri.TryCreate(u, loc, out u)) throw new Exception("bad redirect");
                        continue;                                                   // (checked again at the top)
                    }
                    if (code == 429)
                    {
                        int wait; if (!int.TryParse(resp.Headers["Retry-After"] ?? "", out wait) || wait < 1) wait = 60;
                        wait = Math.Min(wait, 3600);
                        if (nexus) lock (gate) busyUntil = DateTime.UtcNow.AddSeconds(wait);
                        throw new Busy(wait, service);
                    }
                    if (code != 200) throw new Exception(service + " answered " + code);
                    if (resp.ContentLength > cap) throw new Exception("answer too large");
                    using (var s = resp.GetResponseStream()) using (var ms = new MemoryStream())
                    {
                        var buf = new byte[1 << 15]; int n;
                        var clock = Stopwatch.StartNew();                           // (ReadWriteTimeout is per read - a trickling answer is cut here)
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (clock.ElapsedMilliseconds > 60000) throw new Exception(service + " answered too slowly");
                            if (ms.Length + n > cap) throw new Exception("answer too large");
                            ms.Write(buf, 0, n);
                        }
                        return ms.ToArray();
                    }
                }
            }
            throw new Exception("too many redirects");
        }
    }

    // ---------------------------------------------------------------------------------------------------- one mod of the list
    class NexusMod
    {
        public int Id;
        public string Name = "", Summary = "", Author = "", Version = "", Category = "", Thumb = "", Picture = "";
        public long Downloads, Endorsements;
        public DateTime Updated;
        public bool Adult;
    }

    partial class Launcher
    {
        const string NEXUS_GAME = "foxhole";
        const string NEXUS_API = "https://api.nexusmods.com/v2/graphql";
        // (properties, not static fields: a field initializer reading another file's field depends on the compiler's file order - review)
        static string BrowseDir { get { return Path.Combine(ModsRoot, "browse"); } }
        static string ThumbDir { get { return Path.Combine(BrowseDir, "thumbs"); } }
        static readonly TimeSpan LIST_TTL = TimeSpan.FromHours(1), THUMB_TTL = TimeSpan.FromDays(7);
        public static readonly string[] BrowseCategories =
            { "Graphics & lighting", "UI & HUD", "Sounds", "Uniforms & soldiers", "Vehicles", "Weapons", "Maps & terrain", "Settings tweaks", "Other" };
        // (order = priority: the first category with a keyword in the NAME wins, then the same over the summary)
        static readonly string[][] CategoryWords = {
            new[] { "map icon", "icons?", "ui", "hud", "interface", "menus?", "fonts?", "crosshairs?", "minimap", "markers?" },
            new[] { "lighting", "light", "graphics?", "reshade", "shadows?", "fog", "colou?rs?", "visuals?", "sky", "bloom", "sharpen\\w*", "night", "weather", "clouds?" },
            new[] { "sound\\w*", "audio", "music", "voices?", "sfx", "soundtrack" },
            new[] { "uniforms?", "soldiers?", "characters?", "helmets?", "outfits?", "infantry", "faces?", "gear" },
            new[] { "vehicles?", "tanks?", "trucks?", "ships?", "boats?", "planes?", "aircraft", "trains?", "cars?", "armou?r" },
            new[] { "weapons?", "rifles?", "guns?", "pistols?", "smgs?", "mgs?", "artillery", "grenades?", "launchers?" },
            new[] { "maps?", "terrain", "foliage", "grass", "trees?", "landscape", "hexe?s?", "roads?" },
            new[] { "ini", "config\\w*", "scalability", "fps", "performance", "settings?", "tweaks?", "potato" },
        };
        static readonly int[] CategoryOrder = { 1, 0, 2, 3, 4, 5, 6, 7 };   // (CategoryWords index -> BrowseCategories index)

        // ---------------------------------------------------------------------------------------------------- the list
        // all Foxhole mods (adult ones only when asked), from the cache when it is under an hour old (or when Nexus cannot be reached)
        // -> (mods, a line about where they came from)
        static DateTime lastListFetch = DateTime.MinValue;
        static List<NexusMod> NexusList(bool adult, bool force, out string note)
        {
            string cache = Path.Combine(BrowseDir, "list-" + (adult ? 1 : 0) + ".json");
            var ser = new JavaScriptSerializer { MaxJsonLength = (int)NexusHttp.JSON_MAX * 4 };
            DateTime cached = File.Exists(cache) ? File.GetLastWriteTimeUtc(cache) : DateTime.MinValue;
            if (!force && DateTime.UtcNow - cached < LIST_TTL)
            {
                try { var l = ReadList(ser, cache); note = "from this PC (" + Age(cached) + ")"; return l; } catch { }
            }
            if (DateTime.UtcNow - lastListFetch < TimeSpan.FromSeconds(60))
            {
                if (File.Exists(cache)) { try { var l = ReadList(ser, cache); note = "asked Nexus under a minute ago - the list saved " + Age(cached); return l; } catch { } }
                throw new Exception("asked Nexus Mods under a minute ago - try again in a moment");
            }
            lastListFetch = DateTime.UtcNow;
            try
            {
                var all = new List<object>();
                for (int page = 0, offset = 0; page < 20; page++)                 // (Nexus answers at most 80 per page)
                {
                    string q = "query($o:Int){ mods(filter:{gameDomainName:[{value:\"" + NEXUS_GAME + "\"}]" + (adult ? "" : ", adultContent:[{value:false}]") +
                        "}, count:100, offset:$o, sort:[{downloads:{direction:DESC}}]){ totalCount nodes{ modId name summary author version " +
                        "thumbnailUrl pictureUrl downloads endorsements updatedAt adultContent uploader{name} } } }";
                    string body = ser.Serialize(new Dictionary<string, object> { { "query", q }, { "variables", new Dictionary<string, object> { { "o", offset } } } });
                    var r = ser.DeserializeObject(Encoding.UTF8.GetString(NexusHttp.Send(NEXUS_API, NexusHttp.JSON_MAX, body))) as Dictionary<string, object>;
                    var mods = Dig(r, "data", "mods");
                    if (mods == null) throw new Exception("unexpected answer from Nexus Mods");
                    var nodes = mods.ContainsKey("nodes") ? mods["nodes"] as object[] : null;
                    if (nodes == null || nodes.Length == 0) break;
                    all.AddRange(nodes);
                    offset += nodes.Length;
                    int total = mods.ContainsKey("totalCount") && mods["totalCount"] is int ? (int)mods["totalCount"] : 0;
                    if (total > 0 && all.Count >= total) break;                     // (no total: until an empty page)
                }
                Directory.CreateDirectory(BrowseDir);
                string tmp = cache + ".tmp";
                File.WriteAllText(tmp, ser.Serialize(all));
                if (File.Exists(cache)) File.Delete(cache);
                File.Move(tmp, cache);
                note = "from Nexus Mods (just now)";
                return ReadList(ser, cache);
            }
            catch (Exception ex)
            {
                if (File.Exists(cache))
                {
                    try { var l = ReadList(ser, cache); note = ex.Message + " - showing the list saved " + Age(cached); return l; } catch { }
                }
                throw;
            }
        }
        static string Age(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            return d.TotalMinutes < 2 ? "just now" : d.TotalHours < 2 ? (int)d.TotalMinutes + " min ago" : d.TotalDays < 2 ? (int)d.TotalHours + " h ago" : (int)d.TotalDays + " days ago";
        }
        static Dictionary<string, object> Dig(Dictionary<string, object> d, params string[] keys)
        {
            foreach (var k in keys) { if (d == null || !d.ContainsKey(k)) return null; d = d[k] as Dictionary<string, object>; }
            return d;
        }
        static List<NexusMod> ReadList(JavaScriptSerializer ser, string file)
        {
            var fix = CuratedCategories();
            var r = new List<NexusMod>();
            foreach (var o in (object[])ser.DeserializeObject(File.ReadAllText(file)))
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !(d.ContainsKey("modId") && d["modId"] is int) || (int)d["modId"] <= 0) continue;   // (ids are positive integers only)
                Func<string, string> s = k => d.ContainsKey(k) && d[k] != null ? Clean(d[k].ToString()) : "";
                Func<string, long> n = k => { long v; return d.ContainsKey(k) && d[k] != null && long.TryParse(d[k].ToString(), out v) ? v : 0; };
                var m = new NexusMod { Id = (int)d["modId"], Name = s("name"), Summary = s("summary"), Version = s("version"),
                    Thumb = s("thumbnailUrl"), Picture = s("pictureUrl"), Downloads = n("downloads"), Endorsements = n("endorsements"),
                    Adult = d.ContainsKey("adultContent") && d["adultContent"] is bool && (bool)d["adultContent"] };
                var up = Dig(d, "uploader");
                m.Author = s("author") != "" ? s("author") : up != null && up.ContainsKey("name") && up["name"] != null ? Clean(up["name"].ToString()) : "";
                DateTime t; if (DateTime.TryParse(s("updatedAt"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out t)) m.Updated = t;
                string c; m.Category = fix.TryGetValue(m.Id.ToString(), out c) && Array.IndexOf(BrowseCategories, c) >= 0 ? c : GuessCategory(m);
                r.Add(m);
            }
            return r;
        }
        // server text -> plain text for drawing (no control characters, HTML entities decoded, BBCode tags dropped, length capped)
        static string Clean(string t)
        {
            t = WebUtility.HtmlDecode(t ?? "");
            t = Regex.Replace(t, @"\[/?[a-zA-Z]+(=[^\]]*)?\]", "");
            t = Regex.Replace(t, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F\u200B-\u200F\u202A-\u202E\u2066-\u2069]", "");
            return t.Length > 2000 ? t.Substring(0, 2000) : t.Trim();
        }
        static string GuessCategory(NexusMod m)
        {
            foreach (var text in new[] { m.Name, m.Summary })
                for (int i = 0; i < CategoryWords.Length; i++)
                    foreach (var w in CategoryWords[i])
                        if (Regex.IsMatch(text, @"\b" + w + @"\b", RegexOptions.IgnoreCase)) return BrowseCategories[CategoryOrder[i]];
            return "Other";
        }
        static Dictionary<string, string> CuratedCategories()
        {
            var r = new Dictionary<string, string>();
            try
            {
                string f = Path.Combine(Dir, "nexus_categories.json");
                if (File.Exists(f) && new FileInfo(f).Length < 1 << 20)
                    foreach (var kv in (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f)))
                        if (Regex.IsMatch(kv.Key, @"^[0-9]{1,9}$") && kv.Value is string) r[kv.Key] = (string)kv.Value;
            }
            catch { }
            return r;
        }
        // the mod's page, built from its id only (never a URL from a server)
        static string NexusPage(int id, bool files) { return "https://www.nexusmods.com/" + NEXUS_GAME + "/mods/" + id + (files ? "?tab=files" : ""); }

        // (UI thread) every picture forgotten: queued jobs dropped, ones in flight never saved or shown (the generation moves on)
        static void ThumbInvalidate()
        {
            Interlocked.Increment(ref thumbGen);
            ThumbForgetQueued();
            lock (thumbQueue) { foreach (var kv in thumbArrived) kv.Value.Dispose(); thumbArrived.Clear(); thumbRetry.Clear(); }
            thumbs.Clear();
        }
        static void ClearBrowseCache() { ThumbInvalidate(); DeleteTree(BrowseDir); }
        // thumbnails over 7 days old are deleted (Nexus' terms: no lasting copies)
        static void PurgeThumbs()
        {
            try
            {
                if (!Directory.Exists(ThumbDir)) return;
                foreach (var f in Directory.GetFiles(ThumbDir, "*.png"))
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > THUMB_TTL) try { File.Delete(f); } catch { }
            }
            catch { }
        }

        // ---------------------------------------------------------------------------------------------------- thumbnails
        // Thumb(mod, w, h): the scaled picture, or null while it loads / when there is none (requests queued for the worker thread)
        // (2026-11-18) also the Installed page's rows (ThumbFor with the mod id and its picture address from the update check / the Browse
        // list): net false (the public edition before Nexus' approval) = only a picture already on this PC - the exact one or the Browse
        // cards' copy of the same picture scaled again - never a download.
        static readonly ThumbCache thumbs = new ThumbCache(160);
        class ThumbJob { public string Key, Url; public int Gen; public bool Net; }
        class ThumbNoNet : Exception { }
        static readonly Queue<ThumbJob> thumbQueue = new Queue<ThumbJob>();
        static readonly HashSet<string> thumbPending = new HashSet<string>();
        static readonly Dictionary<string, DateTime> thumbRetry = new Dictionary<string, DateTime>();   // (no picture: never again / network: in 3 min)
        static int thumbGen;
        static readonly List<KeyValuePair<string, Bitmap>> thumbArrived = new List<KeyValuePair<string, Bitmap>>();   // (worker -> UI thread)
        static Thread thumbWorker;
        public static Action ThumbArrived;                                  // (the window redraws)
        static bool thumbNoFetch;                                           // (--shot: pictures only from the memory cache, nothing queued)

        static string ThumbKey(string url, int w, int h)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").Substring(0, 16).ToLowerInvariant() + "-" + w + "x" + h;
        }
        static Bitmap Thumb(NexusMod m, bool big, int w, int h)
        {
            return ThumbFor(m.Id, big && m.Picture != "" ? m.Picture : m.Thumb, w, h, true);
        }
        // a picture address is usable: https on Nexus' image hosts only (the allow-list)
        static bool ThumbUrlOk(string url)
        {
            Uri u;
            return !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) && NexusHttp.Allowed(u);
        }
        static Bitmap ThumbFor(int id, string url, int w, int h, bool net)
        {
            if (id <= 0 || !ThumbUrlOk(url)) return null;
            string key = id + "-" + ThumbKey(url, w, h);
            lock (thumbQueue)                                               // (the cache is touched on the UI thread only - it disposes)
            {
                foreach (var kv in thumbArrived) thumbs.Put(kv.Key, kv.Value);
                thumbArrived.Clear();
            }
            var b = thumbs.Get(key);
            if (b != null || thumbNoFetch) return b;
            lock (thumbQueue)
            {
                DateTime until;
                if ((thumbRetry.TryGetValue(key, out until) && DateTime.UtcNow < until) || thumbPending.Contains(key)) return null;
                thumbPending.Add(key);
                thumbQueue.Enqueue(new ThumbJob { Key = key, Url = url, Gen = thumbGen, Net = net });
                if (thumbWorker == null)
                {
                    thumbWorker = new Thread(ThumbLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    thumbWorker.SetApartmentState(ApartmentState.STA);         // (WIC through WPF)
                    thumbWorker.Start();
                }
                Monitor.Pulse(thumbQueue);
            }
            return null;
        }
        // (UI thread, after ThumbFor gave null) is that picture still on its way - the row shows a placeholder instead of the icon
        static bool ThumbWaiting(int id, string url, int w, int h)
        {
            if (id <= 0 || !ThumbUrlOk(url)) return false;
            string key = id + "-" + ThumbKey(url, w, h);
            lock (thumbQueue) return thumbPending.Contains(key) || thumbNoFetch;
        }
        // (the visible page changed: drop requests for cards no longer shown - no prefetch)
        static void ThumbForgetQueued() { lock (thumbQueue) { foreach (var j in thumbQueue) thumbPending.Remove(j.Key); thumbQueue.Clear(); } }

        static void ThumbLoop()
        {
            while (true)
            {
                ThumbJob job;
                lock (thumbQueue)
                {
                    while (thumbQueue.Count == 0) Monitor.Wait(thumbQueue);
                    job = thumbQueue.Dequeue();
                }
                Bitmap bmp = null; bool network = false;
                try
                {
                    var wh = Regex.Match(job.Key, @"-(\d+)x(\d+)$");
                    bmp = LoadThumb(job.Key, job.Url, int.Parse(wh.Groups[1].Value), int.Parse(wh.Groups[2].Value), job.Gen, job.Net);
                }
                catch { network = true; }                                   // (a network failure, or none allowed: asked again in 3 min)
                lock (thumbQueue)
                {
                    thumbPending.Remove(job.Key);
                    if (job.Gen != thumbGen) { if (bmp != null) bmp.Dispose(); bmp = null; }      // (forgotten meanwhile)
                    else if (bmp == null) thumbRetry[job.Key] = network ? DateTime.UtcNow.AddMinutes(3) : DateTime.MaxValue;
                    else thumbArrived.Add(new KeyValuePair<string, Bitmap>(job.Key, bmp));
                }
                if (bmp != null) { var a = ThumbArrived; if (a != null) try { a(); } catch { } }
            }
        }
        // the same picture kept in another size ("<id>-<url hash>-<w>x<h>.png" - the Browse cards' copy), under 7 days old, or null
        static string ThumbCachedOtherSize(string key)
        {
            var m = Regex.Match(key, @"^(\d+-[0-9a-f]{16}-)\d+x\d+$");
            if (!m.Success || !Directory.Exists(ThumbDir)) return null;
            string best = null; DateTime bt = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(ThumbDir, m.Groups[1].Value + "*.png"))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (DateTime.UtcNow - t < THUMB_TTL && t > bt) { best = f; bt = t; }
            }
            return best;
        }
        // where a picture would come from now: "cached" (this PC), "fetch" (Nexus) or "blocked" (not on this PC and no network allowed)
        static string ThumbPlan(string key, bool net)
        {
            string file = Path.Combine(ThumbDir, key + ".png");
            if ((File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < THUMB_TTL) || ThumbCachedOtherSize(key) != null) return "cached";
            return net ? "fetch" : "blocked";
        }
        // cache file (under 7 days), else the same picture in another size, else a download (net only) -> checked -> decoded -> scaled (only
        // the scaled picture is kept, as a PNG); nothing on this PC and net false -> ThumbNoNet
        static Bitmap LoadThumb(string key, string url, int w, int h, int gen = -1, bool net = true)
        {
            string file = Path.Combine(ThumbDir, key + ".png");
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < THUMB_TTL)
            {
                try { var data = File.ReadAllBytes(file); if (ImageKind(data) == "png") return Decode(data, w, h, false); } catch { }
            }
            string other = ThumbCachedOtherSize(key);
            if (other != null)
            {
                try
                {
                    var data = File.ReadAllBytes(other);
                    var ob = ImageKind(data) == "png" ? Decode(data, w, h, true) : null;
                    if (ob != null)
                    {
                        // (saved with the original's time: the 7-day limit counts from the download, not from this copy)
                        if (gen < 0 || gen == thumbGen) try { ob.Save(file, System.Drawing.Imaging.ImageFormat.Png); File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(other)); } catch { }
                        return ob;
                    }
                }
                catch { }
            }
            if (!net) throw new ThumbNoNet();
            var raw = NexusHttp.Send(url, NexusHttp.IMAGE_MAX, null);
            var b = Decode(raw, w, h, true);
            if (b == null) return null;
            if (gen < 0 || gen == thumbGen) try { Directory.CreateDirectory(ThumbDir); b.Save(file, System.Drawing.Imaging.ImageFormat.Png); } catch { }
            return b;
        }
        static string ImageKind(byte[] d)
        {
            if (d.Length > 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "png";
            if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpeg";
            if (d.Length > 30 && Encoding.ASCII.GetString(d, 0, 4) == "RIFF" && Encoding.ASCII.GetString(d, 8, 4) == "WEBP") return "webp";
            return "";
        }
        // the picture's size from its header (before any decoding) -> (w, h), 0 when unknown
        static Size ImageHeaderSize(byte[] d)
        {
            string k = ImageKind(d);
            if (k == "png") return new Size((d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19], (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23]);
            if (k == "webp")                                                // (VP8X canvas / VP8 lossy frame / VP8L lossless header)
            {
                string c = Encoding.ASCII.GetString(d, 12, 4);
                if (c == "VP8X") return new Size(1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16));
                if (c == "VP8 " && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A) return new Size((d[26] | d[27] << 8) & 0x3FFF, (d[28] | d[29] << 8) & 0x3FFF);
                if (c == "VP8L" && d[20] == 0x2F) { uint b = (uint)(d[21] | d[22] << 8 | d[23] << 16 | d[24] << 24); return new Size(1 + (int)(b & 0x3FFF), 1 + (int)((b >> 14) & 0x3FFF)); }
                return Size.Empty;
            }
            if (k == "jpeg")
            {
                int i = 2;
                while (i + 9 < d.Length)
                {
                    if (d[i] != 0xFF) { i++; continue; }
                    byte mk = d[i + 1];
                    if (mk == 0xD8 || mk == 0x01 || (mk >= 0xD0 && mk <= 0xD7) || mk == 0xFF) { i++; continue; }
                    int len = (d[i + 2] << 8) | d[i + 3];
                    if (mk >= 0xC0 && mk <= 0xCF && mk != 0xC4 && mk != 0xC8 && mk != 0xCC) return new Size((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                    if (len < 2) break;
                    i += 2 + len;
                }
            }
            return Size.Empty;
        }
        const int IMAGE_SIDE_MAX = 4096;
        static Bitmap Decode(byte[] data, int w, int h, bool check)
        {
            string kind = ImageKind(data);
            if (kind == "") return null;
            if (check)
            {
                var sz = ImageHeaderSize(data);
                if (sz.Width <= 0 || sz.Height <= 0 || sz.Width > IMAGE_SIDE_MAX || sz.Height > IMAGE_SIDE_MAX) return null;
            }
            if (kind == "webp") return DecodeWic(data, w, h);
            using (var ms = new MemoryStream(data))
            using (var img = Image.FromStream(ms, false, true))
                return Cover(img, w, h);
        }
        // WebP (Nexus' image server converts every picture to it): Windows' own imaging codec (WIC, through WPF's classes); without the
        // codec (very old Windows 10) -> null = the placeholder
        static Bitmap DecodeWic(byte[] data, int w, int h)
        {
            using (var ms = new MemoryStream(data))
            {
                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (dec.Frames.Count == 0) return null;
                var f = dec.Frames[0];
                if (f.PixelWidth <= 0 || f.PixelHeight <= 0 || f.PixelWidth > IMAGE_SIDE_MAX || f.PixelHeight > IMAGE_SIDE_MAX) return null;
                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(f, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int stride = conv.PixelWidth * 4;
                var px = new byte[stride * conv.PixelHeight];
                conv.CopyPixels(px, stride, 0);
                using (var full = new Bitmap(conv.PixelWidth, conv.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    var bd = full.LockBits(new Rectangle(0, 0, full.Width, full.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, full.PixelFormat);
                    for (int y = 0; y < full.Height; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * stride, bd.Scan0 + y * bd.Stride, stride);
                    full.UnlockBits(bd);
                    return Cover(full, w, h);
                }
            }
        }
        // the picture scaled to fill w x h (the overhang cut in the middle)
        static Bitmap Cover(Image img, int w, int h)
        {
            {
                if (img.Width > IMAGE_SIDE_MAX || img.Height > IMAGE_SIDE_MAX) return null;
                var b = new Bitmap(w, h);
                try
                {
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // (cover: fill the card, cut the overhang in the middle)
                    float s = Math.Max((float)w / img.Width, (float)h / img.Height);
                    float dw = img.Width * s, dh = img.Height * s;
                    g.DrawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
                }
                }
                catch { b.Dispose(); throw; }
                return b;
            }
        }

        // ---------------------------------------------------------------------------------------------------- command line
        static bool NexusCommandLine(string[] args)
        {
            var a = new List<string>(args);
            if (!a.Exists(x => x == "--nexus-list" || x == "--nexus-get" || x == "--nexus-thumb")) return false;
            AttachConsole(-1);
            Headless = true;
            NexusHttp.EnableTls();
            Func<string, string> opt = k => { int i = a.IndexOf(k); return i >= 0 && i + 1 < a.Count ? a[i + 1] : ""; };
            Environment.ExitCode = 0;
            NexusHttp.NoNet = a.Contains("--no-net");
            try
            {
                // (review N1) the public edition talks to Nexus only once browse_enabled is on - its command line too
                if (!BrowseAllowed()) throw new Exception("Nexus requests are off in this edition until browse_enabled is on");
                if (a.Contains("--nexus-get"))
                {
                    var b = NexusHttp.Send(opt("--nexus-get"), NexusHttp.JSON_MAX, null);
                    Console.WriteLine("OK " + b.Length + " bytes");
                }
                if (a.Contains("--nexus-list") || a.Contains("--nexus-thumb"))
                {
                    string note;
                    var l = NexusList(a.Contains("--adult"), a.Contains("--force"), out note);
                    Console.WriteLine(l.Count + " mods, " + note);
                    var counts = new Dictionary<string, int>();
                    foreach (var m in l) { int c; counts.TryGetValue(m.Category, out c); counts[m.Category] = c + 1; }
                    foreach (var c in BrowseCategories) Console.WriteLine("  " + c + ": " + (counts.ContainsKey(c) ? counts[c] : 0));
                    if (a.Contains("--nexus-list"))
                        for (int i = 0; i < Math.Min(l.Count, 15); i++)
                            Console.WriteLine("  " + l[i].Id + "  " + l[i].Downloads + "  [" + l[i].Category + "]  " + l[i].Name + (l[i].Adult ? "  (adult)" : ""));
                    if (a.Contains("--nexus-thumb"))
                    {
                        var m = l.Find(x => x.Id.ToString() == opt("--nexus-thumb"));
                        if (m == null) throw new Exception("no such mod in the list");
                        var key = m.Id + "-" + ThumbKey(m.Thumb, 216, 122);
                        var b = LoadThumb(key, m.Thumb, 216, 122);
                        Console.WriteLine(b == null ? "no picture (refused or not JPEG / PNG / WebP)" : "thumb " + b.Width + "x" + b.Height + " -> " + Path.Combine(ThumbDir, key + ".png"));
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Environment.ExitCode = 1; }
            Console.WriteLine("network requests: " + NexusHttp.Attempts);
            return true;
        }
    }

    // a bounded picture cache: the least recently used picture is dropped AND disposed (GDI handles are scarce)
    class ThumbCache
    {
        readonly int cap;
        readonly LinkedList<KeyValuePair<string, Bitmap>> order = new LinkedList<KeyValuePair<string, Bitmap>>();
        readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Bitmap>>> map = new Dictionary<string, LinkedListNode<KeyValuePair<string, Bitmap>>>();
        public ThumbCache(int cap) { this.cap = cap; }
        public Bitmap Get(string k)
        {
            lock (map)
            {
                LinkedListNode<KeyValuePair<string, Bitmap>> n;
                if (!map.TryGetValue(k, out n)) return null;
                order.Remove(n); order.AddFirst(n);
                return n.Value.Value;
            }
        }
        public void Put(string k, Bitmap b)
        {
            lock (map)
            {
                if (map.ContainsKey(k)) { b.Dispose(); return; }
                map[k] = order.AddFirst(new KeyValuePair<string, Bitmap>(k, b));
                while (order.Count > cap)
                {
                    var last = order.Last; order.RemoveLast(); map.Remove(last.Value.Key);
                    try { last.Value.Value.Dispose(); } catch { }              // (UI thread only - nothing is drawing it now)
                }
            }
        }
        public void Clear() { lock (map) { foreach (var n in order) try { n.Value.Dispose(); } catch { } order.Clear(); map.Clear(); } }
    }
}
