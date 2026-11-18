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
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FoxholeModLauncher
{
    partial class Launcher : Form
    {
        MachineFacts machine;                 // (null until read)
        string machineClass = "", machineLine = "";
        bool belowWarned;

        // ---- reading the PC
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow; public int LuidHigh;
            public uint Flags;
        }
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1Fn(IntPtr self, out DXGI_ADAPTER_DESC1 desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
        static T VtFn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, slot * IntPtr.Size), typeof(T)) as T;
        }
        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)), dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);

        // every hardware adapter (IDXGIFactory1::EnumAdapters1 = vtable slot 12, IDXGIAdapter1::GetDesc1 = slot 10, Release = 2)
        static List<DXGI_ADAPTER_DESC1> Adapters()
        {
            var list = new List<DXGI_ADAPTER_DESC1>();
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            IntPtr f;
            if (CreateDXGIFactory1(ref iid, out f) != 0 || f == IntPtr.Zero) return list;
            try
            {
                var enumAdapters = VtFn<EnumAdapters1Fn>(f, 12);
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr a;
                    if (enumAdapters(f, i, out a) != 0 || a == IntPtr.Zero) break;     // (DXGI_ERROR_NOT_FOUND: no more)
                    try
                    {
                        DXGI_ADAPTER_DESC1 d;
                        if (VtFn<GetDesc1Fn>(a, 10)(a, out d) == 0 && (d.Flags & 2) == 0 && d.VendorId != 0x1414) list.Add(d);   // (not the software one)
                    }
                    finally { VtFn<ReleaseFn>(a, 2)(a); }
                }
            }
            finally { VtFn<ReleaseFn>(f, 2)(f); }
            return list;
        }

        static MachineFacts ReadMachine()
        {
            var m = new MachineFacts();
            ulong kb;
            try { if (GetPhysicallyInstalledSystemMemory(out kb) && kb > 0) m.RamGb = Math.Round(kb / 1048576.0, 1); } catch { }
            if (m.RamGb <= 0)
            {
                var ms = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ms)) m.RamGb = Math.Ceiling(ms.ullTotalPhys / 1073741824.0);   // (what Windows sees: a bit under the installed amount)
            }
            m.Cores = Environment.ProcessorCount;
            try
            {
                var sys = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)));
                m.SystemFreeGb = Math.Round(sys.AvailableFreeSpace / 1073741824.0, 1);
            }
            catch { }
            try
            {
                bool picked = false, pickedDedicated = false;
                foreach (var d in Adapters())
                {
                    double ded = d.DedicatedVideoMemory.ToUInt64() / 1073741824.0;
                    bool igpu = MachineClass.IsIntegrated(d.Description, d.VendorId, ded);
                    m.Adapters.Add(d.Description.Trim() + " (" + (igpu ? "integrated" : "dedicated") + ", " + ded.ToString("0.0") + " GB own memory, vendor " +
                                   d.VendorId.ToString("x4") + ")");
                    // (Unreal 4.24 picks the first dedicated adapter, else the first one)
                    if (!picked || (!igpu && !pickedDedicated))
                    {
                        picked = true; pickedDedicated = !igpu;
                        m.GpuKnown = true; m.Gpu = d.Description.Trim(); m.Integrated = igpu; m.VramGb = Math.Round(ded, 1);
                    }
                }
            }
            catch { }
            return m;
        }

        MachineRules Rules()
        {
            // (the manifest's "machine" thresholds - launcher.json may override them for tests)
            object o;
            var lc = ReadJson(Path.Combine(Dir, "launcher.json"));
            if (lc.TryGetValue("machine", out o) && o is Dictionary<string, object>) return MachineRules.From((Dictionary<string, object>)o);
            var man = ReadJson(Path.Combine(Dir, "manifest.json"));
            if (man.TryGetValue("machine", out o) && o is Dictionary<string, object>) return MachineRules.From((Dictionary<string, object>)o);
            return new MachineRules();
        }

        // at start (a background thread): read, classify, say it; the mode follows the class until the player chooses
        void CheckMachine()
        {
            try
            {
                var m = ReadMachine();
                var reasons = new List<string>();
                string cls = MachineClass.Classify(m, Rules(), reasons);
                machine = m; machineClass = cls; machineLine = MachineClass.Line(cls, reasons);
                Say("this PC: " + machineLine + " (" + m.RamGb.ToString("0.#") + " GB RAM, " + (m.GpuKnown ? m.Gpu + ", " + m.VramGb.ToString("0.#") + " GB" : "graphics unknown") +
                    ", " + m.Cores + " threads)");
                foreach (var a in m.Adapters) Say("  graphics: " + a);
                if (!Options().ContainsKey("lowspec")) Say("low-spec mode " + (LowSpecOn() ? "ON by itself for this PC" : "off") + " (Settings)");
                SyncLowSpec(false);
                Ui(Redraw);
            }
            catch (Exception ex) { Say("this PC could not be read: " + ex.Message); }
        }

        // ---- the mode
        bool LowSpecAuto() { return machineClass != "" && machineClass != MachineClass.OK; }
        bool LowSpecOn()
        {
            var o = Options();
            return o.ContainsKey("lowspec") ? On(o, "lowspec") : LowSpecAuto();
        }

        void ToggleLowSpec()
        {
            bool now = !LowSpecOn();
            SetOption("lowspec", now);
            Say("option lowspec " + (now ? "on" : "off"));
            SyncLowSpec(true);
            Redraw();
        }

        // (FOX_LAUNCHER_GAME_SETTINGS: another file - tests, never the player's own)
        static string GameSettingsPath
        {
            get
            {
                string t = Environment.GetEnvironmentVariable("FOX_LAUNCHER_GAME_SETTINGS");
                return !string.IsNullOrEmpty(t) ? t : Path.Combine(SavedDir, "Config", "WindowsNoEditor", "GameUserSettings.ini");
            }
        }
        static readonly string LowSpecBackupDir = Path.Combine(DataDir, "lowspec_backup");
        static bool FoxholeRunning() { return Process.GetProcessesByName("War-Win64-Shipping").Length > 0; }

        // kit\lowspec.txt for the kit's Join, and the game's settings: lowest once when on, the player's own back when off. tell = say it
        // in the status line (a click in OPTIONS)
        void SyncLowSpec(bool tell)
        {
            bool on = LowSpecOn();
            try
            {
                string flag = Path.Combine(Kit, "lowspec.txt");
                if (on) { Directory.CreateDirectory(Kit); File.WriteAllText(flag, "on\r\n" + (machineLine != "" ? "this PC: " + machineLine + "\r\n" : "")); }
                else if (File.Exists(flag)) File.Delete(flag);
            }
            catch (Exception ex) { Say("low-spec mode: " + ex.Message); }
            string backup = Path.Combine(LowSpecBackupDir, "GameUserSettings.ini");
            string absent = Path.Combine(LowSpecBackupDir, "none.txt");
            bool applied = File.Exists(backup) || File.Exists(absent);
            if (on == applied) { if (tell) Status(on ? "Low-spec mode on - takes effect at the next PLAY." : "Low-spec mode off - takes effect at the next PLAY."); return; }
            if (FoxholeRunning() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOX_LAUNCHER_GAME_SETTINGS")))   // (a test file: no game writes it)
            {
                Say("low-spec mode: the game's graphics settings are changed when Foxhole is closed (it writes them when it closes) - at the next PLAY");
                if (tell) Status("Low-spec mode " + (on ? "on" : "off") + " - graphics settings change once Foxhole is closed (next PLAY).");
                return;
            }
            try
            {
                string gus = GameSettingsPath;
                if (on)
                {
                    Directory.CreateDirectory(LowSpecBackupDir);
                    if (File.Exists(gus)) File.Copy(gus, backup, true); else File.WriteAllText(absent, "the game had no settings file before low-spec mode\r\n");
                    Directory.CreateDirectory(Path.GetDirectoryName(gus));
                    File.WriteAllText(gus, MachineClass.LowSpecSettings(File.Exists(gus) ? File.ReadAllText(gus) : ""), Encoding.ASCII);
                    Say("low-spec mode: the game's graphics set to the lowest preset (your own settings kept in lowspec_backup)");
                    if (tell) Status("Low-spec mode on: lowest graphics settings, lighter mod client - at the next PLAY.");
                }
                else
                {
                    if (File.Exists(backup)) { File.Copy(backup, gus, true); File.Delete(backup); }
                    else if (File.Exists(absent)) { if (File.Exists(gus)) File.Delete(gus); File.Delete(absent); }
                    try { Directory.Delete(LowSpecBackupDir); } catch { }
                    Say("low-spec mode off: your own graphics settings are back");
                    if (tell) Status("Low-spec mode off: your own graphics settings are back.");
                }
            }
            catch (Exception ex) { Say("low-spec mode: the game's settings could not be changed - " + ex.Message); }
        }

        // PLAY on a PC below the minimum: said plainly once per launcher start; the player may still try
        bool ConfirmBelowMinimum()
        {
            if (machineClass != MachineClass.BELOW || belowWarned) return true;
            bool go = false;
            Invoke((Action)(() => go = MessageBox.Show(this,
                "This PC is below what Foxhole needs: " + machineLine.Substring(Math.Min(machineLine.Length, MachineClass.BELOW.Length + 3)) + ".\n\n" +
                "The game is unlikely to run on it - it may close at start or take many minutes to load. Low-spec mode (Settings) makes it as " +
                "light as it can be, but it cannot make up for missing memory or graphics power. Close every other program before trying.\n\n" +
                "Try anyway?", "This PC is below the minimum", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes));
            belowWarned = go;
            if (!go) Status("Not started - this PC is below the minimum (see Settings).");
            return go;
        }

        // facts.txt lines (problem reports)
        void AddMachineFacts(StringBuilder f)
        {
            var m = machine ?? ReadMachine();
            if (machine == null) { var r = new List<string>(); machineClass = MachineClass.Classify(m, Rules(), r); machineLine = MachineClass.Line(machineClass, r); }
            f.AppendLine("machine class: " + machineLine);
            f.AppendLine("ram: " + m.RamGb.ToString("0.#") + " GB installed");
            f.AppendLine("gpu: " + (m.GpuKnown ? m.Gpu + (m.Integrated ? " (integrated)" : " (dedicated)") : "unknown"));
            f.AppendLine("vram: " + (m.GpuKnown ? m.VramGb.ToString("0.#") + " GB dedicated" : "unknown"));
            foreach (var a in m.Adapters) f.AppendLine("graphics adapter: " + a);
            f.AppendLine("windows drive free: " + (m.SystemFreeGb >= 0 ? m.SystemFreeGb.ToString("0.#") + " GB" : "unknown"));
            f.AppendLine("low-spec mode: " + (LowSpecOn() ? "on" : "off") + (Options().ContainsKey("lowspec") ? " (set by the player)" : " (by itself, from the class)"));
        }

        // the OPTIONS row's second line and the main screen's warning
        string LowSpecRowText()
        {
            if (machineClass == "") return "A lighter game for weak PCs (checking this PC...).";
            if (machine == null) return "This PC: " + machineLine;
            // (short: the whole reason is in DETAILS, problem reports and the warning on PLAY)
            return "This PC: " + machineClass + " - " + machine.RamGb.ToString("0.#") + " GB RAM, " +
                   (!machine.GpuKnown ? "graphics unknown" : machine.Integrated ? "integrated graphics" : machine.VramGb.ToString("0.#") + " GB graphics card");
        }

        // (dev: "--machine <file>" writes what the launcher reads of this PC, nothing else is done)
        static bool MachineCommandLine(string[] args)
        {
            // (dev: "--lowspec on|off|auto" - the option set and synced as a click in OPTIONS does, no window; with FOX_LAUNCHER_GAME_SETTINGS
            //  pointing at a test file)
            if (args.Length >= 2 && args[0] == "--lowspec")
            {
                Headless = true;
                var t = new Launcher();
                t.CheckMachine();
                var o = ReadJson(OptionsPath);
                if (args[1] == "auto") o.Remove("lowspec"); else o["lowspec"] = args[1] == "on";
                File.WriteAllText(OptionsPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(o));
                t.SyncLowSpec(false);
                return true;
            }
            if (args.Length < 2 || args[0] != "--machine") return false;
            var m = ReadMachine();
            var reasons = new List<string>();
            var l = new Launcher();
            string cls = MachineClass.Classify(m, l.Rules(), reasons);
            var f = new StringBuilder();
            l.machine = m; l.machineClass = cls; l.machineLine = MachineClass.Line(cls, reasons);
            l.AddMachineFacts(f);
            f.AppendLine("cpu threads: " + m.Cores);
            File.WriteAllText(args[1], f.ToString());
            return true;
        }
    }
}
