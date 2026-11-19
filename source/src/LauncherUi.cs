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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher : Form
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
                                         uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll")] static extern int SetTextCharacterExtra(IntPtr hdc, int extra);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string s, int n, ref RECT r, uint fmt);
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int n, out SIZE sz);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const uint DT_LEFT = 0, DT_CENTER = 1, DT_RIGHT = 2, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_END_ELLIPSIS = 0x8000, DT_NOPREFIX = 0x800;
        const string FACE = "Segoe UI";
        // (a property: as a field its initializer read PublicEdition of another file - right only in one compile order)
        static string headFace;
        static string HEAD_FACE { get { return headFace ?? (headFace = PublicEdition ? (FontInstalled("Bahnschrift") ? "Bahnschrift" : "Segoe UI Semibold") : "Renner*"); } }
        bool txtHead;                                        // (TxtH - LauncherShell.cs)
        static bool FontInstalled(string f)
        {
            try { using (var c = new System.Drawing.Text.InstalledFontCollection()) foreach (var x in c.Families) if (x.Name == f) return true; }
            catch { }
            return false;
        }

        static readonly Color Ink = Color.FromArgb(236, 234, 228), InkDim = Color.FromArgb(163, 160, 151), InkOff = Color.FromArgb(120, 118, 111);
        readonly Dictionary<string, IntPtr> fonts = new Dictionary<string, IntPtr>();
        Image wall, logo, header, warden, colonial;
        Bitmap frame;
        string statusText = "Starting...", releaseText = "", hover = "", shotState = "";
        int progress = -1;                                   // -1 = no bar
        bool buttonsOn = true, details;
        readonly List<string> logLines = new List<string>();
        readonly Dictionary<string, Rectangle> hits = new Dictionary<string, Rectangle>();

        [STAThread]
        static void Main(string[] args)
        {
            NexusHttp.EnableTls();                                        // TLS 1.2 (+ 1.3 where Windows has it) - LauncherNexus.cs
            Application.EnableVisualStyles();
            if (ModsCommandLine(args)) return;                            // (cosmetic mod commands without a window - LauncherMods.cs)
            if (NexusCommandLine(args)) return;
            if (ModUpdatesCommandLine(args)) return;                      // (--mod-updates-check <ids> / --mod-updates - LauncherModUpdates.cs)
            if (SandboxCommandLine(args)) return;                         // (--sandbox-revert / -livecheck / -changed / -leftovers / -trace - LauncherSandbox.cs)
            if (MachineCommandLine(args)) return;                         // (--machine <file>: what the launcher reads of this PC - LauncherMachine.cs)
            // (tests) the PLAY pill's mode from the options and what this edition lets be clicked: --play-ui [id...] -> "mode: <name>" + "click <id>: yes|no"
            if (args.Length >= 1 && args[0] == "--play-ui")
            {
                AttachConsole(-1);
                Console.WriteLine("mode: " + PlayModeName());
                for (int i = 1; i < args.Length; i++) Console.WriteLine("click " + args[i] + ": " + (ClickAllowed(args[i]) ? "yes" : "no"));
                return;
            }
            if (args.Length == 0 && !SingleInstance()) return;           // (a second start: the open window comes to the front)
            var l = new Launcher();
            if (args.Length >= 2 && args[0] == "--shot") { l.Shot(args[1], args.Length > 2 ? args[2] : "idle"); return; }
            Application.Run(l);
        }

        static System.Threading.Mutex singleInstance;
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        static bool SingleInstance()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\').ToLowerInvariant();
            string key;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                key = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir))).Replace("-", "").Substring(0, 16);
            bool created;
            try { singleInstance = new System.Threading.Mutex(true, "Local\\FoxholeModLauncher-" + key, out created); }
            catch { return true; }                                        // (no mutex: run as before)
            if (created) return true;
            try
            {
                var me = System.Diagnostics.Process.GetCurrentProcess();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    string path = "";
                    try { path = p.MainModule.FileName; } catch { }
                    if (path != "" && !string.Equals(Path.GetDirectoryName(path).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9);   // (SW_RESTORE)
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
            catch { }
            return false;
        }

        Launcher()
        {
            Text = PublicEdition ? PUBLIC_NAME : "Foxhole Mod Launcher";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            AllowDrop = true;                                            // (a mod file dropped on the window - Installed)
            DoubleBuffered = true;
            BackColor = Color.Black;
            LoadAssets();
            FitToScreen();                                               // (shrunk on small / scaled screens - LauncherShell.cs)
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            MouseMove += (s, e) => { string h = HitAt(e.Location); if (h != hover) { hover = h; Cursor = h != "" ? Cursors.Hand : Cursors.Default; Redraw(); } };
            MouseLeave += (s, e) => { if (hover != "") { hover = ""; Redraw(); } };
            MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                string h = HitAt(e.Location);
                var fp = ToFrame(e.Location);
                if (h == "" && (fp.Y < HDR || (fp.X < SIDE && fp.Y < 96))) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); return; }   // drag by the header / logo
                Click_(h);
            };
            Shown += (s, e) =>
            {
                // (the last manifest's server address at once - the update fetches the new one)
                if (PublicEdition)
                {
                    new System.Threading.Thread(CheckMachine) { IsBackground = true }.Start();
                    CountInstalled();
                    ModUpdatesCheck(false);             // (only once Nexus approved the app: browse_enabled - LauncherModUpdates.cs)
                    RefreshPageFacts();
                    OfflineStartup();
                    return;
                }
            };
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape || browseOpen) return;          // (BROWSE has its own Esc chain - LauncherBrowse.cs)
                if (page != "home") Go("home");
            };
            // the mouse wheel turns the Servers / Installed pages (BROWSE has its own - LauncherBrowse.cs)
            MouseWheel += (s, e) =>
            {
                int by = e.Delta < 0 ? 1 : -1;
                if (page == "installed")
                {
                    int pages = Math.Max(1, (cosCache.Count + INSTALLED_ROWS - 1) / INSTALLED_ROWS);
                    int to = Math.Max(0, Math.Min(pages - 1, modsPage + by));
                    if (to != modsPage) { modsPage = to; Redraw(); }
                }
            };
            // a .pak / .zip / .7z dropped on the window: added like ADD FILE (checked first; switched on in Installed). (1.1.1) the public app takes
            // no .7z: the others are added, then the line asks for the .pak (LauncherMods.cs NO_7Z)
            DragEnter += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                if (!buttonsOn || offlineBusy) return;                    // (not while the game is being changed)
                var take = new List<string>();
                bool sevenZ = false;
                foreach (var f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".7z") { sevenZ = true; continue; }
                    if (ext == ".pak" || ext == ".zip" || ext == ".7z") take.Add(f);
                }
                if (take.Count == 0 && !sevenZ) return;
                ModsRun(() =>
                {
                    foreach (var f in take) CosAdd(f, false);
                    if (sevenZ) throw new Exception(NO_7Z);
                });
                Go("installed");
            };
        }

        // ---- state from the update / play code (any thread)
        void Ui(Action a) { if (Headless) return; if (IsHandleCreated && InvokeRequired) BeginInvoke(a); else a(); }
        void Say(string m)
        {
            try
            {
                string lp = Path.Combine(DataDir, "launcher.log");                   // (public: in app data)
                if (File.Exists(lp) && new FileInfo(lp).Length > 1048576) File.Delete(lp);
                File.AppendAllText(lp, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + m + Environment.NewLine);
            }
            catch { }
            if (Headless) { Console.WriteLine(m); return; }
            Ui(() =>
            {
                logLines.Add(DateTime.Now.ToString("HH:mm:ss  ") + m);
                if (logLines.Count > 400) logLines.RemoveRange(0, 100);
                if (details) Redraw();
            });
        }
        void Status(string m) { if (Headless && m != statusText) { statusText = m; Console.WriteLine("  " + m); } Ui(() => { statusText = m; Redraw(); }); }
        void Buttons(bool on) { Ui(() => { buttonsOn = on; if (on) progress = -1; Redraw(); }); }
        void Progress(int pct) { Ui(() => { if (pct != progress) { progress = pct; Redraw(); } }); }
        void Release(string commit, string built, string subject)
        {
            string c = commit.Length > 7 ? commit.Substring(0, 7) : commit;
            DateTime d;
            string when = DateTime.TryParse(built, out d) ? d.ToString("dd MMM yyyy").ToUpperInvariant() : built;
            Ui(() => { releaseText = ("RELEASE " + c + "   " + when).Trim(); Redraw(); });
        }

        static readonly string[] PublicClicks = { "close", "min", "offline", "restore", "set:restore", "pubupdate", "hdr:offline", "opt:lowspec" };
        // ("mod:" covers Installed in both editions: on: / rm: / info: / add / prev / next and, 2026-11-18, the mod updates' checkupdates /
        //  update:<id> / rollback:<id> - LauncherModUpdates.cs; mod:catalog is private only)
        static readonly string[] PublicPrefixes = { "nav:", "side:", "br:", "mod:" };
        static readonly string[] PublicOnlyClicks = { "offline", "hdr:offline", "restore", "set:restore", "pubupdate" };
        internal static bool ClickAllowed(string h)
        {
            if (h == "") return false;
            if (!PublicEdition) return Array.IndexOf(PublicOnlyClicks, h) < 0;
            if (Array.IndexOf(PublicClicks, h) >= 0) return true;
            if (h == "mod:catalog") return false;
            foreach (var x in PublicPrefixes) if (h.StartsWith(x)) return true;
            return false;
        }
        // the PLAY pill's chosen mode by its option value (LauncherShell.cs PlaySplit): public "official" | "offline" (the public release has
        // no mode list - it only plays the live war), private "server" | "offline"; anything else = the default
        static string PlayModeName() { return Str(Options(), "play_mode") == "offline" ? "offline" : PublicEdition ? "official" : "server"; }
        void Click_(string h)
        {
            if (!ClickAllowed(h)) return;
            if (h.StartsWith("hdr:")) h = h.Substring(4);                 // (the header's primary action = the page's own)
            if (h == "close") Close();
            else if (h == "min") WindowState = FormWindowState.Minimized;
            else if (h.StartsWith("nav:")) Go(h.Substring(4));
            else if (h.StartsWith("side:")) Go(h.Substring(5));
            else if (h.StartsWith("br:")) BrowseClick(h.Substring(3));    // (LauncherBrowse.cs)
            else if (h == "offline") OfficialPlayClick();
            else if (h == "restore" || h == "set:restore") OfflineRestoreClick();
            else if (h == "pubupdate") OfflineCheckUpdate();
            else if (h.StartsWith("mod:")) ModsClick(h.Substring(4));
            else if (h.StartsWith("opt:")) ToggleOption(h.Substring(4));
        }

        string HitAt(Point p)
        {
            p = ToFrame(p);                                               // (the window may show the frame shrunk - LauncherShell.cs FitToScreen)
            foreach (var kv in hits) if (kv.Value.Contains(p)) return kv.Key;
            return "";
        }

        // ---- assets (embedded resources; a plain dark window when one is missing)
        static Stream Res(string name) { return typeof(Launcher).Assembly.GetManifestResourceStream(name); }
        static Image Img(string name) { var s = Res(name); return s == null ? null : Image.FromStream(s); }
        void LoadAssets()
        {
            var walls = new List<string>();
            foreach (var n in typeof(Launcher).Assembly.GetManifestResourceNames()) if (n.StartsWith("wall")) walls.Add(n);
            if (walls.Count > 0) wall = Img(walls[new Random().Next(walls.Count)]);
            logo = Img("logo.png"); header = Img("header.png"); warden = Img("warden.png"); colonial = Img("colonial.png");
        }
        IntPtr FontOf(int px, bool bold)
        {
            string face = txtHead ? HEAD_FACE : FACE;
            string k = face + px + (bold ? "b" : "");
            IntPtr f;
            if (!fonts.TryGetValue(k, out f)) fonts[k] = f = CreateFontW(-px, 0, 0, 0, bold ? (txtHead && PublicEdition ? 600 : 700) : 400, 0, 0, 0, 1, 0, 0, 4, 0, face);
            return f;
        }

        // GDI text with a soft shadow (the art behind is busy)
        void Txt(Graphics g, string s, int px, bool bold, Color c, Rectangle r, uint fmt, int spacing, bool shadow)
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                IntPtr old = SelectObject(hdc, FontOf(px, bold));
                SetBkMode(hdc, 1);
                SetTextCharacterExtra(hdc, spacing);
                fmt |= DT_NOPREFIX;
                if ((fmt & (DT_CENTER | DT_RIGHT)) != 0 && (fmt & DT_SINGLELINE) != 0 && spacing != 0)
                {   // (DrawText ignores the letter spacing when it centres / right-aligns - GetTextExtentPoint32 counts it)
                    SIZE sz; GetTextExtentPoint32W(hdc, s, s.Length, out sz);
                    int w = sz.cx - spacing;
                    int x = (fmt & DT_CENTER) != 0 ? r.X + (r.Width - w) / 2 : r.Right - w;
                    r = new Rectangle(x, r.Y, r.Right - x + 40, r.Height);
                    fmt &= ~(DT_CENTER | DT_RIGHT | DT_END_ELLIPSIS);
                }
                if (shadow)
                {
                    SetTextColor(hdc, 0);
                    var rs = new RECT { L = r.Left + 1, T = r.Top + 2, R = r.Right + 1, B = r.Bottom + 2 };
                    DrawTextW(hdc, s, -1, ref rs, fmt);
                }
                SetTextColor(hdc, c.R | (c.G << 8) | (c.B << 16));
                var rc = new RECT { L = r.Left, T = r.Top, R = r.Right, B = r.Bottom };
                DrawTextW(hdc, s, -1, ref rc, fmt);
                SelectObject(hdc, old);
            }
            finally { g.ReleaseHdc(hdc); }
        }

        static void Panel(Graphics g, Rectangle r, int alpha)
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, 8, 8, 8))) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(46, 255, 255, 255))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }
        void Strip(Graphics g, Rectangle r, string title)
        {
            if (header != null)
            {
                var ia = new ImageAttributes();
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.85f });
                g.DrawImage(header, r, 0, 0, header.Width, header.Height, GraphicsUnit.Pixel, ia);
            }
            else using (var b = new SolidBrush(Color.FromArgb(200, 60, 60, 58))) g.FillRectangle(b, r);
            Txt(g, title, 13, true, Ink, new Rectangle(r.X + 14, r.Y, r.Width - 28, r.Height), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 3, false);
        }

        void Entry(Graphics g, string id, string text, int y, int h, int px)
        {
            var r = new Rectangle(40, y, 380, h);
            hits[id] = r;
            bool on = buttonsOn || id == "details" || id == "servers" || id == "report";   // (a problem can be reported while the update runs)
            bool hov = on && hover == id;
            if (hov)
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(185, 10, 10, 10), Color.FromArgb(0, 10, 10, 10), 0f)) g.FillRectangle(b, r);
                using (var b = new SolidBrush(Ink)) g.FillRectangle(b, r.X, r.Y + 4, 3, r.Height - 8);
            }
            Txt(g, text, px, true, !on ? InkOff : hov ? Color.White : Ink, new Rectangle(r.X + 16, r.Y, r.Width - 16, r.Height),
                DT_LEFT | DT_VCENTER | DT_SINGLELINE, px >= 30 ? 3 : 2, true);
        }

        void Render(Graphics g)
        {
            hits.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            RenderShell(g);                                               // (LauncherShell.cs)
        }

        void Redraw()
        {
            if (Headless) return;
            if (frame == null) frame = new Bitmap(W, H, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(frame)) Render(g);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (frame == null) Redraw();
            if (uiScale == 1f) e.Graphics.DrawImageUnscaled(frame, 0, 0);
            else
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(frame, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        void Shot(string png, string what)
        {
            shotState = what;
            address = "";
            Release("1212c18aa", "2026-11-12 11:25:40", "");
            statusText = "Up to date - press Play.";
            foreach (var m in new[] { "release 1212c18aa0 (2026-11-12 11:25:40)", "everything is up to date" }) logLines.Add("11:25:41  " + m);
            if (what == "download")
            {
                buttonsOn = false; progress = 46; statusText = "Downloading 3 / 7";
                logLines.Add("11:25:42  7 file(s) to download, 512 MB"); logLines.Add("11:25:44  updated 1 file");
            }
            if (what == "details") { details = true; hover = "details"; }
            if (what == "hover") hover = "play";
            if (what == "busy") { buttonsOn = false; statusText = "Starting the live war through Steam..."; }
            sideText = "Up to date"; sideKind = 1;
            if (what == "options-low")
            {
                optionsOpen = true; hover = "opt:lowspec";
                machine = new MachineFacts { RamGb = 8, GpuKnown = true, Integrated = true, Gpu = "Intel(R) UHD Graphics 620", VramGb = 0.1, Cores = 8 };
                machineClass = MachineClass.BELOW; machineLine = "BELOW MINIMUM - integrated graphics (Intel(R) UHD Graphics 620) sharing 8 GB of memory";
            }
            if (what == "browse" || what == "browse-detail") { statusText = ""; address = ""; ShotBrowse(what == "browse-detail"); }
            const string ready = "Ready - PLAY starts the live war through Steam.";
            if (what == "public") { statusText = ready; address = ""; releaseText = ""; }
            if (what == "public-mods") { modsOpen = true; hover = "mod:add"; statusText = ready; address = ""; }
            // (the sidebar with an old sandbox's changes found - Restore the game shown)
            if (what == "public-restore") { statusText = "Your Foxhole is still changed (the offline sandbox's changes are still in the game) - press RESTORE THE GAME before you play."; address = ""; releaseText = "";
                                            gameTrace = "the offline sandbox's changes are still in the game"; gameTraceRestorable = true; hover = "restore"; }
            uiScale = 1f;
            CountInstalled();
            if (what == "installed-update") ShotInstalledUpdate();
            if (what == "installed-thumbs") ShotInstalledThumbs();      // (pictures on the rows - LauncherModUpdates.cs)
            if (what == "details") page = "activity";
            if (what.StartsWith("options")) page = what == "options-me" ? "tools" : "settings";
            if (what == "mods") page = "tools";
            if (what == "public-mods" || what == "installed" || what == "installed-update" || what == "installed-thumbs") page = "installed";
            if (what == "settings") page = "settings";
            frame = new Bitmap(W, H, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(frame)) Render(g);
            frame.Save(png, ImageFormat.Png);
        }
    }
}
