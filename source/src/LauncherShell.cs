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
using System.IO;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher
    {
        const int W = 1180, H = 720, SIDE = 216, HDR = 60, STB = 44;
        const int CX = SIDE + 24, CR = W - 24, CY = HDR + 20, CB = H - STB - 16;      // (the page's inner box)
        static readonly Color CBg = Color.FromArgb(14, 15, 17), CSide = Color.FromArgb(19, 20, 23), CSurf = Color.FromArgb(23, 24, 27),
            CSurf2 = Color.FromArgb(29, 31, 35), CLine = Color.FromArgb(38, 40, 45), CLine2 = Color.FromArgb(52, 54, 60),
            CText = Color.FromArgb(236, 234, 228), CText2 = Color.FromArgb(201, 198, 190), CMuted = Color.FromArgb(163, 160, 151),
            CFaint = Color.FromArgb(140, 138, 131), CAccent = Color.FromArgb(212, 184, 114), CAccentHi = Color.FromArgb(226, 202, 140),
            CAccentInk = Color.FromArgb(26, 22, 8), CGood = Color.FromArgb(143, 191, 106), CWarn = Color.FromArgb(224, 180, 90),
            CBad = Color.FromArgb(226, 123, 102), CSwitchOn = Color.FromArgb(111, 160, 75), CSwitchOff = Color.FromArgb(58, 60, 66),
            CNavOn = Color.FromArgb(35, 37, 31);
        string page = "home";

        // ---------------------------------------------------------------------------------------------------- navigation
        static readonly string[] PublicPages = { "home", "browse", "installed", "settings", "activity" };
        static readonly string[] PrivatePages = { "home", "servers", "browse", "installed", "tools", "settings", "activity" };
        void Go(string p)
        {
            if (Array.IndexOf(PublicEdition ? PublicPages : PrivatePages, p) < 0) p = "home";
            if (p == "browse" && page == "browse") { if (browseDetail != null) { browseDetail = null; Redraw(); } return; }
            RefreshPageFacts();
            if (browseOpen && p != "browse") BrowseClose(false);
            page = p;
            modsOpen = p == "installed"; optionsOpen = p == "settings"; details = p == "activity";
            if (p == "browse" && BrowseAvailable()) BrowseOpen();          // (public, until Nexus approves: a placeholder page - DrawBrowsePage)
            if (p == "installed") { CountInstalled(); BrowseRefreshInstalled(); ModUpdatesCheck(false); }   // (an hourly Nexus check where allowed - LauncherModUpdates.cs)
            Redraw();
        }
        bool markerOn;
        System.Windows.Forms.Timer factsTimer;
        void RefreshPageFacts()
        {
            try { markerOn = PublicEdition && File.Exists(OfflineMarker); } catch { }
            if (factsTimer == null && !Headless)
            {
                factsTimer = new System.Windows.Forms.Timer { Interval = 3000 };
                factsTimer.Tick += (s, e) => { bool m = markerOn; RefreshPageFacts(); if (m != markerOn) Redraw(); };
                factsTimer.Start();
            }
        }
        float uiScale = 1f;
        void FitToScreen()
        {
            Rectangle wa;
            try { wa = Screen.FromPoint(Cursor.Position).WorkingArea; } catch { wa = new Rectangle(0, 0, W, H); }
            float s = Math.Min(1f, Math.Min((wa.Width - 24) / (float)W, (wa.Height - 24) / (float)H));
            uiScale = Math.Max(0.6f, s);
            ClientSize = new Size((int)Math.Round(W * uiScale), (int)Math.Round(H * uiScale));
            PlaceSearchBox();
        }
        Point ToFrame(Point p) { return uiScale == 1f ? p : new Point((int)(p.X / uiScale), (int)(p.Y / uiScale)); }

        // ---------------------------------------------------------------------------------------------------- primitives
        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        static void Fill(Graphics g, Rectangle r, Color c, int rad)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }
        static void Edge(Graphics g, Rectangle r, Color c, int rad, float w = 1f)
        {
            using (var p = Round(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }
        static void Card(Graphics g, Rectangle r) { Fill(g, r, CSurf, 10); Edge(g, r, CLine, 10); }
        // a heading line (Renner / Bahnschrift)
        void TxtH(Graphics g, string s, int px, Color c, Rectangle r, uint fmt, int spacing)
        {
            txtHead = true;
            try { Txt(g, s, px, true, c, r, fmt, spacing, false); } finally { txtHead = false; }
        }
        // buttons: kind 0 primary (accent), 1 secondary (outlined), 2 ghost (text only); a greyed one is not clickable
        void Btn(Graphics g, string id, string text, Rectangle r, int kind, bool enabled, string icon = null)
        {
            if (enabled) hits[id] = r; else hits.Remove(id);
            bool hov = enabled && hover == id;
            Color fg;
            if (kind == 0)
            {
                Fill(g, r, !enabled ? CSurf2 : hov ? CAccentHi : CAccent, 8);
                fg = enabled ? CAccentInk : InkOff;
            }
            else if (kind == 1)
            {
                Fill(g, r, hov ? CSurf2 : CSurf, 8); Edge(g, r, hov ? CMuted : CLine2, 8);
                fg = enabled ? CText : InkOff;
            }
            else
            {
                if (hov) Fill(g, r, CSurf2, 8);
                fg = enabled ? (hov ? CText : CText2) : InkOff;
            }
            if (icon != null)
            {
                bool big = kind == 0 && r.Height >= 40;
                int tw = TextWidth(text, big ? 17 : 13, true, big) + (big ? text.Length : 0);     // (+ the heading's 1 px letter spacing)
                int x = r.X + Math.Max(8, (r.Width - tw - 22) / 2);
                Glyph(g, icon, new Rectangle(x, r.Y + (r.Height - 16) / 2, 16, 16), fg);
                r = new Rectangle(x + 22, r.Y, r.Right - x - 22, r.Height);
                if (kind == 0 && r.Height >= 40) TxtH(g, text, 17, fg, r, DT_LEFT | DT_VCENTER | DT_SINGLELINE, 1);
                else Txt(g, text, 13, true, fg, r, DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0, false);
                return;
            }
            if (kind == 0 && r.Height >= 40) TxtH(g, text, 17, fg, r, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 1);
            else Txt(g, text, 13, true, fg, r, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
        }
        // a switch (46 x 26)
        void Switch(Graphics g, string id, bool on, int x, int y, bool enabled)
        {
            var r = new Rectangle(x, y, 46, 26);
            if (enabled) hits[id] = r; else hits.Remove(id);
            Fill(g, r, !enabled ? CSurf2 : on ? CSwitchOn : CSwitchOff, 13);
            if (enabled && hover == id) Edge(g, r, CText2, 13);
            Fill(g, new Rectangle(on ? r.Right - 23 : r.X + 3, r.Y + 3, 20, 20), enabled ? Color.FromArgb(244, 242, 236) : CMuted, 10);
        }
        // a small label with a coloured tint (a verdict, INSTALLED, a category)
        int Badge(Graphics g, string text, int x, int y, Color fg, bool right = false, bool solid = false)
        {
            int w = TextWidth(text, 12, true, false) + 18;
            if (right) x -= w;
            var r = new Rectangle(x, y, w, 24);
            if (solid) Fill(g, r, Color.FromArgb(235, 14, 15, 17), 5);          // (on a picture: a dark backing first)
            Fill(g, r, Color.FromArgb(40, fg.R, fg.G, fg.B), 5);
            Edge(g, r, Color.FromArgb(110, fg.R, fg.G, fg.B), 5);
            Txt(g, text, 12, true, fg, r, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
            return w;
        }
        static Color VerdictTone(string v)
        {
            if (v == "OK") return CGood;
            if (v == "WILL NOT LOAD") return CBad;
            return CWarn;
        }
        // the width of a text line in the body (or heading) font
        int TextWidth(string s, int px, bool bold, bool head)
        {
            using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    txtHead = head;
                    IntPtr old = SelectObject(hdc, FontOf(px, bold));
                    SIZE sz; GetTextExtentPoint32W(hdc, s, s.Length, out sz);
                    SelectObject(hdc, old);
                    return sz.cx;
                }
                finally { txtHead = false; g.ReleaseHdc(hdc); }
            }
        }
        // line icons (16 x 16 box)
        static void Glyph(Graphics g, string name, Rectangle r, Color c)
        {
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = r.Width / 24f;
            Func<float, float, PointF> P = (px, py) => new PointF(r.X + px * s, r.Y + py * s);
            using (var pen = new Pen(c, Math.Max(1.4f, 2f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                switch (name)
                {
                    case "play": g.FillPolygon(br, new[] { P(7, 4), P(20, 12), P(7, 20) }); break;
                    case "search": g.DrawEllipse(pen, r.X + 4 * s, r.Y + 4 * s, 14 * s, 14 * s); g.DrawLine(pen, P(16, 16), P(21, 21)); break;
                    case "box": g.DrawLines(pen, new[] { P(3, 8), P(12, 3), P(21, 8), P(12, 13), P(3, 8) }); g.DrawLines(pen, new[] { P(3, 13), P(12, 18), P(21, 13) }); g.DrawLines(pen, new[] { P(3, 17.5f), P(12, 22), P(21, 17.5f) }); break;
                    case "server": g.DrawRectangle(pen, r.X + 3 * s, r.Y + 4 * s, 18 * s, 7 * s); g.DrawRectangle(pen, r.X + 3 * s, r.Y + 13 * s, 18 * s, 7 * s); g.FillEllipse(br, r.X + 6 * s, r.Y + 6.5f * s, 2.2f * s, 2.2f * s); g.FillEllipse(br, r.X + 6 * s, r.Y + 15.5f * s, 2.2f * s, 2.2f * s); break;
                    case "tools": g.DrawLine(pen, P(5, 19), P(14, 10)); g.DrawEllipse(pen, r.X + 12 * s, r.Y + 3 * s, 9 * s, 9 * s); g.DrawLine(pen, P(4, 4), P(10, 10)); break;
                    case "gear": g.DrawEllipse(pen, r.X + 8 * s, r.Y + 8 * s, 8 * s, 8 * s); for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; g.DrawLine(pen, P(12 + (float)Math.Cos(a) * 6.5f, 12 + (float)Math.Sin(a) * 6.5f), P(12 + (float)Math.Cos(a) * 9.5f, 12 + (float)Math.Sin(a) * 9.5f)); } break;
                    case "list": for (int i = 0; i < 3; i++) { g.DrawLine(pen, P(8, 6 + i * 6), P(21, 6 + i * 6)); g.FillEllipse(br, r.X + 2.5f * s, r.Y + (4.8f + i * 6) * s, 2.6f * s, 2.6f * s); } break;
                    case "home": g.DrawLines(pen, new[] { P(3, 11), P(12, 3), P(21, 11) }); g.DrawLines(pen, new[] { P(5, 9.5f), P(5, 21), P(19, 21), P(19, 9.5f) }); break;
                    case "download": g.DrawLine(pen, P(12, 3), P(12, 15)); g.DrawLines(pen, new[] { P(6, 10), P(12, 16), P(18, 10) }); g.DrawLine(pen, P(4, 20), P(20, 20)); break;
                    case "plus": g.DrawLine(pen, P(12, 5), P(12, 19)); g.DrawLine(pen, P(5, 12), P(19, 12)); break;
                    case "trash": g.DrawLine(pen, P(4, 7), P(20, 7)); g.DrawLines(pen, new[] { P(6, 7), P(7, 20), P(17, 20), P(18, 7) }); g.DrawLines(pen, new[] { P(9, 7), P(9, 4), P(15, 4), P(15, 7) }); break;
                    case "back": g.DrawLines(pen, new[] { P(15, 5), P(8, 12), P(15, 19) }); break;
                    case "refresh": g.DrawArc(pen, r.X + 4 * s, r.Y + 4 * s, 16 * s, 16 * s, 300, 300); g.DrawLines(pen, new[] { P(19, 3), P(19.5f, 8), P(14.5f, 8) }); break;
                    case "check": g.DrawLines(pen, new[] { P(5, 12.5f), P(10, 17.5f), P(19.5f, 6.5f) }); break;
                    case "up": g.DrawLines(pen, new[] { P(4, 20), P(4, 12), P(9, 12), P(9, 7), P(14, 7), P(14, 3), P(20, 3) }); break;
                    case "down": g.DrawLines(pen, new[] { P(6, 9), P(12, 15), P(18, 9) }); break;
                    case "lock": g.DrawRectangle(pen, r.X + 4 * s, r.Y + 11 * s, 16 * s, 10 * s); g.DrawArc(pen, r.X + 7 * s, r.Y + 3 * s, 10 * s, 12 * s, 180, 180); break;
                    case "warn": g.DrawPolygon(pen, new[] { P(12, 3), P(22, 20), P(2, 20) }); g.DrawLine(pen, P(12, 9), P(12, 14)); g.FillEllipse(br, r.X + 11 * s, r.Y + 16 * s, 2.2f * s, 2.2f * s); break;
                    case "chevR": g.DrawLines(pen, new[] { P(9, 5), P(16, 12), P(9, 19) }); break;
                    case "chevL": g.DrawLines(pen, new[] { P(15, 5), P(8, 12), P(15, 19) }); break;
                    case "thumb": g.DrawLine(pen, P(7, 10), P(7, 21)); g.DrawLines(pen, new[] { P(7, 10), P(11, 2.5f), P(14, 4), P(13, 10), P(20, 10), P(18.5f, 21), P(7, 21) }); break;
                }
            }
            g.SmoothingMode = sm;
        }

        // ---------------------------------------------------------------------------------------------------- the frame
        void RenderShell(Graphics g)
        {
            g.Clear(CBg);
            DrawSidebar(g);
            DrawHeader(g);
            var clip = g.Clip;
            g.SetClip(new Rectangle(SIDE, HDR, W - SIDE, H - HDR - STB));
            switch (page)
            {
                case "browse": DrawBrowsePage(g); break;
                case "installed": DrawInstalledPage(g); break;
                case "settings": DrawSettingsPage(g); break;
                case "activity": DrawActivityPage(g); break;
                default: DrawHomePublic(g); break;
            }
            g.Clip = clip; clip.Dispose();
            DrawStatusBar(g);
        }

        void DrawSidebar(Graphics g)
        {
            using (var b = new SolidBrush(CSide)) g.FillRectangle(b, 0, 0, SIDE, H);
            using (var p = new Pen(CLine)) g.DrawLine(p, SIDE - 1, 0, SIDE - 1, H);
            {
                TxtH(g, "FOXHOLE", 30, CText, new Rectangle(22, 18, 190, 34), DT_LEFT | DT_SINGLELINE, 3);
                TxtH(g, "MOD MANAGER", 14, CAccent, new Rectangle(23, 52, 190, 20), DT_LEFT | DT_SINGLELINE, 3);
            }
            int y = 104;
            var items = new[] { new[] { "home", "Play", "play" }, new[] { "browse", "Browse mods", "search" }, new[] { "installed", "Installed", "box" },
                                new[] { "settings", "Settings", "gear" }, new[] { "activity", "Activity", "list" } };
            foreach (var it in items)
            {
                var r = new Rectangle(12, y, SIDE - 24, 40);
                string id = "side:" + it[0];                          // (own ids: a page button "nav:<page>" elsewhere must not take its place)
                hits[id] = r;
                bool on = page == it[0], hov = hover == id;
                if (on || hov) Fill(g, r, on ? CNavOn : CSurf, 8);
                if (on) Fill(g, new Rectangle(r.X, r.Y + 10, 3, 20), CAccent, 1);
                Glyph(g, it[2], new Rectangle(r.X + 14, r.Y + 12, 16, 16), on ? CAccent : hov ? CText : CText2);
                Txt(g, it[1], 14, on, on || hov ? CText : CText2, new Rectangle(r.X + 42, r.Y, 120, r.Height), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0, false);
                if (it[0] == "installed" && installedCount > 0)
                {
                    string n = installedCount.ToString();
                    int bw = TextWidth(n, 11, true, false) + 14;
                    var br = new Rectangle(r.Right - bw - 10, r.Y + 11, bw, 18);
                    Fill(g, br, CLine2, 9);
                    Txt(g, n, 11, true, CText, br, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
                    if (updatesCount > 0)                                 // (mod updates waiting: an accent pill before the count)
                    {
                        string u = updatesCount + " new";
                        int uw = TextWidth(u, 11, true, false) + 14;
                        var ur = new Rectangle(br.X - uw - 6, r.Y + 11, uw, 18);
                        Fill(g, ur, CAccent, 9);
                        Txt(g, u, 11, true, CAccentInk, ur, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
                    }
                }
                y += 44;
            }
            // the game's state + the way back to the original game
            // (no sandbox in this build) the card shows Restore only while something of a 1.0.0 sandbox is in the game (RefreshTrace)
            bool restoreShown = markerOn || offlineBusy || (gameTrace != null && gameTraceRestorable);
            var gc = restoreShown ? new Rectangle(12, H - 172, SIDE - 24, 112) : new Rectangle(12, H - 132, SIDE - 24, 72);
            Fill(g, gc, CSurf, 10); Edge(g, gc, CLine, 10);
            Txt(g, "Your Foxhole", 12, false, CMuted, new Rectangle(gc.X + 14, gc.Y + 10, 160, 18), DT_LEFT | DT_SINGLELINE, 0, false);
            bool changed = restoreShown || gameTrace != null;
            string st = offlineBusy ? "Restoring..." : !changed ? "Original - online ready" : restoreShown ? "Changed - press Restore" : "Changed - a server kit";
            Color dot = PublicEdition ? (changed ? CWarn : CGood) : sideKind == 1 ? CGood : sideKind == 2 ? CWarn : sideKind == 3 ? CBad : CAccent;
            Fill(g, new Rectangle(gc.X + 14, gc.Y + 37, 8, 8), dot, 4);
            Txt(g, st, 13, true, CText, new Rectangle(gc.X + 28, gc.Y + 30, gc.Width - 36, 22), DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            if (restoreShown) Btn(g, "restore", "Restore the game", new Rectangle(gc.X + 12, gc.Y + 64, gc.Width - 24, 34), 1, buttonsOn);
            else hits.Remove("restore");
            Txt(g, "Fan-made. Not affiliated with Siege Camp.", 11, false, CFaint, new Rectangle(20, H - 50, SIDE - 32, 36), DT_LEFT | DT_WORDBREAK, 0, false);
        }
        // (private) the sidebar's game state, set where the update / setup / join / revert end (Launcher.cs): 0 working, 1 good, 2 warning, 3 failed
        string sideText = "Checking...";
        int sideKind = 0;
        void Side(string text, int kind) { sideText = text; sideKind = kind; Ui(() => Redraw()); }
        int installedCount;                                        // (the Installed badge + list - read on start and after every change, never while painting)
        List<Dictionary<string, object>> cosCache = new List<Dictionary<string, object>>();
        void CountInstalled()
        {
            try { cosCache = CosMods(); } catch { cosCache = new List<Dictionary<string, object>>(); }
            installedCount = cosCache.Count;
            try { ComputeUpdates(); } catch { modUpdates.Clear(); updatesCount = 0; }      // (the badges - LauncherModUpdates.cs)
            InstalledRefresh();                                    // (seen downloads + picture addresses, read on a worker)
        }

        void DrawHeader(Graphics g)
        {
            using (var p = new Pen(CLine)) g.DrawLine(p, SIDE, HDR - 1, W, HDR - 1);
            string title = page == "servers" ? "Servers" : page == "installed" ? "Installed mods" : page == "tools" ? "Tools" :
                           page == "settings" ? "Settings" : page == "activity" ? "Activity" : page == "browse" ? "" : "Play";
            if (page == "browse") DrawBrowseHeader(g);
            else TxtH(g, title, 24, CText, new Rectangle(CX, 0, 400, HDR), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 1);
            // the primary action, on every page
            PlayPill(g, "hdr:", new Rectangle(W - 100 - 176, 10, 172, 40));
            // window buttons
            foreach (var id in new[] { "min", "close" })
            {
                var r = id == "min" ? new Rectangle(W - 92, 13, 34, 34) : new Rectangle(W - 52, 13, 34, 34);
                hits[id] = r;
                if (hover == id) Fill(g, r, id == "close" ? Color.FromArgb(150, 40, 32) : CSurf2, 8);
                using (var p = new Pen(hover == id ? Color.White : CText2, 1.6f))
                {
                    int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                    if (id == "min") g.DrawLine(p, cx - 6, cy + 1, cx + 6, cy + 1);
                    else { g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5); g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5); }
                }
            }
        }

        void DrawStatusBar(Graphics g)
        {
            var r = new Rectangle(SIDE, H - STB, W - SIDE, STB);
            using (var b = new SolidBrush(CSide)) g.FillRectangle(b, r);
            using (var p = new Pen(CLine)) g.DrawLine(p, r.X, r.Y, r.Right, r.Y);
            if (progress >= 0)
            {
                Fill(g, new Rectangle(r.X, r.Y, r.Width, 3), CSurf2, 0);
                if (progress > 0) Fill(g, new Rectangle(r.X, r.Y, r.Width * Math.Min(100, progress) / 100, 3), CAccent, 0);
            }
            Fill(g, new Rectangle(CX, r.Y + 18, 8, 8), !buttonsOn ? CAccent : CGood, 4);
            string right = PublicEdition ? PUBLIC_NOTICE : address != "" ? "SERVER  " + address : "";
            int rw = right == "" ? 0 : Math.Min(420, TextWidth(right, 12, false, false) + right.Length + 8);   // (+ its 1 px letter spacing)
            Txt(g, statusText, 13, false, CText2, new Rectangle(CX + 18, r.Y, CR - rw - 24 - CX - 18, r.Height), DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            Txt(g, right, 12, false, CFaint, new Rectangle(CR - rw, r.Y, rw, r.Height), DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 1, false);
        }

        // ---------------------------------------------------------------------------------------------------- home
        // (no sandbox in this build) PLAY = the live war: one plain pill (the click id stays "offline" - LauncherUi.cs Click_)
        void PlayPill(Graphics g, string pre, Rectangle r)
        {
            Btn(g, pre + "offline", buttonsOn && !offlineBusy ? "PLAY" : "BUSY...", r, 0, buttonsOn && !offlineBusy, buttonsOn && !offlineBusy ? "play" : null);
        }
        void DrawHomePublic(Graphics g)
        {
            var hero = new Rectangle(CX, CY, CR - CX, 250);
            Card(g, hero);
            Badge(g, "LIVE WAR", hero.X + 28, hero.Y + 26, CAccent);
            TxtH(g, "Play the live war", 34, CText, new Rectangle(hero.X + 26, hero.Y + 58, 640, 44), DT_LEFT | DT_SINGLELINE, 1);
            Txt(g, "Starts Foxhole through Steam, with the cosmetic mods you switched on in Installed.",
                14, false, CText2, new Rectangle(hero.X + 28, hero.Y + 108, 600, 44), DT_LEFT | DT_WORDBREAK, 0, false);
            PlayPill(g, "", new Rectangle(hero.X + 28, hero.Y + 176, 200, 46));
            Btn(g, "pubupdate", "Check for updates", new Rectangle(hero.X + 242, hero.Y + 182, 160, 34), 2, true);
            string[] steps = { "Switch your mods on in Installed", "Press PLAY: Steam starts Foxhole", "Your switched-on mods load with the game" };
            string modsNote = "Switched-on cosmetic mods load with the game.";
            HeroSteps(g, hero, steps);
            int y = hero.Bottom + 20, w = (CR - CX - 32) / 3;
            InfoCard(g, new Rectangle(CX, y, w, 150), "box", "Your mods", installedCount + " installed", modsNote, "nav:installed", "Manage");
            InfoCard(g, new Rectangle(CX + w + 16, y, w, 150), "search", "Browse mods", "Nexus Mods", "Find Foxhole mods and add them in one step.", "nav:browse", "Browse");
            MachineCard(g, new Rectangle(CX + 2 * (w + 16), y, w, 150));
        }
        // the three numbered steps at the hero's right (both editions)
        void HeroSteps(Graphics g, Rectangle hero, string[] steps)
        {
            int sx = hero.X + 680, sy = hero.Y + 30;
            for (int i = 0; i < steps.Length; i++)
            {
                var cr = new Rectangle(sx, sy + i * 62, 30, 30);
                Fill(g, cr, CSurf2, 15); Edge(g, cr, CLine2, 15);
                Txt(g, (i + 1).ToString(), 13, true, CAccent, cr, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
                Txt(g, steps[i], 13, false, CText2, new Rectangle(sx + 42, sy + i * 62, hero.Right - sx - 60, 34), DT_LEFT | DT_WORDBREAK, 0, false);
            }
        }
        void InfoCard(Graphics g, Rectangle r, string icon, string title, string value, string text, string id, string action)
        {
            Card(g, r);
            Glyph(g, icon, new Rectangle(r.X + 20, r.Y + 20, 18, 18), CAccent);
            Txt(g, title, 12, true, CMuted, new Rectangle(r.X + 46, r.Y + 18, r.Width - 60, 22), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 1, false);
            TxtH(g, value, 20, CText, new Rectangle(r.X + 20, r.Y + 46, r.Width - 40, 28), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0);
            Txt(g, text, 12, false, CMuted, new Rectangle(r.X + 20, r.Y + 78, r.Width - 130, 40), DT_LEFT | DT_WORDBREAK, 0, false);
            Btn(g, id, action, new Rectangle(r.Right - 110, r.Bottom - 46, 92, 32), 1, true);
        }
        void MachineCard(Graphics g, Rectangle r)
        {
            bool below = machineClass == MachineClass.BELOW, low = machineClass == MachineClass.LOW;
            string v = below ? "Below the minimum" : low ? "A weak PC" : machine != null ? "Ready" : "Checking...";
            string t = below || low ? (LowSpecOn() ? "Low-spec mode is on." : "Low-spec mode is off - turn it on in Settings.") : "Your PC meets the game's needs.";
            Card(g, r);
            Glyph(g, below || low ? "warn" : "check", new Rectangle(r.X + 20, r.Y + 20, 18, 18), below ? CWarn : low ? CWarn : CGood);
            Txt(g, "This PC", 12, true, CMuted, new Rectangle(r.X + 46, r.Y + 18, r.Width - 60, 22), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 1, false);
            TxtH(g, v, 20, below ? CWarn : CText, new Rectangle(r.X + 20, r.Y + 46, r.Width - 40, 28), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0);
            Txt(g, t, 12, false, CMuted, new Rectangle(r.X + 20, r.Y + 78, r.Width - 130, 40), DT_LEFT | DT_WORDBREAK, 0, false);
            Btn(g, "nav:settings", "Settings", new Rectangle(r.Right - 110, r.Bottom - 46, 92, 32), 1, true);
        }


        // ---------------------------------------------------------------------------------------------------- installed
        const int INSTALLED_ROWS = 6;
        // (2026-11-18) the row's picture: the mod's Nexus thumbnail (LauncherModUpdates.cs InstalledThumb - memory only while painting), a
        // placeholder while it loads, the box icon for a mod not from Nexus / without a picture; rounded like the Browse cards
        void DrawInstalledThumb(Graphics g, Dictionary<string, object> m, Rectangle tr)
        {
            var p = NexusOf(m);
            bool waiting = false;
            var pic = p != null ? InstalledThumb(p.ModId, out waiting) : null;
            using (var path = Round(tr, 6))
            {
                var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                if (pic != null) g.DrawImageUnscaled(pic, tr.X, tr.Y);
                else
                {
                    using (var b = new SolidBrush(CSurf2)) g.FillRectangle(b, tr);
                    if (waiting)                                          // (loading: three soft dots)
                        using (var b = new SolidBrush(Color.FromArgb(120, CMuted.R, CMuted.G, CMuted.B)))
                            for (int i = 0; i < 3; i++) g.FillEllipse(b, tr.X + tr.Width / 2 - 13 + i * 10, tr.Y + tr.Height / 2 - 3, 6, 6);
                    else Glyph(g, "box", new Rectangle(tr.X + (tr.Width - 20) / 2, tr.Y + (tr.Height - 20) / 2, 20, 20), CMuted);
                }
                g.Clip = clip; clip.Dispose();
            }
            if (pic != null) Edge(g, tr, CLine, 6);
        }
        void DrawInstalledPage(Graphics g)
        {
            Txt(g, "Switched-on mods go into your Foxhole - the live game loads them.", 13, false, CMuted,
                new Rectangle(CX, CY, CR - 506 - CX, 34), DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            bool free = buttonsOn && !offlineBusy;
            Btn(g, "mod:add", "Add mod file", new Rectangle(CR - 150, CY, 150, 34), 0, free, "plus");
            Btn(g, "nav:browse", "Browse mods", new Rectangle(CR - 296, CY, 136, 34), 1, true, "search");
            // (mod updates - LauncherModUpdates.cs; the public edition before Nexus' approval: the button explains why there is no check yet)
            Btn(g, "mod:checkupdates", updatesChecking ? "Checking..." : "Check for mod updates", new Rectangle(CR - 498, CY, 194, 34), 1, !updatesChecking);
            var mods = cosCache;
            int pages = Math.Max(1, (mods.Count + INSTALLED_ROWS - 1) / INSTALLED_ROWS);
            if (modsPage >= pages) modsPage = pages - 1;
            int y = CY + 52;
            for (int i = modsPage * INSTALLED_ROWS; i < Math.Min(mods.Count, (modsPage + 1) * INSTALLED_ROWS); i++, y += 70)
            {
                var m = mods[i];
                string id = Str(m, "id");
                var r = new Rectangle(CX, y, CR - CX, 62);
                Fill(g, r, hover == "mod:info:" + id ? CSurf2 : CSurf, 10); Edge(g, r, CLine, 10);
                // (right to left: remove, switch, verdict, then Update / Roll back - LauncherModUpdates.cs)
                string v = Str(m, "verdict");
                int vw = v != "" ? Badge(g, v, r.Right - 150, r.Y + 20, VerdictTone(v), true) : 0;
                int bx = r.Right - 150 - vw - 10;
                ModUpdate up; modUpdates.TryGetValue(id, out up);
                if (up != null) { bx -= 84; Btn(g, "mod:update:" + id, "Update", new Rectangle(bx, r.Y + 14, 84, 34), 0, free, "download"); bx -= 8; }
                if (m.ContainsKey("previous")) { bx -= 92; Btn(g, "mod:rollback:" + id, "Roll back", new Rectangle(bx, r.Y + 14, 92, 34), 2, free); bx -= 8; }
                hits["mod:info:" + id] = new Rectangle(r.X, r.Y, bx - r.X, r.Height);
                DrawInstalledThumb(g, m, new Rectangle(r.X + 12, r.Y + 13, INSTALLED_THUMB_W, INSTALLED_THUMB_H));
                int tw = bx - 12 - (r.X + 88);
                string name = Str(m, "name"), ver = ModVersionText(m);
                if (up != null)                                           // (the name, then the badge right after it)
                {
                    int badgeW = TextWidth("UPDATE AVAILABLE", 12, true, false) + 18;
                    int nw = Math.Min(TextWidth(name, 15, true, false) + 4, tw - badgeW - 10);
                    Txt(g, name, 15, true, CText, new Rectangle(r.X + 88, r.Y + 10, Math.Max(40, nw), 22), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
                    Badge(g, "UPDATE AVAILABLE", r.X + 88 + Math.Max(40, nw) + 10, r.Y + 9, CAccent);
                    string line = up.Vague ? "Newer files on Nexus Mods - Update shows them" + (ver != "" ? "  -  you have " + ver : "") :
                                  "New version " + (up.Version != "" ? up.Version : "") + (up.Source == "download" ? " is in your Downloads" : " on Nexus Mods") +
                                  (ver != "" ? "  -  you have " + ver : "");
                    Txt(g, line, 12, true, CAccent, new Rectangle(r.X + 88, r.Y + 35, tw, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
                }
                else
                {
                    Txt(g, name, 15, true, CText, new Rectangle(r.X + 88, r.Y + 10, tw, 22), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
                    var notes = StrList(m, "notes");
                    string note = notes.Count > 0 ? notes[notes.Count > 1 && notes[0].StartsWith("replaces") ? 1 : 0] : Str(m, "source");
                    if (ver != "") note = "v" + ver + "  -  " + note;
                    Txt(g, note, 12, false, CMuted, new Rectangle(r.X + 88, r.Y + 33, tw, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
                }
                Switch(g, "mod:on:" + id, Yes(m, "enabled"), r.Right - 136, r.Y + 18, free);
                Btn(g, "mod:rm:" + id, "", new Rectangle(r.Right - 74, r.Y + 13, 36, 36), 1, free);
                Glyph(g, "trash", new Rectangle(r.Right - 64, r.Y + 23, 16, 16), CText2);
            }
            if (mods.Count == 0)
            {
                var z = new Rectangle(CX, y, CR - CX, 200);
                using (var p = Round(new Rectangle(z.X, z.Y, z.Width - 1, z.Height - 1), 12)) using (var pen = new Pen(CLine2, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, p);
                Glyph(g, "download", new Rectangle(z.X + z.Width / 2 - 14, z.Y + 46, 28, 28), CMuted);
                Txt(g, "Drop a .pak, .zip or .7z here", 15, true, CText, new Rectangle(z.X, z.Y + 88, z.Width, 24), DT_CENTER | DT_SINGLELINE, 0, false);
                Txt(g, "or use Add mod file. Downloads from Browse mods are offered by themselves. Only .pak and settings files are ever kept.", 12, false, CMuted,
                    new Rectangle(z.X, z.Y + 116, z.Width, 20), DT_CENTER | DT_SINGLELINE, 0, false);
            }
            if (pages > 1)
            {
                Btn(g, "mod:prev", "Previous", new Rectangle(CR - 250, CB - 34, 90, 32), 1, modsPage > 0);
                Txt(g, "Page " + (modsPage + 1) + " of " + pages, 12, false, CText2, new Rectangle(CR - 156, CB - 34, 60, 32), DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
                Btn(g, "mod:next", "Next", new Rectangle(CR - 90, CB - 34, 90, 32), 1, modsPage < pages - 1);
            }
            if (modsLine != "")
                Txt(g, modsLine, 12, false, CWarn, new Rectangle(CX, CB - 34, CR - CX - 270, 32), DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
        }


        // ---------------------------------------------------------------------------------------------------- settings
        void DrawSettingsPage(Graphics g)
        {
            var o = Options();
            var rows = new List<string[]> { new[] { "lowspec", "Low-spec mode", LowSpecRowText() } };
            var box = new Rectangle(CX, CY, CR - CX, rows.Count * 64 + 8);
            Card(g, box);
            int y = box.Y + 4;
            for (int i = 0; i < rows.Count; i++, y += 64)
            {
                var row = rows[i];
                if (i > 0) using (var p = new Pen(CLine)) g.DrawLine(p, box.X + 20, y, box.Right - 20, y);
                bool on = row[0] == "lowspec" ? LowSpecOn() : On(o, row[0]);
                Txt(g, row[1], 15, true, CText, new Rectangle(box.X + 24, y + 10, 400, 22), DT_LEFT | DT_SINGLELINE, 0, false);
                Txt(g, row[2], 12, false, row[0] == "lowspec" && machineClass == MachineClass.BELOW ? CWarn : CMuted,
                    new Rectangle(box.X + 24, y + 34, box.Width - 140, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
                Switch(g, "opt:" + row[0], on, box.Right - 70, y + 19, !busy);
            }
            y = box.Bottom + 20;
            int w = (CR - CX - 16) / 2;
            var gf = new Rectangle(CX, y, w, 150);
            Card(g, gf);
            TxtH(g, "Game files", 18, CText, new Rectangle(gf.X + 22, gf.Y + 16, 300, 26), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0);
            {
                Txt(g, "The offline sandbox of version 1.0.0 is removed for now. If you used it, Restore the game puts the original files back.", 12, false, CMuted,
                    new Rectangle(gf.X + 22, gf.Y + 50, gf.Width - 44, 40), DT_LEFT | DT_WORDBREAK, 0, false);
                Btn(g, "set:restore", "Restore the game", new Rectangle(gf.X + 22, gf.Bottom - 52, 160, 34), 1,
                    buttonsOn && (markerOn || (gameTrace != null && gameTraceRestorable)));
            }
            var mb = new Rectangle(CX + w + 16, y, w, 150);
            Card(g, mb);
            TxtH(g, "Mod browser", 18, CText, new Rectangle(mb.X + 22, mb.Y + 16, 300, 26), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0);
            Txt(g, "Show adult mods", 13, false, CText2, new Rectangle(mb.X + 22, mb.Y + 52, 200, 26), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0, false);
            Switch(g, "br:adult", browseAdult || On(o, "browse_adult"), mb.Right - 70, mb.Y + 52, BrowseAvailable());
            if (!BrowseAvailable())
                Txt(g, "The in-app browser comes once Nexus Mods approves this app.", 12, false, CMuted, new Rectangle(mb.X + 22, mb.Y + 84, mb.Width - 44, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            Btn(g, "br:clear", "Clear cache", new Rectangle(mb.X + 22, mb.Bottom - 52, 120, 34), 1, !browseLoading);
            Btn(g, "br:privacy", "Privacy", new Rectangle(mb.X + 152, mb.Bottom - 52, 100, 34), 2, true);
            if (PublicEdition)
            {
                // (2026-11-18, GPL-3.0 "Appropriate Legal Notices": copyright, no warranty, where the license is)
                int ly = mb.Bottom + 18;
                Txt(g, PUBLIC_NAME + " " + NexusHttp.AppVersion + " - Copyright (C) 2026 Foxhole Mod Manager contributors. Free software under the GNU GPL version 3 only (GPL-3.0-only), " +
                    "with ABSOLUTELY NO WARRANTY.", 12, false, CMuted,
                    new Rectangle(CX, ly, CR - CX - 140, 36), DT_LEFT | DT_WORDBREAK, 0, false);
                Btn(g, "br:licenses", "Licenses", new Rectangle(CR - 120, ly, 120, 34), 2, true);
            }
        }

        // ---------------------------------------------------------------------------------------------------- activity
        void DrawActivityPage(Graphics g)
        {
            Txt(g, PublicEdition ? "What the app did (launcher.log in " + DataDir.TrimEnd('\\') + ")." :
                "What the launcher did - send this to the host when something fails (launcher.log next to the launcher).", 13, false, CMuted,
                new Rectangle(CX, CY, 640, 34), DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            var box = new Rectangle(CX, CY + 50, CR - CX, CB - CY - 50);
            Fill(g, box, Color.FromArgb(17, 18, 20), 10); Edge(g, box, CLine, 10);
            int lines = (box.Height - 24) / 20, from = Math.Max(0, logLines.Count - lines);
            if (logLines.Count == 0) Txt(g, "Nothing yet.", 13, false, CMuted, new Rectangle(box.X + 18, box.Y + 14, 300, 20), DT_LEFT | DT_SINGLELINE, 0, false);
            for (int i = from, y = box.Y + 12; i < logLines.Count; i++, y += 20)
                Txt(g, logLines[i], 12, false, CText2, new Rectangle(box.X + 18, y, box.Width - 36, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
        }
    }
}
