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
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher
    {
        static readonly string OfflineMarker = Path.Combine(DataDir, "offline_session.json");
        bool offlineBusy;

        void OfflineStartup()
        {
            FormClosing += (s, e) =>
            {
                if (offlineBusy && MessageBox.Show(this, "An offline session is running. Close the app anyway?\n\nYour Foxhole is put back to original the " +
                    "next time you start the app.", PUBLIC_NAME, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
            };
            if (File.Exists(OfflineMarker) && OfflineGameRunning())        // (the app closed while the sandbox ran: pick it up again)
            {
                OfflineRun(() =>
                {
                    Status("An offline sandbox is still running - close Foxhole to end it (the game is then put back to original).");
                    while (OfflineGameRunning()) Thread.Sleep(3000);
                    OfflineRestore("at the end of the session");
                });
                return;
            }
            if (File.Exists(OfflineMarker))
            {
                Status("Your last offline session did not end cleanly - putting Foxhole back to original...");
                OfflineRun(() => OfflineRestore("after the last session"));
                return;
            }
            string g0 = MeGameDir();
            if (g0 != "" && Sandbox.HasLeftovers(g0))                     // (the game is original; some of the player's files wait - LauncherSandbox.cs Revert)
            {
                string left = "";
                try { left = Sandbox.LeftoverText(g0); } catch { }
                if (left != "") { Say("sandbox: " + left); Status("Your Foxhole is original. " + left); return; }
            }
            Status("Ready - the arrow on PLAY switches between the live war and the offline sandbox.");
        }

        // the player's own game (the Steam install's exe - the sandbox server runs from another copy) still open?
        bool OfflineGameRunning()
        {
            string game = MeGameDir();
            if (game == "") return false;
            string exe = Path.GetFullPath(Path.Combine(game, "War", "Binaries", "Win64", "War-Win64-Shipping.exe"));
            foreach (var p in Process.GetProcessesByName("War-Win64-Shipping"))
            {
                try { if (string.Equals(Path.GetFullPath(p.MainModule.FileName), exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            }
            return false;
        }

        static List<string> OfflineMaps()
        {
            var r = new List<string>();
            string f = Path.Combine(Kit, "tools", "maps.json");
            try
            {
                foreach (var o in (System.Collections.IEnumerable)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f)))
                {
                    string m = o.ToString();
                    if (m != "Sandbox" && m != "ShootingRange") r.Add(m);
                }
            }
            catch { }
            if (r.Count == 0) r.AddRange(new[] { "HomeRegionC", "HomeRegionW", "DeadLandsHex" });
            return r;
        }
        static string MapTitle(string m)
        {
            if (m == "HomeRegionC") return "Colonial home region";
            if (m == "HomeRegionW") return "Warden home region";
            return Regex.Replace(Regex.Replace(m, "Hex$", ""), "(?<=[a-z])(?=[A-Z])", " ");
        }

        // the picker: (map, faction 0 Colonial / 1 Warden) or null
        string[] OfflinePick()
        {
            using (var f = DarkForm("Play offline", 420, 200))
            {
                f.Controls.Add(new Label { Text = "HEX", Left = 18, Top = 18, Width = 120 });
                var maps = OfflineMaps();
                var box = new ComboBox { Left = 18, Top = 42, Width = 384, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(40, 40, 37), ForeColor = Color.White };
                foreach (var m in maps) box.Items.Add(MapTitle(m));
                int dl = maps.IndexOf("DeadLandsHex"); box.SelectedIndex = dl >= 0 ? dl : 0;
                f.Controls.Add(box);
                f.Controls.Add(new Label { Text = "FACTION", Left = 18, Top = 82, Width = 120 });
                var col = new RadioButton { Text = "Colonial", Left = 18, Top = 104, Width = 110, Checked = true };
                var war = new RadioButton { Text = "Warden", Left = 140, Top = 104, Width = 110 };
                f.Controls.Add(col); f.Controls.Add(war);
                var ok = new Button { Text = "START", Left = 222, Top = 150, Width = 86, Height = 32, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
                var no = new Button { Text = "CANCEL", Left = 316, Top = 150, Width = 86, Height = 32, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
                f.Controls.Add(ok); f.Controls.Add(no); f.AcceptButton = ok; f.CancelButton = no;
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                return new[] { maps[box.SelectedIndex], war.Checked ? "1" : "0" };
            }
        }

        void OfflineRun(Action a)
        {
            if (offlineBusy) return;
            offlineBusy = true;
            Buttons(false);
            new Thread(() =>
            {
                try { a(); }
                catch (Exception ex) { Say("offline: " + ex.Message); Status("Offline: " + ex.Message); }
                offlineBusy = false;
                Buttons(true);
            }) { IsBackground = true }.Start();
        }

        // (2026-11-18) PLAY in "Official Foxhole" mode (the default, LauncherShell.cs PlaySplit): Steam starts the game as it is. Never while
        // the game is changed - a sandbox running or not put back, or a server kit set up in it (a patched game must never reach the
        // official servers)
        void OfficialPlayClick()
        {
            if (offlineBusy) return;
            string why = null, game = MeGameDir();
            if (game == "") why = "Foxhole was not found (Steam library).";
            else if (File.Exists(OfflineMarker)) why = "The offline sandbox has not put your game back yet - close the sandbox or press \"Restore the game\" first.";
            else if (File.Exists(Path.Combine(game, "_FoxholeOffline", "install.json")))
                why = "Your Foxhole has a server kit set up in it (a changed game) - use its \"Back to official Foxhole\" first.";
            // (review B) the game itself checked: a pak or an exe that is POSITIVELY the patched one blocks (a state folder deleted after a
            // crash leaves no record) - an unknown exe never does (every Steam update would lock everyone out)
            else if ((why = Sandbox.LiveWarBlock(game)) != null) { }
            else if (OfflineGameRunning()) why = "Foxhole is already running.";
            if (why != null) { Status("Play: " + why); MessageBox.Show(this, why, "Play the live war", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/505460") { UseShellExecute = true });
                Say("play: the live war started through Steam");
                Status("Starting the live war through Steam...");
            }
            catch (Exception ex) { Status("Play: Steam could not be started (" + ex.Message + ")"); }
        }

        void OfflinePlayClick()
        {
            if (offlineBusy) return;
            var pick = OfflinePick();
            if (pick == null) return;
            OfflineRun(() =>
            {
                string game = MeGameDir();
                if (game == "") throw new Exception("Foxhole was not found (Steam library)");
                if (OfflineGameRunning()) throw new Exception("Foxhole is running - close it first");
                // (2026-11-18 review) paks in Foxhole's own Saved\Paks folder would load in the sandbox too: refused before anything changes
                string savedPaks = Sandbox.SavedPaksProblem();
                if (savedPaks != null) throw new Exception(savedPaks);
                // leftovers of the last session (the player's set-aside mod files that could not go back): tried again first, refused while
                // any is still waiting (the sandbox would set aside files of the same names)
                if (Sandbox.HasLeftovers(game))
                {
                    Status("Putting back files left over from the last sandbox...");
                    SandboxStep("revert", game);
                    string left = Sandbox.HasLeftovers(game) ? Sandbox.LeftoverText(game) : "";
                    if (left != "") throw new Exception(left);
                }
                if (File.Exists(Sandbox.PathsOf(game).Info))
                    throw new Exception("your Foxhole already has a server kit set up in it - use its \"Back to official Foxhole\" first; the sandbox leaves it alone");
                string off = SandboxSwitchedOff(game);
                if (off != null) throw new Exception(off);
                File.WriteAllText(OfflineMarker, new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }, { "map", pick[0] }, { "faction", pick[1] }, { "game", game } }));
                Status("Setting up your game for the offline sandbox (a minute)...");
                Sandbox.Log = m => Say(m);
                int code = SandboxStep("install", game);
                if (code != 0) { OfflineRestore("after a failed setup"); throw new Exception("the setup failed - Foxhole is back to original (see Activity)"); }
                Status("Starting the sandbox: " + MapTitle(pick[0]) + " (only this PC can join)...");
                try { Sandbox.Play(game, pick[0], pick[1]); }
                catch (Exception ex) { Say("sandbox: " + ex.Message); OfflineRestore("after a failed start"); throw new Exception(ex.Message + " - Foxhole is back to original"); }
                for (int i = 0; i < 90 && !OfflineGameRunning(); i++) Thread.Sleep(2000);
                if (!OfflineGameRunning())
                {
                    Status("Foxhole has not opened yet - still waiting for it (up to 6 minutes in all) before the game is put back to original...");
                    for (int i = 0; i < 90 && !OfflineGameRunning(); i++) Thread.Sleep(2000);
                }
                Status("Offline sandbox running - close Foxhole to end it (the game is then put back to original).");
                while (OfflineGameRunning()) Thread.Sleep(3000);
                Status("Offline session over - putting Foxhole back to original...");
                OfflineRestore("at the end of the session");
            });
        }

        void OfflineRestore(string when)
        {
            string game = "";
            try { var mk = File.Exists(OfflineMarker) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(OfflineMarker)) as Dictionary<string, object> : null; if (mk != null && mk.ContainsKey("game")) game = mk["game"] as string ?? ""; } catch { }
            if (game == "" || !Directory.Exists(game)) game = MeGameDir();
            Sandbox.Log = m => Say(m);
            try { Sandbox.Stop(game); } catch (Exception ex) { Say("stop: " + ex.Message); }
            // (R4: nothing was set up - a refused or failed setup - so there is nothing to put back.) (review A1) No state folder does NOT
            // prove that: the player may have deleted it after a crash. The game itself is checked; the marker goes only when it is original.
            if (!Directory.Exists(Sandbox.PathsOf(game).State))
            {
                string changed = Sandbox.ChangedWithoutState(game);
                if (changed == null) { try { File.Delete(OfflineMarker); } catch { } Status("Foxhole was not changed " + when + "."); return; }
                Say("sandbox: the game is still changed (" + changed + ") but its _FoxholeOffline backup folder is gone - Steam's Verify repairs it");
                Status("Your Foxhole is still changed (" + changed + ") and the app's backup folder in the game is gone. In Steam: right-click Foxhole > " +
                       "Properties > Installed Files > \"Verify integrity of game files\", then press RESTORE THE GAME - the app checks again.");
                return;
            }
            int code = SandboxStep("revert", game);
            if (code != 0) { Status("Could not put Foxhole back to original " + when + " (code " + code + ") - RESTORE THE GAME tries again; Steam's Verify also repairs it."); return; }
            try { File.Delete(OfflineMarker); } catch { }
            if (revertNote != "") { Status("Foxhole is back to original " + when + ". " + revertNote); return; }
            Status("Foxhole is back to original " + when + ". Your cosmetic mods stay as you set them in Installed.");
        }

        string revertNote = "";                                           // (the last revert's leftovers note - "" = fully clean)
        int SandboxStep(string step, string game)
        {
            if (!Sandbox.CanWrite(Sandbox.PathsOf(game)))
            {
                Say("sandbox " + step + ": this app cannot change your Foxhole folder (" + game + ") - Windows does not let your account write there. " +
                    "Steam's own library folders normally allow it; a library under Program Files may not.");
                return 1;
            }
            revertNote = "";
            try { if (step == "install") Sandbox.Install(game); else revertNote = Sandbox.Revert(game); return 0; }
            catch (Exception ex) { Say("sandbox " + step + ": " + ex.Message); return 1; }
        }

        void OfflineRestoreClick()
        {
            if (offlineBusy) return;
            string g = MeGameDir();
            bool leftovers = g != "" && Sandbox.HasLeftovers(g);           // (files of the last session still waiting: Restore tries them again)
            if (!File.Exists(OfflineMarker) && !leftovers)
            {
                MessageBox.Show(this, "Your Foxhole is only changed while an offline session runs - there is nothing to restore right now.", PUBLIC_NAME,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (OfflineGameRunning()) { MessageBox.Show(this, "Close Foxhole first.", PUBLIC_NAME); return; }
            OfflineRun(() => OfflineRestore("on request"));
        }

        static string PublicRepo()
        {
            try
            {
                var s = Res("public_release.json");
                if (s == null) return "";
                using (var r = new StreamReader(s)) { var d = new JavaScriptSerializer().DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>; string repo = d != null && d.ContainsKey("repo") ? d["repo"] as string : ""; return repo != null && Regex.IsMatch(repo, @"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$") ? repo : ""; }
            }
            catch { return ""; }
        }
        // REMOTE OFF SWITCH (2026-11-18): a Foxhole update can break the sandbox (new code the server does not know yet). update.json may say
        //   "sandbox_off": {"builds": ["<Steam build id>", ...], "all": false, "below_app": "1.0.1", "message": "..."}
        // (one entry, or a list of them) and PLAY OFFLINE then refuses on those builds with the message, instead of a broken start.
        // "below_app" = the app version that fixes it: the entry applies only while this app is OLDER than that (an app with the fix is never
        // stopped by an old entry); an entry without it applies to every app version, one whose "below_app" cannot be read to none.
        // Steam build id = the game's appmanifest_505460.acf "buildid" (two folders up from the game). Fail-open: no network, no repo, no
        // manifest -> the sandbox starts (it is an offline feature; the switch only exists to stop a KNOWN-bad combination).
        static string GameBuildId(string game)
        {
            try
            {
                var acf = Path.GetFullPath(Path.Combine(game, "..", "..", "appmanifest_505460.acf"));
                if (!File.Exists(acf)) return "";
                var m = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s+\"(\\d{1,12})\"");
                return m.Success ? m.Groups[1].Value : "";
            }
            catch { return ""; }
        }
        string SandboxSwitchedOff(string game)
        {
            string repo = PublicRepo();
            if (repo == "") return null;
            Status("Checking the sandbox status...");
            try
            {
                var data = NexusHttp.SendTo("https://raw.githubusercontent.com/" + repo + "/main/update.json", 64 * 1024, new[] { "raw.githubusercontent.com" }, "GitHub");
                var d = new JavaScriptSerializer().DeserializeObject(System.Text.Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
                object raw = d != null && d.ContainsKey("sandbox_off") ? d["sandbox_off"] : null;
                var entries = new List<Dictionary<string, object>>();
                if (raw is Dictionary<string, object>) entries.Add((Dictionary<string, object>)raw);
                else if (raw is object[]) foreach (var x in (object[])raw) { var e = x as Dictionary<string, object>; if (e != null) entries.Add(e); }
                if (entries.Count == 0) return null;
                string build = GameBuildId(game);
                Version have = new Version(NexusHttp.AppVersion);
                foreach (var so in entries)
                {
                    if (so.ContainsKey("below_app"))
                    {
                        Version below;
                        if (!Version.TryParse(so["below_app"] as string ?? "", out below) || have >= below) continue;   // (this app has the fix - or unreadable)
                    }
                    bool hit = so.ContainsKey("all") && so["all"] is bool && (bool)so["all"];
                    var builds = so.ContainsKey("builds") ? so["builds"] as object[] : null;
                    if (!hit && build != "" && builds != null) foreach (var b in builds) if (b != null && b.ToString() == build) { hit = true; break; }
                    if (!hit) continue;
                    string msg = so.ContainsKey("message") ? (so["message"] as string ?? "") : "";
                    if (msg.Length > 300) msg = msg.Substring(0, 300) + "...";
                    Say("sandbox: switched off for game build " + (build == "" ? "?" : build) + " (app " + NexusHttp.AppVersion + ") by update.json");
                    return (msg != "" ? msg : "the offline sandbox does not work with this Foxhole version yet - an update of the app will follow") + " (Live War is not affected)";
                }
                return null;
            }
            catch (Exception ex) { Say("sandbox: off-switch check skipped (" + ex.Message + ")"); return null; }
        }

        void OfflineCheckUpdate()
        {
            string repo = PublicRepo();
            if (repo == "") { Status("This build has no update source."); return; }
            Status("Checking for updates...");
            new Thread(() =>
            {
                try
                {
                    var data = NexusHttp.SendTo("https://raw.githubusercontent.com/" + repo + "/main/update.json", 64 * 1024, new[] { "raw.githubusercontent.com" }, "GitHub");
                    var d = new JavaScriptSerializer().DeserializeObject(System.Text.Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
                    Version have = new Version(NexusHttp.AppVersion), latest;
                    if (d == null || !d.ContainsKey("version") || !Version.TryParse(d["version"] as string ?? "", out latest)) throw new Exception("no version in update.json");
                    if (latest <= have) { Status("You have the newest version (" + NexusHttp.AppVersion + ")."); return; }
                    string notes = d.ContainsKey("notes") ? (d["notes"] as string ?? "") : "";
                    if (notes.Length > 400) notes = notes.Substring(0, 400) + "...";
                    Status("Version " + latest + " is out.");
                    Ui(() =>
                    {
                        if (MessageBox.Show(this, "Version " + latest + " of " + PUBLIC_NAME + " is out (you have " + NexusHttp.AppVersion + ")." +
                            (notes != "" ? "\n\n" + notes : "") + "\n\nOpen its download page?", PUBLIC_NAME, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            try { Process.Start("https://github.com/" + repo + "/releases/latest"); } catch { }
                    });
                }
                catch (Exception ex) { Status("Could not check for updates: " + ex.Message); }
            }) { IsBackground = true }.Start();
        }
    }
}
