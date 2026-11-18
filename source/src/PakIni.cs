// Foxhole Mod Manager (unofficial)
// Copyright (C) 2026 Foxhole Mod Manager contributors
// SPDX-License-Identifier: GPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
// version 3 as published by the Free Software Foundation. This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License (LICENSE) for more details.

using System;
using System.IO;
using System.Text;

public static class FhoPakIni
{
    public const string Path = "War/Config/DefaultEngine.ini";

    // where the file's bytes are in the pak: data offset and size (uncompressed, unencrypted entries only)
    public static long[] Locate(string pak) {
        using (FileStream f = new FileStream(pak, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            long n = f.Length;
            byte[] tail = new byte[Math.Min(n, 512)];
            f.Position = n - tail.Length; ReadAll(f, tail);
            int m = -1;
            for (int i = tail.Length - 4; i >= 0; i--) if (BitConverter.ToUInt32(tail, i) == 0x5A6F12E1u) { m = i; break; }
            if (m < 0) throw new Exception("not a pak file");
            int version = BitConverter.ToInt32(tail, m + 4);
            long indexOff = BitConverter.ToInt64(tail, m + 8), indexSize = BitConverter.ToInt64(tail, m + 16);
            if (version < 3 || version > 9) throw new Exception("pak version " + version + " is not supported");
            if (m >= 1 && tail[m - 1] != 0) throw new Exception("the pak index is encrypted");
            byte[] ix = new byte[indexSize];
            f.Position = indexOff; ReadAll(f, ix);
            int p = 0;
            string mount = FStr(ix, ref p);
            int count = BitConverter.ToInt32(ix, p); p += 4;
            for (int k = 0; k < count; k++) {
                string name = FStr(ix, ref p);
                int start = p;
                long off = BitConverter.ToInt64(ix, p), size = BitConverter.ToInt64(ix, p + 8), usize = BitConverter.ToInt64(ix, p + 16);
                uint method = BitConverter.ToUInt32(ix, p + 24); p += 28;
                p += 20;                                            // hash
                if (method != 0) { int nb = BitConverter.ToInt32(ix, p); p += 4 + 16 * nb; }
                byte flags = ix[p]; p += 5;                         // flags, compression block size
                if (name == Path) {
                    if (method != 0 || (flags & 1) != 0 || size != usize) throw new Exception(Path + " is compressed or encrypted");
                    return new long[] { off + (p - start), size };  // (each entry's header is repeated before its data)
                }
            }
            throw new Exception(Path + " is not in the pak");
        }
    }

    // vanilla / patched / unknown, by content (any build)
    public static string State(byte[] ini) {
        string s = Encoding.UTF8.GetString(ini);
        bool steamDriver = s.Contains("DriverClassName=\"SteamSockets.SteamSocketsNetDriver\"");
        bool ipDriver = s.Contains("DefName=\"GameNetDriver\",  DriverClassName=\"OnlineSubsystemUtils.IpNetDriver\"");
        if (ipDriver && !steamDriver && s.Contains("bUseSteamNetworking=false")) return "patched";
        if (steamDriver && s.Contains("+Components=OnlineSubsystemSteam.SteamAuthComponentModuleInterface")) return "vanilla";
        return "unknown";
    }

    public static byte[] MakeOffline(byte[] vanilla) {
        string s = Encoding.UTF8.GetString(vanilla);
        s = Replace1(s, "+Components=OnlineSubsystemSteam.SteamAuthComponentModuleInterface", ";Components=SteamAuth (removed: offline)");
        foreach (string def in new string[] { "GameNetDriver\",  ", "BeaconNetDriver\"," })
            s = Replace1(s, "(DefName=\"" + def + "DriverClassName=\"SteamSockets.SteamSocketsNetDriver\"",
                            "(DefName=\"" + def + "DriverClassName=\"OnlineSubsystemUtils.IpNetDriver\"");
        s = Replace1(s, "[OnlineSubsystemSteam]\r\nbEnabled=true\r\n", "[OnlineSubsystemSteam]\r\nbEnabled=true\r\nbUseSteamNetworking=false\r\n");
        byte[] b = Encoding.UTF8.GetBytes(s);
        int pad = vanilla.Length - b.Length;
        if (pad < 0) throw new Exception("the offline settings do not fit in the original's size");
        string tail = pad >= 3 ? "\r\n;" + new string(' ', pad - 3) : new string(' ', pad);
        byte[] r = new byte[vanilla.Length];
        Array.Copy(b, r, b.Length);
        Encoding.ASCII.GetBytes(tail).CopyTo(r, b.Length);
        return r;
    }

    static string Replace1(string s, string from, string to) {
        int i = s.IndexOf(from, StringComparison.Ordinal);
        if (i < 0 || s.IndexOf(from, i + 1, StringComparison.Ordinal) >= 0) throw new Exception("DefaultEngine.ini: '" + from + "' not found exactly once");
        return s.Substring(0, i) + to + s.Substring(i + from.Length);
    }
    static string FStr(byte[] b, ref int p) {
        int n = BitConverter.ToInt32(b, p); p += 4;
        string s;
        if (n < 0) { s = Encoding.Unicode.GetString(b, p, -2 * n); p += -2 * n; }
        else { s = Encoding.ASCII.GetString(b, p, n); p += n; }
        return s.TrimEnd('\0');
    }
    static void ReadAll(Stream f, byte[] b) {
        int got = 0;
        while (got < b.Length) { int r = f.Read(b, got, b.Length - got); if (r <= 0) throw new Exception("short read"); got += r; }
    }
}
