using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LongRangeSupport
{
    // Only assassin_multi bytecode is modified; native machine code is never patched.
    public static class PatchLayout
    {
        public const uint MissionHash = 0x4D2E3772;
        public static bool IsMissionIdentity(uint hash, string name)
        {
            return hash == MissionHash && string.Equals(name, "assassin_multi", StringComparison.OrdinalIgnoreCase);
        }
        public static int[] Match(byte[] data, string pattern)
        {
            string[] fields = pattern.Split(' ');
            int[] bytes = Array.ConvertAll(fields, s => s == "??" ? -1 : Convert.ToInt32(s, 16));
            var hits = new List<int>();
            for (int i = 0; i <= data.Length - bytes.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < bytes.Length; j++)
                    if (bytes[j] >= 0 && data[i + j] != bytes[j]) { ok = false; break; }
                if (ok) hits.Add(i);
            }
            return hits.ToArray();
        }

        public static List<Change> Create(byte[] code, float range, bool preventEscape, int activeStage = -1)
        {
            if (float.IsNaN(range) || range < 500 || range > 10000)
                throw new InvalidDataException("Range must be 500..10000 metres.");
            if (activeStage < -1 || activeStage > 3) throw new InvalidDataException("Invalid target stage.");
            // Paired comparisons of destination and spawn position, followed by OR.
            string pattern = "74 38 00 40 06 32 72 5D ?? ?? ?? 29 ?? ?? ?? ?? 18 2A 06 56 12 00 74 38 00 40 01 32 72 5D ?? ?? ?? 29 ?? ?? ?? ?? 18 20 56 1F 00";
            int[] hits = Match(code, pattern);
            if (hits.Length != 4) throw new InvalidDataException("Expected exactly four mission spawn gates, found " + hits.Length);
            var changes = new List<Change>();
            for (int i = 0; i < 4; i++)
            {
                int p = hits[i];
                if (i > 0 && p - hits[i - 1] != 84)
                    throw new InvalidDataException("Unexpected mission gate layout.");
                if (p < 7 || code[p - 7] != 0x50 || code[p - 4] != 0x71 + i)
                    throw new InvalidDataException("Unexpected target-stage selector.");
                if (code[p - 6] != code[hits[0] - 6] || code[p - 5] != code[hits[0] - 5])
                    throw new InvalidDataException("Stage selectors do not use the same local.");
                for (int j = 0; j < 3; j++)
                    if (code[p + 8 + j] != code[p + 30 + j] || code[p + 8 + j] != code[hits[0] + 8 + j])
                        throw new InvalidDataException("Distance helper calls disagree.");
                float expected = i == 3 ? 500f : 400f;
                foreach (int offset in new int[] { p + 12, p + 34 })
                {
                    if (BitConverter.ToSingle(code, offset) != expected)
                        throw new InvalidDataException("Spawn gate was changed by another mod or game update.");
                    changes.Add(new Change(offset, Slice(code, offset, 4), BitConverter.GetBytes(activeStage < 0 || activeStage == i ? range : expected)));
                }
            }
            if (preventEscape)
            {
                // Three-argument distance/visibility escape helper. Keep ENTER's frame intact.
                int[] escape = Match(code, "2D 03 07 00 00 38 00 2C 05 ?? ?? 56 95 00 38 00 2C 05 ?? ?? 06 56 8B 00 38 00 71 2C 09 ?? ?? 56 5A 00");
                if (escape.Length != 1) throw new InvalidDataException("Escape helper signature is not unique.");
                changes.Add(new Change(escape[0] + 5, Slice(code, escape[0] + 5, 4), new byte[] { 0x71, 0x2E, 3, 1 }));
            }
            return changes;
        }

        public static byte[] Slice(byte[] bytes, int offset, int length)
        {
            byte[] result = new byte[length]; Array.Copy(bytes, offset, result, 0, length); return result;
        }
        public static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    public sealed class Change
    {
        public readonly int Offset;
        public readonly byte[] Before, After;
        public Change(int offset, byte[] before, byte[] after) { Offset = offset; Before = before; After = after; }
    }

    public sealed class MissionPatch
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        readonly Action<string> log;
        long table, patchedProgram;
        long[] patchedPages;
        int patchedLength;
        List<Change> changes;
        public bool Applied { get { return changes != null; } }
        public int AppliedStage { get; private set; }
        public MissionPatch(Action<string> logger) { log = logger; }

        static byte[] Read(long address, int length)
        {
            if (address < 0x10000 || length < 0 || length > 0x4000000) throw new InvalidDataException("Invalid memory read bounds.");
            byte[] data = new byte[length]; UIntPtr count;
            if (!ReadProcessMemory(GetCurrentProcess(), new IntPtr(address), data, (UIntPtr)length, out count) || count.ToUInt64() != (ulong)length)
                throw new IOException("Game memory is unavailable.");
            return data;
        }
        static void Write(long address, byte[] data)
        {
            UIntPtr count;
            if (!WriteProcessMemory(GetCurrentProcess(), new IntPtr(address), data, (UIntPtr)data.Length, out count) || count.ToUInt64() != (ulong)data.Length)
                throw new IOException("Mission bytecode write failed.");
        }

        public void LocateTable()
        {
            var module = Process.GetCurrentProcess().MainModule;
            long start = module.BaseAddress.ToInt64();
            byte[] dos = Read(start, 64);
            int pe = BitConverter.ToInt32(dos, 60);
            byte[] header = Read(start + pe, 24);
            int sections = BitConverter.ToUInt16(header, 6), optional = BitConverter.ToUInt16(header, 20);
            if (sections < 1 || sections > 96) throw new InvalidDataException("Invalid PE sections.");
            var candidates = new List<long>();
            for (int s = 0; s < sections; s++)
            {
                byte[] h = Read(start + pe + 24 + optional + 40 * s, 40);
                string name = System.Text.Encoding.ASCII.GetString(h, 0, 8).TrimEnd('\0');
                if (name != ".text") continue;
                int size = BitConverter.ToInt32(h, 8), rva = BitConverter.ToInt32(h, 12);
                if (size <= 0 || size > module.ModuleMemorySize || rva < 0 || (long)rva + size > module.ModuleMemorySize)
                    throw new InvalidDataException("Invalid executable text section.");
                byte[] text = Read(start + rva, size);
                foreach (int hit in PatchLayout.Match(text, "48 03 15 ?? ?? ?? ?? 4C 23 C2 49 8B 08"))
                    candidates.Add(start + rva + hit + 7 + BitConverter.ToInt32(text, hit + 3));
            }
            if (candidates.Count != 1) throw new InvalidDataException("Script-table signature must match once; found " + candidates.Count);
            table = candidates[0];
            log("Located Legacy script table.");
        }

        long FindProgram()
        {
            byte[] h = Read(table, 28);
            long entries = BitConverter.ToInt64(h, 0); int count = BitConverter.ToInt32(h, 24);
            if (entries == 0 || count == 0) return 0;
            if (count < 0 || count > 4096) throw new InvalidDataException("Script table count is invalid.");
            byte[] items = Read(entries, count * 16);
            for (int i = 0; i < count; i++)
                if (BitConverter.ToUInt32(items, i * 16 + 12) == PatchLayout.MissionHash)
                {
                    long program = BitConverter.ToInt64(items, i * 16);
                    if (program == 0) return 0;
                    byte[] p = Read(program, 0x78);
                    if (BitConverter.ToUInt32(p, 0x58) != PatchLayout.MissionHash)
                        throw new InvalidDataException("Mission program hash mismatch.");
                    byte[] nameBytes = Read(BitConverter.ToInt64(p, 0x60), 15);
                    int nameEnd = Array.IndexOf(nameBytes, (byte)0);
                    string name = System.Text.Encoding.ASCII.GetString(nameBytes, 0, nameEnd < 0 ? nameBytes.Length : nameEnd);
                    if (!PatchLayout.IsMissionIdentity(BitConverter.ToUInt32(p, 0x58), name))
                        throw new InvalidDataException("Mission name mismatch: expected assassin_multi (case-insensitive), found '" + name + "'.");
                    return program;
                }
            return 0;
        }

        static long[] Pages(long program, out int length)
        {
            byte[] h = Read(program, 0x30); length = BitConverter.ToInt32(h, 0x1C);
            if (length < 30000 || length > 1000000) throw new InvalidDataException("Unexpected mission code size.");
            int count = (length + 0x3FFF) / 0x4000;
            byte[] p = Read(BitConverter.ToInt64(h, 0x10), count * 8);
            long[] result = new long[count];
            for (int i = 0; i < count; i++) result[i] = BitConverter.ToInt64(p, i * 8);
            return result;
        }
        static byte[] ReadCode(long[] pages, int length)
        {
            var result = new byte[length];
            for (int i = 0; i < pages.Length; i++)
                Array.Copy(Read(pages[i], Math.Min(0x4000, length - i * 0x4000)), 0, result, i * 0x4000, Math.Min(0x4000, length - i * 0x4000));
            return result;
        }
        static void WriteCode(long[] pages, int offset, byte[] data)
        {
            // Bytecode operands may cross a 16 KiB page boundary.
            int done = 0;
            while (done < data.Length)
            {
                int pos = offset + done, amount = Math.Min(data.Length - done, 0x4000 - (pos & 0x3FFF));
                Write(pages[pos >> 14] + (pos & 0x3FFF), PatchLayout.Slice(data, done, amount)); done += amount;
            }
        }

        public bool Apply(float range, bool preventEscape, int stage)
        {
            if (table == 0) LocateTable();
            long program = FindProgram();
            if (program == 0) return false;
            if (Applied && AppliedStage == stage) return true;
            if (Applied) Restore();
            int length; long[] pages = Pages(program, out length);
            byte[] code = ReadCode(pages, length);
            List<Change> plan = PatchLayout.Create(code, range, preventEscape, stage);
            int written = 0;
            try
            {
                foreach (Change c in plan) { written++; WriteCode(pages, c.Offset, c.After); }
                byte[] actual = ReadCode(pages, length);
                foreach (Change c in plan) if (!PatchLayout.Equal(PatchLayout.Slice(actual, c.Offset, c.After.Length), c.After))
                    throw new IOException("Patch verification failed.");
            }
            catch
            {
                for (int i = written - 1; i >= 0; i--) WriteCode(pages, plan[i].Offset, plan[i].Before);
                throw;
            }
            changes = plan; patchedProgram = program; patchedPages = pages; patchedLength = length; AppliedStage = stage;
            log("Widened target " + (stage + 1) + " spawn gate; other stages retain original gates" + (preventEscape ? "; distance-escape guard enabled" : "") + "; range=" + range + "; codeLength=" + length);
            return true;
        }

        public void Restore()
        {
            if (!Applied) return;
            try
            {
                long program = FindProgram();
                if (program != patchedProgram) { log("Mission unloaded; discarded obsolete patch references."); return; }
                int length; long[] pages = Pages(program, out length);
                if (length != patchedLength || pages.Length != patchedPages.Length) return;
                for (int i = 0; i < pages.Length; i++) if (pages[i] != patchedPages[i]) return;
                byte[] code = ReadCode(pages, length);
                foreach (Change c in changes)
                    if (!PatchLayout.Equal(PatchLayout.Slice(code, c.Offset, c.After.Length), c.After))
                        throw new InvalidDataException("Patch changed externally; refusing to overwrite another mod.");
                foreach (Change c in changes) WriteCode(pages, c.Offset, c.Before);
                log("Restored original mission bytecode.");
            }
            finally { changes = null; patchedPages = null; patchedProgram = 0; }
        }
    }
}
