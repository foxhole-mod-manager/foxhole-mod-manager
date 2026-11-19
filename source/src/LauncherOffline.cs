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

        // (no sandbox in this build) a 1.0.0 session that did not end is still put back at start - the marker is the proof (unless a server kit
        // is set up in that game: then the marker goes and nothing is touched - review A) - and anything else of an old sandbox is shown in the
        // sidebar with RESTORE THE GAME
        void OfflineStartup()
        {
            FormClosing += (s, e) =>
            {
                if (offlineBusy && MessageBox.Show(this, "The game is being restored. Close the app anyway?\n\nRESTORE THE GAME finishes it the next time.",
                    PUBLIC_NAME, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
            };
            if (MarkerForKit()) { RefreshTrace(true); return; }
            if (File.Exists(OfflineMarker) && OfflineGameRunning())        // (a 1.0.0 sandbox still running: put back once it is closed)
            {
                OfflineRun(() =>
                {
                    Status("Foxhole is running with the changes of an offline sandbox (version 1.0.0) - close it; the game is then put back to original.");
                    while (OfflineGameRunning()) Thread.Sleep(3000);
                    OfflineRestore("at the end of the session");
                });
                return;
            }
            if (File.Exists(OfflineMarker))
            {
                Status("An offline session of an earlier version did not end cleanly - putting Foxhole back to original...");
                OfflineRun(() => OfflineRestore("after the last session"));
                return;
            }
            Status("Ready - PLAY starts the live war through Steam.");
            RefreshTrace(true);
        }
        // the game folder of the offline session's record (offline_session.json "game") - "" when there is none
        static string MarkerGame()
        {
            try
            {
                var mk = File.Exists(OfflineMarker) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(OfflineMarker)) as Dictionary<string, object> : null;
                string g = mk != null && mk.ContainsKey("game") ? mk["game"] as string ?? "" : "";
                return g != "" && Directory.Exists(g) ? g : "";
            }
            catch { return ""; }
        }
        bool MarkerForKit()
        {
            if (!File.Exists(OfflineMarker)) return false;
            string g = MarkerGame();
            if (g == "") g = MeGameDir();
            if (g == "" || !Sandbox.KitNotOurs(g)) return false;
            try { File.Delete(OfflineMarker); } catch { }
            Say("sandbox: the last session's record was dropped - a server kit is set up in " + g + " (left as it is)");
            Status("Your Foxhole has a server kit set up in it - the app leaves it alone. Use its \"Back to official Foxhole\" before you play the live war.");
            return true;
        }
        // what the sidebar shows of an old sandbox (Sandbox.Trace) - read on a worker (the exe's fingerprint reads 80 MB once), never while
        // painting: null = nothing to restore; restorable = Restore the game is the way back
        string gameTrace;
        bool gameTraceRestorable, traceBusy;
        void RefreshTrace(bool say)
        {
            if (traceBusy || Headless) return;
            traceBusy = true;
            new Thread(() =>
            {
                string tr = null; bool ok = false;
                try { string g = MeGameDir(); if (g != "") tr = Sandbox.Trace(g, File.Exists(OfflineMarker), out ok); } catch { }
                traceBusy = false;
                Ui(() =>
                {
                    bool changed = tr != gameTrace || ok != gameTraceRestorable;
                    gameTrace = tr; gameTraceRestorable = ok;
                    if (say && tr != null && !offlineBusy) Status(ok ? "Your Foxhole is still changed (" + tr + ") - press RESTORE THE GAME before you play." : "Your Foxhole is changed: " + tr + ".");
                    if (changed) Redraw();
                });
            }) { IsBackground = true }.Start();
        }

        // the player's own game (the Steam install's exe - the sandbox server runs from another copy) still open?
        bool OfflineGameRunning() { return OfflineGameRunning(MeGameDir()); }
        static bool OfflineGameRunning(string game)
        {
            if (string.IsNullOrEmpty(game)) return false;
            string exe = Path.GetFullPath(Path.Combine(game, "War", "Binaries", "Win64", "War-Win64-Shipping.exe"));
            foreach (var p in Process.GetProcessesByName("War-Win64-Shipping"))
            {
                try { if (string.Equals(Path.GetFullPath(p.MainModule.FileName), exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            }
            return false;
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
            string why = null, game = FindFoxholeSteam();
            if (game == "") why = "Foxhole was not found (Steam library).";
            else if (File.Exists(OfflineMarker)) why = "The offline sandbox has not put your game back yet - close the sandbox or press \"Restore the game\" first.";
            else if (File.Exists(Sandbox.PathsOf(game).Info) && Sandbox.IsOurs(Sandbox.PathsOf(game)))
                why = "Your Foxhole still has the offline sandbox's changes - press \"Restore the game\" first.";
            else if (File.Exists(Path.Combine(game, "_FoxholeOffline", "install.json")))
                why = "Your Foxhole has a server kit set up in it (a changed game) - use its \"Back to official Foxhole\" first, or run Steam's " +
                      "\"Verify integrity of game files\" for Foxhole and delete that _FoxholeOffline folder (" + Path.Combine(game, "_FoxholeOffline") + ").";
            // (review B) the game itself checked: a pak or an exe that is POSITIVELY the patched one blocks (a state folder deleted after a
            // crash leaves no record) - an unknown exe never does (every Steam update would lock everyone out)
            else if ((why = Sandbox.LiveWarBlock(game)) != null) { }
            else if (OfflineGameRunning(game)) why = "Foxhole is already running.";
            if (why != null) { Status("Play: " + why); MessageBox.Show(this, why, "Play the live war", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/505460") { UseShellExecute = true });
                Say("play: the live war started through Steam");
                Status("Starting the live war through Steam...");
            }
            catch (Exception ex) { Status("Play: Steam could not be started (" + ex.Message + ")"); }
        }


        // (public) the sandbox's own server stopped (found by its path - never another Foxhole), the game put back (exe, pak settings, client
        // mods back to the originals - LauncherSandbox.cs), then the marker goes. (private) only the singleplayer server ends.
        void OfflineRestore(string when)
        {
            string game = "";
            try { var mk = File.Exists(OfflineMarker) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(OfflineMarker)) as Dictionary<string, object> : null; if (mk != null && mk.ContainsKey("game")) game = mk["game"] as string ?? ""; } catch { }
            if (game == "" || !Directory.Exists(game)) game = MeGameDir();
            Sandbox.Log = m => Say(m);
            if (MarkerForKit()) { RefreshTrace(false); return; }          // (review A: a server kit's server, saves and records are never touched)
            try { int n = Sandbox.StopServerCopy(game); if (n > 0) Say("restore: ended " + n + " process(es) of the old sandbox's server copy"); } catch (Exception ex) { Say("restore: " + ex.Message); }
            // (R4: nothing was set up - a refused or failed setup - so there is nothing to put back.) (review A1) No state folder does NOT
            // prove that: the player may have deleted it after a crash. The game itself is checked; the marker goes only when it is original.
            if (!Directory.Exists(Sandbox.PathsOf(game).State))
            {
                string changed = Sandbox.ChangedWithoutState(game);
                if (changed == null) { try { File.Delete(OfflineMarker); } catch { } Status("Foxhole was not changed " + when + "."); RefreshTrace(false); return; }
                Say("sandbox: the game is still changed (" + changed + ") but its _FoxholeOffline backup folder is gone - Steam's Verify repairs it");
                Status("Your Foxhole is still changed (" + changed + ") and the app's backup folder in the game is gone. In Steam: right-click Foxhole > " +
                       "Properties > Installed Files > \"Verify integrity of game files\", then press RESTORE THE GAME - the app checks again.");
                RefreshTrace(false);
                return;
            }
            int code = SandboxStep("revert", game);
            if (code != 0) RefreshTrace(false);
            if (code != 0) { Status("Could not put Foxhole back to original " + when + " (code " + code + ") - RESTORE THE GAME tries again; Steam's Verify also repairs it."); return; }
            try { File.Delete(OfflineMarker); } catch { }
            RefreshTrace(false);
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
            try { if (step != "revert") throw new Exception("not in this build"); revertNote = Sandbox.Revert(game); return 0; }
            catch (Exception ex) { Say("sandbox " + step + ": " + ex.Message); return 1; }
        }

        void OfflineRestoreClick()
        {
            if (offlineBusy) return;
            string g = MeGameDir();
            bool leftovers = g != "" && Sandbox.HasLeftovers(g);           // (files of the last session still waiting: Restore tries them again)
            // (no sandbox in this build) what an old one left: the marker, its install record, leftovers, or a positively patched game
            bool ok = false; string tr = null;
            try { if (g != "") tr = Sandbox.Trace(g, File.Exists(OfflineMarker), out ok); } catch { }
            if (tr == null || !ok)
            {
                MessageBox.Show(this, tr == null ? "Nothing to restore: the app finds no change of the offline sandbox in your Foxhole." : "Your Foxhole is changed: " + tr + ". The app does not change that.",
                    PUBLIC_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshTrace(false);
                return;
            }
            leftovers = true;
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
