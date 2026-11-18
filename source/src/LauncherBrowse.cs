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
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher
    {
        bool browseOpen, browseLoading, browseKeys, browseWired, browseAdult;
        bool browseSearchWanted;                                         // (the player chose the search box - else focus stays off it)
        int browseGen;                                                   // (a load started before the adult switch / CLEAR CACHE is thrown away)
        List<NexusMod> browseAll;
        readonly List<NexusMod> browseView = new List<NexusMod>();
        readonly Dictionary<string, int> browseCounts = new Dictionary<string, int>();
        readonly Dictionary<int, Dictionary<string, object>> browseInstalled = new Dictionary<int, Dictionary<string, object>>();
        string browseCat = "", browseNote = "";
        int browseSort, browsePage, browseFocus;
        NexusMod browseDetail;
        TextBox browseSearch;
        DateTime browseRefreshed = DateTime.MinValue;
        static readonly string[] SortNames = { "MOST DOWNLOADED", "MOST ENDORSED", "RECENTLY UPDATED", "NAME" };
        static readonly string[] SortKeys = { "downloads", "endorsements", "updated", "name" };
        static readonly Dictionary<int, DateTime> nexusAsked = new Dictionary<int, DateTime>();
        static DateTime nexusBrowsedAt = DateTime.MinValue;               // (BROWSE NEXUS in the browser: any Nexus-named file)
        static readonly TimeSpan OFFER_WINDOW = TimeSpan.FromHours(2);

        const int COLS = 4, ROWS = 2, GAP = 14, CARD_W = 218, CARD_H = 204, THUMB_H = 122;   // (2026-11-18: inside the shell's page)
        const int PER_PAGE = COLS * ROWS;
        const uint DT_WORDBREAK = 0x10, DT_EDITCONTROL = 0x2000;
        const string PRIVACY_NOTE =
            "The browser asks Nexus Mods for its public list of Foxhole mods. Nexus and its image servers see your IP address and this app's " +
            "name, as any website does; no account, no Steam ID and nothing about your PC is sent; no tracking or analytics.\n\n" +
            "DOWNLOAD opens Nexus in your own browser, under Nexus' privacy policy.\n\n" +
            "The list and thumbnails are kept on this PC only (CLEAR CACHE deletes them).\n\nData from Nexus Mods. Every mod belongs to its author."
            + "\n\nCHECK FOR UPDATES, and once before each offline sandbox start, the app reads one small file (update.json) from the project's " +
            "GitHub repository (raw.githubusercontent.com): the newest version, and whether a Foxhole update stops the sandbox for now. GitHub sees " +
            "your IP address and this app's name, as any website does; nothing else is sent."
            ;

        const string UPDATES_PRIVACY =
            "\n\nMOD UPDATES: about once an hour (and when you press Check for mod updates) the app asks Nexus Mods for the newest files of the " +
            "mods you added from Nexus, and Installed shows those mods' pictures from Nexus' image servers (kept on this PC like the browser's). The request holds only those mods' Nexus numbers - nothing about you, your account or your PC.";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        bool BrowseAvailable() { return BrowseAllowed(); }
        static bool BrowseAllowed() { return !PublicEdition || On(Options(), "browse_enabled"); }   // (also the command line - review N1)

        // ---------------------------------------------------------------------------------------------------- open / close / load
        void BrowseOpen()
        {
            if (!BrowseAvailable()) { ModsBrowseNexus(); return; }
            browseOpen = true;                                            // (the page itself: LauncherShell.cs Go)
            var o = Options();
            browseSort = Math.Max(0, Array.IndexOf(SortKeys, Str(o, "browse_sort")));
            browseAdult = On(o, "browse_adult");
            BrowseWire();
            browseSearch.Visible = true;
            browseSearchWanted = true; browseSearch.Focus();
            new Thread(PurgeThumbs) { IsBackground = true }.Start();
            BrowseRefreshInstalled();
            if (browseAll == null) BrowseLoad(false); else BrowseApply();
            Redraw();
        }
        void BrowseClose(bool toInstalled)
        {
            browseOpen = false; browseDetail = null; browseKeys = false;
            if (browseSearch != null) browseSearch.Visible = false;
            ThumbForgetQueued();
            ActiveControl = null;
            Redraw();
        }
        void BrowseWire()
        {
            if (browseWired) return;
            browseWired = true;
            browseSearch = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = CSurf, ForeColor = CText,
                Font = new Font("Segoe UI", 11f), MaxLength = 80, AccessibleName = "Search mods by name", Visible = false, TabStop = true
            };
            browseSearch.TextChanged += (s, e) => { browsePage = 0; browseFocus = 0; BrowseApply(); Redraw(); };
            // (WinForms puts focus back on the only control after a dialog / Alt-Tab: taken off again unless the player chose the box)
            browseSearch.GotFocus += (s, e) =>
            {
                if (browseOpen && !browseSearchWanted) BeginInvoke((Action)(() => { if (!browseSearchWanted) ActiveControl = null; }));
                else browseKeys = false;
                Redraw();
            };
            browseSearch.MouseDown += (s, e) => { browseSearchWanted = true; browseKeys = false; Redraw(); };
            Controls.Add(browseSearch);
            PlaceSearchBox();
            try { SendMessage(browseSearch.Handle, 0x1501, (IntPtr)1, "Search by name or author"); } catch { }   // (EM_SETCUEBANNER)
            MouseEventHandler wheel = (s, e) => { if (browseOpen && browseDetail == null) BrowseTurn(e.Delta < 0 ? 1 : -1); };
            MouseWheel += wheel; browseSearch.MouseWheel += wheel;
            ThumbWire();
        }
        void BrowseLoad(bool force)
        {
            if (browseLoading) return;                                    // (one at a time; a stale one reloads itself - generation)
            if (force && DateTime.UtcNow - browseRefreshed < TimeSpan.FromSeconds(60)) { browseNote = "The list was refreshed under a minute ago."; Redraw(); return; }
            browseLoading = true;
            bool adult = browseAdult;
            int gen = browseGen;
            new Thread(() =>
            {
                List<NexusMod> l = null; string note = "", error = null;
                try { l = NexusList(adult, force, out note); } catch (Exception ex) { error = ex.Message; }
                Ui(() =>
                {
                    browseLoading = false;
                    if (gen != browseGen) { BrowseLoad(false); return; }       // (the switch / cache changed meanwhile - this answer is for the old one)
                    if (error != null) { browseNote = error; Redraw(); return; }
                    browseAll = l; browseNote = note; browseRefreshed = DateTime.UtcNow; BrowseApply(); Redraw();
                });
            }) { IsBackground = true }.Start();
            Redraw();
        }
        // which Nexus mods are in the store (read once here - painting never touches the disk)
        void BrowseRefreshInstalled()
        {
            browseInstalled.Clear();
            try
            {
                foreach (var m in CosMods())
                {
                    var p = NexusOf(m);                                   // (only a full Nexus file name counts - review C2)
                    if (p != null) browseInstalled[p.ModId] = m;
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------------------------------------------- filter / sort
        void BrowseApply()
        {
            browseView.Clear(); browseCounts.Clear();
            if (browseAll == null) return;
            string q = browseSearch != null ? browseSearch.Text.Trim() : "";
            var words = q == "" ? new string[0] : q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int all = 0;
            foreach (var m in browseAll)
            {
                bool hit = true;
                foreach (var w in words)
                    if (m.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0 && m.Author.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { hit = false; break; }
                if (!hit) continue;
                all++;
                int c; browseCounts.TryGetValue(m.Category, out c); browseCounts[m.Category] = c + 1;
                if (browseCat == "" || m.Category == browseCat) browseView.Add(m);
            }
            browseCounts[""] = all;
            Comparison<NexusMod> cmp;
            if (browseSort == 1) cmp = (a, b) => b.Endorsements.CompareTo(a.Endorsements);
            else if (browseSort == 2) cmp = (a, b) => b.Updated.CompareTo(a.Updated);
            else if (browseSort == 3) cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            else cmp = (a, b) => b.Downloads.CompareTo(a.Downloads);
            browseView.Sort((a, b) => { int r = cmp(a, b); return r != 0 ? r : a.Id.CompareTo(b.Id); });   // (ties stable by id)
            int pages = Math.Max(1, (browseView.Count + PER_PAGE - 1) / PER_PAGE);
            browsePage = Math.Max(0, Math.Min(browsePage, pages - 1));
            browseFocus = Math.Max(0, Math.Min(browseFocus, browseView.Count - 1));
            ThumbForgetQueued();
        }
        void BrowseTurn(int by)
        {
            int pages = Math.Max(1, (browseView.Count + PER_PAGE - 1) / PER_PAGE);
            int to = Math.Max(0, Math.Min(pages - 1, browsePage + by));
            if (to == browsePage) return;
            browsePage = to;
            browseFocus = Math.Min(browseView.Count - 1, to * PER_PAGE + browseFocus % PER_PAGE);
            ThumbForgetQueued();
            Redraw();
        }

        // ---------------------------------------------------------------------------------------------------- clicks
        void BrowseClick(string h)
        {
            if (h == "back") { if (browseDetail != null) { browseDetail = null; Redraw(); } else Go("home"); }
            else if (h == "installed") Go("installed");
            else if (h == "web") ModsBrowseNexus();                       // (public, until Nexus approves the app: the site in the browser)
            else if (h.StartsWith("cat:")) { browseCat = h.Substring(4); browsePage = 0; browseFocus = 0; BrowseApply(); Redraw(); }
            else if (h == "sort")
            {
                browseSort = (browseSort + 1) % SortNames.Length;
                var o = Options(); o["browse_sort"] = SortKeys[browseSort]; SaveOptions(o);
                browsePage = 0; browseFocus = 0; BrowseApply(); Redraw();
            }
            else if (h == "adult") BrowseToggleAdult();
            else if (h == "refresh" || h == "retry") BrowseLoad(h == "refresh");
            else if (h == "clear")
            {
                browseGen++;
                try { ClearBrowseCache(); browseNote = "Cache cleared (list and thumbnails)."; }
                catch (Exception ex) { browseNote = "Could not clear everything: " + ex.Message; }
                browseAll = null; browseView.Clear(); browseDetail = null;
                if (browseOpen) BrowseLoad(false); else Redraw();           // (from Settings: no Nexus request until BROWSE is opened)
            }
            else if (h == "privacy") MessageBox.Show(this, PRIVACY_NOTE + (UpdatesAutoAllowed() ? UPDATES_PRIVACY : ""), "Privacy - " + (PublicEdition ? PUBLIC_NAME : "Foxhole Mod Launcher"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (h == "licenses")                                     // (Settings: the license files shipped next to the app)
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory, gpl = Path.Combine(dir, "LICENSE.txt");
                if (File.Exists(gpl)) try { Process.Start(new ProcessStartInfo(gpl) { UseShellExecute = true }); } catch { }
                else MessageBox.Show(this, "This app is free software under the GNU General Public License version 3 only (GPL-3.0-only, https://www.gnu.org/licenses/gpl-3.0.html), " +
                    "with ABSOLUTELY NO WARRANTY. The license files (LICENSE.txt, LICENSE-SANDBOX.txt, THIRD-PARTY.txt) come with the download.",
                    "Licenses", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (h == "prev") BrowseTurn(-1);
            else if (h == "next") BrowseTurn(1);
            else if (h.StartsWith("card:")) { int i = int.Parse(h.Substring(5)); if (i < browseView.Count) { browseFocus = i; BrowseShowDetail(browseView[i]); } }
            else if (h == "dl" && browseDetail != null)
            {
                int id = browseDetail.Id;
                if (!BrowseDownloadNotice(browseDetail.Name)) return;
                lock (nexusAsked) nexusAsked[id] = DateTime.UtcNow;
                ModsWatchDownloads();
                try { Process.Start(NexusPage(id, true)); } catch (Exception ex) { browseNote = "could not open the browser: " + ex.Message; Redraw(); return; }
                browseNote = "Nexus opened in your browser - download the file there; it is offered here when it lands in Downloads.";
                Redraw();
            }
            else if (h == "page" && browseDetail != null) { try { Process.Start(NexusPage(browseDetail.Id, false)); } catch { } }
            else if (h == "onoff" && browseDetail != null)
            {
                Dictionary<string, object> m;
                if (!browseInstalled.TryGetValue(browseDetail.Id, out m)) return;
                string id = Str(m, "id"); bool to = !Yes(m, "enabled");
                ModsRun(() => CosEnable(id, to));
            }
        }
        bool BrowseDownloadNotice(string mod)
        {
            if (On(Options(), "browse_dl_notice_off")) return true;
            using (var f = DarkForm("Download from Nexus Mods", 460, 236))
            {
                f.Controls.Add(new Label
                {
                    Left = 18, Top = 16, Width = 424, Height = 136,
                    Text = "The download happens on the Nexus Mods website, in your web browser - not in this app.\n\n" +
                           "1. Your browser opens the Files tab of \"" + (mod.Length > 60 ? mod.Substring(0, 60) + "..." : mod) + "\".\n" +
                           "2. Download the file there (\"Manual download\"; Nexus may ask you to sign in).\n" +
                           "3. When the file lands in your Downloads folder, this app offers to add it - keep it open meanwhile."
                });
                var never = new CheckBox { Text = "Don't show this again", Left = 18, Top = 160, Width = 260 };
                f.Controls.Add(never);
                var ok = new Button { Text = "OPEN NEXUS", Left = 236, Top = 192, Width = 110, Height = 32, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
                var no = new Button { Text = "CANCEL", Left = 352, Top = 192, Width = 90, Height = 32, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
                f.Controls.Add(ok); f.Controls.Add(no); f.AcceptButton = ok; f.CancelButton = no;
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                if (never.Checked) { var o = Options(); o["browse_dl_notice_off"] = true; SaveOptions(o); }
                return true;
            }
        }
        void BrowseShowDetail(NexusMod m)
        {
            browseDetail = m;
            browseSearchWanted = false; ActiveControl = null;               // (keys now belong to the detail view, not the search box)
            ThumbForgetQueued();
            Redraw();
        }
        void BrowseToggleAdult()
        {
            var o = Options();
            bool to = !browseAdult;
            if (to && MessageBox.Show(this, "Show mods that Nexus Mods marks as adult content (18+)?", "Adult content", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!to && browseAll != null)                          // (no adult picture stays on this PC once they are hidden again)
            {
                ThumbInvalidate();                                          // (pictures in flight are never saved)
                try
                {
                    foreach (var m in browseAll)
                        if (m.Adult && Directory.Exists(ThumbDir))
                            foreach (var f in Directory.GetFiles(ThumbDir, m.Id + "-*.png")) try { File.Delete(f); } catch { }
                }
                catch { }
                try { File.Delete(Path.Combine(BrowseDir, "list-1.json")); } catch { }
            }
            o["browse_adult"] = to; SaveOptions(o);
            browseAdult = to; browseGen++;
            browseAll = null; browseView.Clear(); browseDetail = null;
            if (browseOpen) BrowseLoad(false); else Redraw();
        }
        static void SaveOptions(Dictionary<string, object> o) { try { File.WriteAllText(OptionsPath, new JavaScriptSerializer().Serialize(o)); } catch { } }

        // ---------------------------------------------------------------------------------------------------- keys (before the search box sees them)
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (browseOpen && BrowseKey(k)) return true;
            return base.ProcessCmdKey(ref msg, k);
        }
        bool BrowseKey(Keys k)
        {
            if (k == (Keys.Control | Keys.F)) { browseDetail = null; browseSearchWanted = true; browseSearch.Focus(); browseSearch.SelectAll(); Redraw(); return true; }
            if (browseDetail != null)
            {
                if (k == Keys.Escape || k == Keys.Back) { browseDetail = null; Redraw(); return true; }
                if (k == Keys.Left || k == Keys.Right)                         // (the previous / next mod)
                {
                    int i = browseView.IndexOf(browseDetail) + (k == Keys.Left ? -1 : 1);
                    if (i >= 0 && i < browseView.Count) { browseFocus = i; browsePage = i / PER_PAGE; BrowseShowDetail(browseView[i]); }
                    return true;
                }
                if (k == Keys.D) { BrowseClick("dl"); return true; }
                return browseSearch.Focused;                                    // (never typed into the hidden search)
            }
            if (browseSearch.Focused)
            {
                if (k == Keys.Escape) { if (browseSearch.Text != "") browseSearch.Text = ""; else Go("home"); return true; }
                if (k == Keys.Enter || k == Keys.Down || k == Keys.Tab)
                {
                    if (browseView.Count == 0) return true;
                    browseSearchWanted = false; ActiveControl = null; browseKeys = true; browseFocus = Math.Min(browseFocus, browseView.Count - 1);
                    Redraw(); return true;
                }
                return false;                                                   // (typing)
            }
            if (k == Keys.Escape) { browseKeys = false; browseSearchWanted = true; browseSearch.Focus(); Redraw(); return true; }
            if (k == Keys.Tab || k == (Keys.Shift | Keys.Tab)) { browseKeys = false; browseSearchWanted = true; browseSearch.Focus(); Redraw(); return true; }
            if (browseView.Count == 0) return false;
            int f = browseFocus, n = browseView.Count;
            if (k == Keys.Left) f--;
            else if (k == Keys.Right) f++;
            else if (k == Keys.Up) f -= COLS;
            else if (k == Keys.Down) f += COLS;
            else if (k == Keys.PageDown) f += PER_PAGE;
            else if (k == Keys.PageUp) f -= PER_PAGE;
            else if (k == Keys.Home) f = 0;
            else if (k == Keys.End) f = n - 1;
            else if (k == Keys.Enter || k == Keys.Space) { BrowseShowDetail(browseView[browseFocus]); return true; }
            else return false;
            browseKeys = true;
            browseFocus = Math.Max(0, Math.Min(n - 1, f));
            int page = browseFocus / PER_PAGE;
            if (page != browsePage) { browsePage = page; ThumbForgetQueued(); }
            AccessibleDescription = browseView[browseFocus].Name + " by " + browseView[browseFocus].Author;   // (screen readers: the focused card)
            Redraw();
            return true;
        }

        // ---------------------------------------------------------------------------------------------------- drawing (2026-11-18: inside the shell's page - LauncherShell.cs)
        int browseGridY = CY + 90;
        // the header on BROWSE: the search box (the TextBox sits on this), sort, adult; on a detail: back + the mod's name
        void DrawBrowseHeader(Graphics g)
        {
            bool cards = BrowseAvailable() && browseDetail == null;
            if (browseSearch != null && browseSearch.Visible != (cards && browseOpen)) browseSearch.Visible = cards && browseOpen;
            if (!BrowseAvailable()) { TxtH(g, "Browse mods", 24, CText, new Rectangle(CX, 0, 400, HDR), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 1); return; }
            if (browseDetail != null)
            {
                Btn(g, "br:back", "Back to mods", new Rectangle(CX, 12, 150, 36), 1, true, "back");
                Txt(g, "Browse  /  " + browseDetail.Category + "  /  " + browseDetail.Name, 13, false, CMuted, new Rectangle(CX + 166, 0, 520, HDR),
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
                return;
            }
            var sb = new Rectangle(CX, 12, 300, 36);
            bool focus = browseSearch != null && browseSearch.Focused;
            Fill(g, sb, CSurf, 8); Edge(g, sb, focus ? CAccent : CFaint, 8, focus ? 1.6f : 1f);
            Glyph(g, "search", new Rectangle(sb.X + 12, sb.Y + 10, 16, 16), CMuted);
            Txt(g, "Ctrl F", 12, false, CMuted, new Rectangle(sb.Right - 58, sb.Y, 48, sb.Height), DT_RIGHT | DT_VCENTER | DT_SINGLELINE, 0, false);
            Btn(g, "br:sort", "Sort: " + SortLabels[browseSort], new Rectangle(CX + 312, 12, 184, 36), 1, true);
            Btn(g, "br:adult", browseAdult ? "Adult: shown" : "Adult: hidden", new Rectangle(CX + 504, 12, 124, 36), 1, true);
        }
        static readonly string[] SortLabels = { "Most downloaded", "Most endorsed", "Recently updated", "Name" };
        void PlaceSearchBox()
        {
            if (browseSearch == null) return;
            browseSearch.SetBounds((int)((CX + 36) * uiScale), (int)(19 * uiScale), (int)(222 * uiScale), (int)(24 * uiScale));
            var old = browseSearch.Font;
            browseSearch.Font = new Font("Segoe UI", 11f * uiScale);
            if (old != null && old != Control.DefaultFont) old.Dispose();
        }
        void DrawBrowsePage(Graphics g)
        {
            if (!BrowseAvailable())
            {
                var r = new Rectangle(CX, CY, CR - CX, 190);
                Card(g, r);
                Glyph(g, "search", new Rectangle(r.X + 26, r.Y + 28, 22, 22), CAccent);
                TxtH(g, "The mod browser is on its way", 24, CText, new Rectangle(r.X + 60, r.Y + 22, 600, 34), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0);
                Txt(g, "Browsing Nexus Mods inside the app comes once Nexus Mods approves it. Until then, find mods on the Nexus site: a file you " +
                    "download there is offered here when it lands in your Downloads folder.", 14, false, CText2, new Rectangle(r.X + 26, r.Y + 70, r.Width - 52, 48), DT_LEFT | DT_WORDBREAK, 0, false);
                Btn(g, "br:web", "Open Nexus Mods", new Rectangle(r.X + 26, r.Bottom - 58, 170, 38), 0, true);
                Btn(g, "nav:installed", "Installed mods", new Rectangle(r.X + 206, r.Bottom - 58, 150, 38), 1, true);
                return;
            }
            if (browseDetail != null) { DrawBrowseDetail(g, browseDetail); return; }
            // categories as chips (wrapping)
            int x = CX, y = CY;
            var cats = new List<string> { "" }; cats.AddRange(BrowseCategories);
            foreach (var c in cats)
            {
                int n; browseCounts.TryGetValue(c, out n);
                string label = (c == "" ? "All mods" : c) + (browseAll == null ? "" : "  " + n);
                int w = TextWidth(label, 13, c == browseCat, false) + 28;
                if (x + w > CR) { x = CX; y += 42; }
                var r = new Rectangle(x, y, w, 34);
                string id = "br:cat:" + c;
                hits[id] = r;
                bool sel = c == browseCat, hov = hover == id;
                Fill(g, r, sel ? CAccent : hov ? CSurf2 : CSurf, 17);
                if (!sel) Edge(g, r, hov ? CMuted : CLine2, 17);
                Txt(g, label, 13, sel, sel ? CAccentInk : CText2, r, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
                x += w + 8;
            }
            browseGridY = y + 52;
            DrawBrowseGrid(g);
        }
        static string Short(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString(n >= 10000000 ? "0" : "0.0") + "M";
            if (n >= 1000) return (n / 1000.0).ToString(n >= 10000 ? "0" : "0.0") + "K";
            return n.ToString();
        }
        void DrawBrowseGrid(Graphics g)
        {
            if (browseAll == null || browseView.Count == 0)
            {
                string t = browseAll != null ? "No mods match." : browseLoading ? "Loading the list from Nexus Mods..." : "The list could not be loaded.";
                Txt(g, t, 15, false, CMuted, new Rectangle(CX, browseGridY + 40, CR - CX, 30), DT_CENTER | DT_SINGLELINE, 0, false);
                if (browseAll == null && !browseLoading) Btn(g, "br:retry", "Try again", new Rectangle((CX + CR) / 2 - 60, browseGridY + 84, 120, 34), 1, true, "refresh");
            }
            else
            {
                int first = browsePage * PER_PAGE;
                for (int i = first; i < Math.Min(browseView.Count, first + PER_PAGE); i++)
                {
                    int k = i - first;
                    var r = new Rectangle(CX + (k % COLS) * (CARD_W + GAP), browseGridY + (k / COLS) * (CARD_H + GAP), CARD_W, CARD_H);
                    DrawCard(g, browseView[i], r, "br:card:" + i, browseKeys && i == browseFocus);
                }
            }
            int pages = Math.Max(1, (browseView.Count + PER_PAGE - 1) / PER_PAGE);
            int by = CB - 34;
            Txt(g, browseNote != "" ? browseNote : "Data from Nexus Mods. Every mod belongs to its author.", 12, false, CFaint,
                new Rectangle(CX, by, 420, 34), DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            Btn(g, "br:privacy", "Privacy", new Rectangle(CX + 424, by + 1, 76, 32), 2, true);
            Btn(g, "br:refresh", browseLoading ? "Loading..." : "Refresh", new Rectangle(CX + 504, by + 1, 96, 32), 2, !browseLoading);
            Btn(g, "br:prev", "Previous", new Rectangle(CR - 290, by + 1, 96, 32), 1, browsePage > 0);
            Txt(g, "Page " + (browsePage + 1) + " of " + pages, 13, false, CText2, new Rectangle(CR - 190, by, 90, 34), DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
            Btn(g, "br:next", "Next", new Rectangle(CR - 96, by + 1, 96, 32), 1, browsePage < pages - 1);
        }
        void DrawCard(Graphics g, NexusMod m, Rectangle r, string id, bool focused)
        {
            hits[id] = r;
            bool hov = hover == id;
            Fill(g, r, hov ? CSurf2 : CSurf, 10);
            var tr = new Rectangle(r.X, r.Y, r.Width, THUMB_H);
            var pic = Thumb(m, false, tr.Width, tr.Height);
            using (var p = Round(new Rectangle(r.X, r.Y, r.Width, r.Height), 10))
            {
                var clip = g.Clip; g.SetClip(p, CombineMode.Intersect);
                if (pic != null) g.DrawImageUnscaled(pic, tr.X, tr.Y);
                else
                {
                    using (var b = new SolidBrush(CSurf2)) g.FillRectangle(b, tr);
                    Txt(g, m.Thumb == "" ? "No picture" : "Loading...", 12, false, CMuted, tr, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
                }
                g.Clip = clip; clip.Dispose();
            }
            if (browseInstalled.ContainsKey(m.Id)) Badge(g, "INSTALLED", tr.X + 8, tr.Y + 8, CGood, false, true);
            Txt(g, m.Name, 14, true, CText, new Rectangle(r.X + 12, tr.Bottom + 10, r.Width - 24, 20), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            Txt(g, "by " + (m.Author != "" ? m.Author : "unknown"), 12, false, CMuted, new Rectangle(r.X + 12, tr.Bottom + 31, r.Width - 24, 18), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            int sy = tr.Bottom + 54;
            Glyph(g, "download", new Rectangle(r.X + 12, sy + 2, 14, 14), CText2);
            Txt(g, Short(m.Downloads), 12, false, CText2, new Rectangle(r.X + 30, sy, 70, 18), DT_LEFT | DT_SINGLELINE, 0, false);
            Glyph(g, "thumb", new Rectangle(r.X + 92, sy + 2, 14, 14), CText2);
            Txt(g, Short(m.Endorsements), 12, false, CText2, new Rectangle(r.X + 110, sy, 70, 18), DT_LEFT | DT_SINGLELINE, 0, false);
            Edge(g, r, focused ? CAccent : hov ? CLine2 : CLine, 10, focused ? 2.4f : 1f);
        }
        void DrawBrowseDetail(Graphics g, NexusMod m)
        {
            var pr = new Rectangle(CX, CY, 480, 270);
            var pic = Thumb(m, true, pr.Width, pr.Height);
            using (var p = Round(pr, 12))
            {
                var clip = g.Clip; g.SetClip(p, CombineMode.Intersect);
                if (pic != null) g.DrawImageUnscaled(pic, pr.X, pr.Y);
                else
                {
                    using (var b = new SolidBrush(CSurf2)) g.FillRectangle(b, pr);
                    Txt(g, m.Picture == "" && m.Thumb == "" ? "No picture" : "Loading...", 13, false, CMuted, pr, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, false);
                }
                g.Clip = clip; clip.Dispose();
            }
            Edge(g, pr, CLine, 12);
            Txt(g, "ABOUT", 12, true, CMuted, new Rectangle(CX, pr.Bottom + 18, 200, 18), DT_LEFT | DT_SINGLELINE, 2, false);
            Txt(g, m.Summary != "" ? m.Summary : "(no summary)", 14, false, CText2, new Rectangle(CX, pr.Bottom + 42, pr.Width, CB - pr.Bottom - 80),
                DT_LEFT | DT_WORDBREAK | DT_EDITCONTROL | DT_END_ELLIPSIS, 0, false);
            int x = pr.Right + 28, w = CR - x;
            Badge(g, m.Category.ToUpperInvariant(), x, CY, CText2);
            if (m.Adult) Badge(g, "ADULT", x + TextWidth(m.Category.ToUpperInvariant(), 12, true, false) + 30, CY, CWarn);
            TxtH(g, m.Name, 30, CText, new Rectangle(x, CY + 30, w, 76), DT_LEFT | DT_WORDBREAK | DT_EDITCONTROL | DT_END_ELLIPSIS, 0);
            Txt(g, "by " + (m.Author != "" ? m.Author : "unknown"), 14, false, CText2, new Rectangle(x, CY + 108, w, 22), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            var tiles = new[] { new[] { "Downloads", m.Downloads.ToString("N0") }, new[] { "Endorsements", m.Endorsements.ToString("N0") },
                                new[] { "Version", m.Version != "" ? m.Version : "-" }, new[] { "Updated", m.Updated > DateTime.MinValue ? m.Updated.ToLocalTime().ToString("dd MMM yyyy") : "-" } };
            int tw = (w - 12) / 2;
            for (int i = 0; i < 4; i++)
            {
                var t = new Rectangle(x + (i % 2) * (tw + 12), CY + 142 + (i / 2) * 70, tw, 60);
                Card(g, t);
                Txt(g, tiles[i][0], 12, false, CMuted, new Rectangle(t.X + 14, t.Y + 8, t.Width - 28, 18), DT_LEFT | DT_SINGLELINE, 0, false);
                Txt(g, tiles[i][1], 17, true, CText, new Rectangle(t.X + 14, t.Y + 26, t.Width - 28, 26), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
            }
            int by = CY + 290;
            Btn(g, "br:dl", "DOWNLOAD", new Rectangle(x, by, w - 140, 46), 0, true, "download");
            Btn(g, "br:page", "Open page", new Rectangle(CR - 128, by + 4, 128, 38), 1, true);
            Txt(g, "Your browser opens the mod's Files tab on Nexus Mods; the file is offered here when it lands in Downloads.", 12, false, CMuted,
                new Rectangle(x, by + 54, w, 34), DT_LEFT | DT_WORDBREAK, 0, false);
            Dictionary<string, object> inst;
            if (browseInstalled.TryGetValue(m.Id, out inst))
            {
                string v = Str(inst, "verdict");
                var ir = new Rectangle(x, by + 100, w, 60);
                Color tone = v != "" ? VerdictTone(v) : CText2;
                Fill(g, ir, Color.FromArgb(28, tone.R, tone.G, tone.B), 10); Edge(g, ir, Color.FromArgb(110, tone.R, tone.G, tone.B), 10);
                Glyph(g, v == "OK" ? "check" : "warn", new Rectangle(ir.X + 16, ir.Y + 20, 18, 18), tone);
                Txt(g, "Installed" + (v != "" ? " - " + v : ""), 14, true, CText, new Rectangle(ir.X + 46, ir.Y + 10, ir.Width - 130, 22), DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, false);
                Txt(g, Yes(inst, "enabled") ? "Switched on - it is in your game." : "Switched off.", 12, false, CText2, new Rectangle(ir.X + 46, ir.Y + 32, ir.Width - 130, 18), DT_LEFT | DT_SINGLELINE, 0, false);
                Switch(g, "br:onoff", Yes(inst, "enabled"), ir.Right - 64, ir.Y + 17, true);
            }
            Txt(g, "Left / Right: previous / next mod     D: download     Esc: back", 12, false, CFaint, new Rectangle(x, CB - 30, w, 30), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0, false);
        }

        // (--shot browse / browse-detail: the list from the cache or Nexus, the page's pictures loaded first)
        void ShotBrowse(bool detail)
        {
            page = "browse"; browseOpen = true;
            BrowseWire();
            string note;
            browseAll = NexusList(false, false, out note); browseNote = note;
            BrowseRefreshInstalled();
            BrowseApply();
            for (int i = 0; i < Math.Min(PER_PAGE, browseView.Count); i++)
            {
                var m = browseView[i];
                try { string key = m.Id + "-" + ThumbKey(m.Thumb, CARD_W, THUMB_H); var b = LoadThumb(key, m.Thumb, CARD_W, THUMB_H); if (b != null) thumbs.Put(key, b); } catch { }
            }
            if (detail && browseView.Count > 0)
            {
                browseDetail = browseView[0];
                var m = browseDetail; string url = m.Picture != "" ? m.Picture : m.Thumb;
                try { string key = m.Id + "-" + ThumbKey(url, 480, 270); var b = LoadThumb(key, url, 480, 270); if (b != null) thumbs.Put(key, b); } catch { }
            }
            else { browseKeys = true; browseFocus = 1; hover = "br:card:2"; }
        }
    }
}
