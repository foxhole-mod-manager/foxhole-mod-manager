// Foxhole Mod Manager (unofficial)
// Copyright (C) 2026 Foxhole Mod Manager contributors
// SPDX-License-Identifier: GPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
// version 3 as published by the Free Software Foundation. This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License (LICENSE) for more details.

// The window itself (2026-11-19, both editions; public 1.1.2):
//  - SHARP ON SCALED SCREENS: the manifests (launcher.manifest, public\app.manifest) declare per-monitor DPI awareness (PerMonitorV2, falling
//    back to PerMonitor / system aware). Windows no longer stretches a 96-DPI bitmap of the window (blurry text at 125 % on a 1920 x 1080
//    screen); the app draws its frame at the real pixel size instead: uiScale = the screen's DPI / 96 (shrunk to fit small screens -
//    LauncherShell.cs FitToScreen), shapes through the Graphics transform, GDI text with fonts and boxes scaled (LauncherUi.cs Txt /
//    LauncherShell.cs TextWidth - GDI text ignores the transform). Moving to a screen with another scale (WM_DPICHANGED) draws it again.
//  - ROUNDED CORNERS + A SILVER EDGE (the look of other modern mod managers): Windows 11 rounds the borderless window and draws a thin
//    silver border along it (DwmSetWindowAttribute: DWMWA_WINDOW_CORNER_PREFERENCE = 33 -> DWMWCP_ROUND, DWMWA_BORDER_COLOR = 34). Windows
//    10 knows neither (an error code, ignored): the frame then draws a 1 px silver line along its own edge (ownEdge). The app's own dialogs
//    get the same. (A DWM border is drawn by Windows outside the app's picture, so --shot pictures show the drawn edge instead.)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher
    {
        [DllImport("dwmapi.dll", PreserveSig = true)] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;
        static readonly Color CEdge = Color.FromArgb(160, 160, 165);     // (silver on the dark frame - not white)
        bool ownEdge = true;                                             // (no DWM border confirmed yet: draw the edge in the frame)
        float drawScale = 1f;                                                   // (the scale the frame is drawn at = uiScale; 1 in --shot unless asked)

        // corners + border for any of the app's windows; true when Windows took the border colour (Windows 11)
        static bool RoundWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int round = DWMWCP_ROUND, edge = CEdge.R | (CEdge.G << 8) | (CEdge.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                return DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref edge, 4) == 0;
            }
            catch (Exception) { return false; }                           // (no DWM function: nothing to do)
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // (review of 1.1.2, 7) the window's OWN screen decides its first scale (mixed-DPI desks: the main screen may be another scale);
            // re-sized and centred on that screen when it differs from the guess made before the window existed
            float own = WindowDpiScale(Handle);
            if (!Headless && Math.Abs(own - dpiScale) > 0.01f)
            {
                dpiScale = own;
                FitToScreen();
                var wa = Screen.FromHandle(Handle).WorkingArea;
                Location = new Point(wa.Left + Math.Max(0, (wa.Width - Width) / 2), wa.Top + Math.Max(0, (wa.Height - Height) / 2));
                DropFrame();
            }
            ownEdge = !RoundWindow(Handle);
            if (!Headless) Say("window: " + (ownEdge ? "edge drawn by the app" : "rounded corners and silver border by Windows") + ", scale " + uiScale.ToString("0.##"));
        }
        // the app's dialogs (Forms shown over the window): rounded + silver edge once, when they first take the focus away from the window
        readonly HashSet<IntPtr> roundedDialogs = new HashSet<IntPtr>();
        void RoundOpenDialogs()
        {
            foreach (Form f in Application.OpenForms)
            {
                if (f == this || !f.IsHandleCreated || roundedDialogs.Contains(f.Handle)) continue;
                roundedDialogs.Add(f.Handle);
                RoundWindow(f.Handle);
            }
        }
        static void ScaleDialogOnLoad(Form f)
        {
            f.Load += (o, e) =>
            {
                float s = WindowDpiScale(f.Handle), sys = ScreenDpiScale();
                if (s > 1.01f) f.Scale(new SizeF(s, s));
                if (Math.Abs(s - sys) > 0.01f) ScaleFonts(f, s / sys);
                var ow = f.Owner;
                if (ow != null && (s > 1.01f || Math.Abs(s - sys) > 0.01f))
                    f.Location = new Point(ow.Left + (ow.Width - f.Width) / 2, ow.Top + (ow.Height - f.Height) / 2);
            };
            f.HandleCreated += (o, e) => RoundWindow(f.Handle);
        }
        // every font of a dialog that is set on the control itself (others are inherited from the parent) times k
        static void ScaleFonts(Form f, float k)
        {
            var own = new List<Control> { f };                           // (found BEFORE any change: the dialog + controls whose font differs from their parent's)
            Action<Control> walk = null;
            walk = c => { foreach (Control ch in c.Controls) { if (!ch.Font.Equals(c.Font)) own.Add(ch); walk(ch); } };
            walk(f);
            var sizes = own.ConvertAll(c => c.Font);
            for (int i = 0; i < own.Count; i++)
                own[i].Font = new Font(sizes[i].FontFamily, Math.Max(4f, sizes[i].SizeInPoints * k), sizes[i].Style, GraphicsUnit.Point);
        }
        // the main screen's scale (1 = 96 DPI) = the system DPI .NET Framework sizes fonts for
        static float ScreenDpiScale()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); } catch { return 1f; }
        }
        // (review of 1.1.2, 7) the scale of the screen a window is on (GetDpiForWindow - Windows 10 1607+; the main screen's before that)
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        static float WindowDpiScale(IntPtr hwnd)
        {
            try { uint d = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd); if (d >= 48) return Math.Max(1f, d / 96f); } catch (Exception) { }
            return ScreenDpiScale();
        }
        float dpiScale = ScreenDpiScale();                               // (a first guess - the window's own screen once it exists: OnHandleCreated)
        void DropFrame() { var f = frame; frame = null; if (f != null) f.Dispose(); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0)                                          // WM_DPICHANGED: the window moved to a screen with another scale
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                dpiScale = Math.Max(1f, dpi / 96f);
                // Windows' suggested rectangle = where the window goes; the size is ours (the scale, shrunk to that screen's work area)
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                var sug = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
                var wa = Screen.FromRectangle(sug).WorkingArea;
                FitToScreen(wa);
                int x = Math.Max(wa.Left, Math.Min(sug.Left, wa.Right - Width)), y = Math.Max(wa.Top, Math.Min(sug.Top, wa.Bottom - Height));
                Location = new Point(x, y);
                DropFrame();
                Redraw();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // the frame's outer edge where Windows draws none (Windows 10, --shot)
        void DrawOwnEdge(Graphics g)
        {
            if (!ownEdge) return;
            using (var p = new Pen(CEdge, 1f / drawScale))
            {
                float o = 0.5f / drawScale;
                g.DrawRectangle(p, o, o, W - 2 * o, H - 2 * o);
            }
        }
    }
}
