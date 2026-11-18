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
using System.Text;

public static class FhoExePatcher
{
    public const int Version = 2;
    class Sec { public string Name; public uint Va, VSize, Raw, RawSize, HeaderOff; }
    static byte[] X;
    static List<Sec> Secs;
    static ulong ImageBase;
    static Sec Text, Data;

    static uint U32(int o) { return BitConverter.ToUInt32(X, o); }
    static void W32(int o, uint v) { byte[] b = BitConverter.GetBytes(v); Array.Copy(b, 0, X, o, 4); }
    static void W32(int o, int v) { W32(o, unchecked((uint)v)); }

    static void ParsePe() {
        int nt = BitConverter.ToInt32(X, 0x3c);
        int nsec = BitConverter.ToUInt16(X, nt + 6), opt = BitConverter.ToUInt16(X, nt + 0x14);
        ImageBase = BitConverter.ToUInt64(X, nt + 0x18 + 0x18);
        Secs = new List<Sec>();
        int sh = nt + 0x18 + opt;
        for (int i = 0; i < nsec; i++, sh += 40) {
            Sec s = new Sec();
            s.Name = Encoding.ASCII.GetString(X, sh, 8).TrimEnd('\0');
            s.VSize = U32(sh + 8); s.Va = U32(sh + 12); s.RawSize = U32(sh + 16); s.Raw = U32(sh + 20); s.HeaderOff = (uint)sh;
            Secs.Add(s);
            if (s.Name == ".text") Text = s;
            if (s.Name == ".data") Data = s;
        }
    }
    static int Off(uint rva) {                                  // file offset of an rva
        foreach (Sec s in Secs) if (rva >= s.Va && rva < s.Va + Math.Min(s.VSize, s.RawSize)) return (int)(s.Raw + rva - s.Va);
        throw new Exception("rva " + rva.ToString("x") + " is not in the file");
    }
    static uint Rva(int off) {
        foreach (Sec s in Secs) if (off >= s.Raw && off < s.Raw + s.RawSize) return (uint)(s.Va + off - s.Raw);
        throw new Exception("offset " + off.ToString("x") + " is not in a section");
    }

    // "48 8B ?? .." -> the only place in .text where it matches (rva); throws when none or several
    static uint Find(string name, string pattern) {
        string[] t = pattern.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        byte[] p = new byte[t.Length]; bool[] m = new bool[t.Length];
        for (int i = 0; i < t.Length; i++) { m[i] = t[i] != "??"; if (m[i]) p[i] = Convert.ToByte(t[i], 16); }
        int a0 = 0, alen = 0;
        for (int i = 0; i < t.Length; ) {
            if (!m[i]) { i++; continue; }
            int j = i; while (j < t.Length && m[j]) j++;
            if (j - i > alen) { a0 = i; alen = j - i; }
            i = j;
        }
        int start = (int)Text.Raw, end = (int)(Text.Raw + Text.RawSize);
        int found = 0; int at = -1;
        for (int q = start + a0; q + alen <= end; q++) {
            if (X[q] != p[a0]) continue;
            bool ok = true;
            for (int k = 0; k < t.Length && ok; k++) {
                int idx = q - a0 + k;
                if (idx < start || idx >= end) ok = false;
                else if (m[k] && X[idx] != p[k]) ok = false;
            }
            if (ok) { found++; at = q - a0; if (found > 1) break; }
        }
        if (found != 1) throw new Exception(name + ": signature " + (found == 0 ? "not found" : "found more than once"));
        return Rva(at);
    }
    static uint RelTarget(uint insnRva, int relAt, int insnLen) {  // the target of a rel32 / rip-relative field
        return (uint)(insnRva + insnLen + BitConverter.ToInt32(X, Off((uint)(insnRva + relAt))));
    }
    static void Expect(string name, uint rva, string hex) {
        string[] t = hex.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int o = Off(rva);
        for (int i = 0; i < t.Length; i++)
            if (t[i] != "??" && X[o + i] != Convert.ToByte(t[i], 16)) throw new Exception(name + ": unexpected bytes");
    }
    // free space in .text: a run of int3 padding (the first one of that length from `after`, 16-aligned start)
    // hints: the caves' places in a known build (the kit's choice), used when still free padding
    static Dictionary<string, uint> Hints = new Dictionary<string, uint>();
    static uint Cave(string name, int len, uint after) {
        uint h;
        if (Hints.TryGetValue(name, out h)) {
            int o = Off(h); bool free = true;
            for (int k = 0; k < len && free; k++) if (X[o + k] != 0xCC) free = false;
            if (free) return h;
        }
        int start = Off(after), end = (int)(Text.Raw + Text.RawSize);
        for (int q = (start + 15) & ~15; q + len <= end; q += 16) {
            bool ok = q > 0 && X[q - 1] == 0xCC;                    // (inside padding, not right after code)
            for (int k = 0; k < len && ok; k++) if (X[q + k] != 0xCC) ok = false;
            if (ok) return Rva(q);
        }
        throw new Exception(name + ": no free space for its code");
    }
    static void Put(uint rva, byte[] b) { Array.Copy(b, 0, X, Off(rva), b.Length); }
    static int Rel(uint fromEnd, uint to) { return (int)to - (int)fromEnd; }

    // the standard PE checksum (as the linker / imagehlp CheckSumMappedFile compute it)
    static uint Checksum(int checksumOff) {
        ulong sum = 0;
        int n = X.Length;
        for (int i = 0; i + 1 < n; i += 2) {
            if (i == checksumOff || i == checksumOff + 2) continue;
            sum += BitConverter.ToUInt16(X, i);
            sum = (sum & 0xffff) + (sum >> 16);
        }
        if ((n & 1) != 0) { sum += X[n - 1]; sum = (sum & 0xffff) + (sum >> 16); }
        sum = (sum & 0xffff) + (sum >> 16);
        return (uint)(sum + (ulong)n);
    }

    // Patches the exe image in place; "" on success, else what failed (the image is then unusable)
    // caveHints: "name=rva,name=rva" (or empty)
    public static string Apply(byte[] exe, string caveHints) {
        try {
            X = exe;
            Hints.Clear();
            foreach (string kv in (caveHints ?? "").Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                string[] p = kv.Split('=');
                Hints[p[0].Trim()] = Convert.ToUInt32(p[1].Trim(), 16);
            }
            ParsePe();
            var log = new StringBuilder();

            // 1. .data grows by 0x360 (the space before the next section): a re-entry table and a stand-in object
            //    for the caves below live there (bss: zero-filled at load, nothing added to the file)
            uint oldData = Data.VSize, dataExt = Data.Va + oldData;
            uint nextVa = uint.MaxValue;
            foreach (Sec s in Secs) if (s.Va > Data.Va && s.Va < nextVa) nextVa = s.Va;
            if (dataExt + 0x360 > nextVa) throw new Exception("no room to grow the data section");
            W32((int)Data.HeaderOff + 8, oldData + 0x360);
            uint guardTable = dataExt, standIn = dataExt + 0x110;

            // 2. dedicated server flag (GIsClient, cleared by FoxServer on servers): the structure check at the site is
            //    skipped while it is set (clients) - cave: cmp byte [flag],0; je T; test byte [rbx+0xc9],8; je T; jmp back
            uint flagRef = Find("dedicated flag", "88 05 ?? ?? ?? ?? 0F B6 41 78 24 01 88 05 ?? ?? ?? ??");
            uint flag = RelTarget(flagRef, 2, 6);
            uint s1 = Find("structure check", "F6 83 C9 00 00 00 08 74 ?? 0F 2F B3 CC 00 00 00");
            uint t1 = (uint)(s1 + 9 + (sbyte)X[Off(s1 + 8)]);
            uint c1 = Cave("structure check", 0x1f, Text.Va);
            var b1 = new List<byte>();
            b1.AddRange(new byte[] { 0x80, 0x3D }); b1.AddRange(BitConverter.GetBytes(Rel(c1 + 7, flag))); b1.Add(0x00);
            b1.AddRange(new byte[] { 0x0F, 0x84 }); b1.AddRange(BitConverter.GetBytes(Rel(c1 + 13, t1)));
            b1.AddRange(new byte[] { 0xF6, 0x83, 0xC9, 0x00, 0x00, 0x00, 0x08 });
            b1.AddRange(new byte[] { 0x0F, 0x84 }); b1.AddRange(BitConverter.GetBytes(Rel(c1 + 26, t1)));
            b1.Add(0xE9); b1.AddRange(BitConverter.GetBytes(Rel(c1 + 31, s1 + 9)));
            Put(c1, b1.ToArray());
            var j1 = new List<byte> { 0xE9 }; j1.AddRange(BitConverter.GetBytes(Rel(s1 + 5, c1))); j1.AddRange(new byte[] { 0x90, 0x90, 0x90, 0x90 });
            Put(s1, j1.ToArray());

            // 3. re-entry guard around one call: (object, arg) pairs already in progress return at once
            uint s2 = Find("re-entry guard", "E8 ?? ?? ?? ?? 4D 85 ED 75 ?? 48 8B 5E 78 48 85 DB");
            uint f2 = RelTarget(s2, 1, 5);
            uint c2 = Cave("re-entry guard", 0x63, c1 + 0x20);
            byte[] g = HexBytes("F681980000004074554C8D1500000000498B0248C1E004498D4402084D8D5A084939C3731149390B750649395308742D4983C310EBEA49833A1073224889084889500849FF024883EC28E8000000004883C4284C8D150000000049FF0AC3E900000000");
            Array.Copy(g, 0, X, Off(c2), g.Length);
            W32(Off(c2 + 0x0c), Rel(c2 + 0x10, guardTable));         // lea r10, [table]
            W32(Off(c2 + 0x4b), Rel(c2 + 0x4f, f2));                 // call <original>
            W32(Off(c2 + 0x56), Rel(c2 + 0x5a, guardTable));         // lea r10, [table]
            W32(Off(c2 + 0x5f), Rel(c2 + 0x63, f2));                 // jmp <original> (flag not set)
            W32(Off(s2 + 1), Rel(s2 + 5, c2));

            // 4. null object at the site -> a zeroed stand-in object instead of skipping
            uint s3 = Find("stand-in object", "40 84 FF 0F 84 ?? ?? ?? ?? 4D 85 FF 0F 84 ?? ?? ?? ??");
            uint c3 = Cave("stand-in object", 0x16, c2 + 0x64);
            var b3 = new List<byte> { 0x40, 0x84, 0xFF, 0x74, 0x05, 0x4D, 0x85, 0xFF, 0x75, 0x07, 0x4C, 0x8D, 0x3D };
            b3.AddRange(BitConverter.GetBytes(Rel(c3 + 17, standIn)));
            b3.Add(0xE9); b3.AddRange(BitConverter.GetBytes(Rel(c3 + 22, s3 + 18)));
            Put(c3, b3.ToArray());
            var j3 = new List<byte> { 0xE9 }; j3.AddRange(BitConverter.GetBytes(Rel(s3 + 5, c3)));
            for (int i = 0; i < 13; i++) j3.Add(0x90);
            Put(s3, j3.ToArray());

            foreach (string v in new string[] { "", "_internal", "_iteration", "_devbranch" })
                ReplaceText("https://s3.amazonaws.com/foxhole-updates/config/client_config" + v + ".json",
                            "http://127.0.0.1:9998/client_config.json");

            // 6. connect directly to the deploy host (the Steam-session check skipped)
            uint s6 = Find("direct connect", "0F 85 ?? ?? ?? ?? 80 3D ?? ?? ?? ?? 05 48 8D 3D ?? ?? ?? ?? 72 ?? 48 8D 15 ?? ?? ?? ??");
            Put(s6, new byte[] { 0x66, 0x0F, 0x1F, 0x44, 0x00, 0x00 });

            uint s7 = Find("pak override filter", "84 C0 75 ?? 48 83 C3 10 48 8D 85 30 01 00 00 48 3B D8 75 ??");
            Put(s7 + 2, new byte[] { 0xEB });

            // 8. header checksum
            int nt = BitConverter.ToInt32(X, 0x3c), csOff = nt + 0x18 + 0x40;
            W32(csOff, Checksum(csOff));
            return "";
        } catch (Exception e) {
            return e.Message;
        }
    }

    static void ReplaceText(string from, string to) {
        byte[] f = Encoding.Unicode.GetBytes(from + "\0"), t = Encoding.Unicode.GetBytes(to);
        int found = -1, n = 0;
        for (int i = 0; i + f.Length <= X.Length; i++) {
            if (X[i] != f[0]) continue;
            int k = 0; while (k < f.Length && X[i + k] == f[k]) k++;
            if (k == f.Length) { found = i; n++; }
        }
        if (n != 1) throw new Exception("text " + from + (n == 0 ? " not found" : " found more than once"));
        for (int k = 0; k < f.Length; k++) X[found + k] = k < t.Length ? t[k] : (byte)0;
    }

    static byte[] HexBytes(string s) {
        s = s.Replace(" ", "");
        byte[] b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }
}
