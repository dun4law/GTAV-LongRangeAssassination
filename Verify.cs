using System;
using System.IO;
using LongRangeSupport;
class Verify
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Main(string[] args)
    {
        byte[] file = File.ReadAllBytes(args[0]);
        int nameOffset = BitConverter.ToInt32(file, 0x60) & 0xFFFFFF;
        int nameEnd = Array.IndexOf(file, (byte)0, nameOffset);
        string referenceName = System.Text.Encoding.ASCII.GetString(file, nameOffset, nameEnd - nameOffset);
        uint referenceHash = BitConverter.ToUInt32(file, 0x58);
        Require(referenceName == "Assassin_Multi", "Fixture must reproduce the actual mixed-case header.");
        Require(PatchLayout.IsMissionIdentity(referenceHash, referenceName), "Actual mission header must be accepted.");
        Require(PatchLayout.IsMissionIdentity(referenceHash, "assassin_multi"), "Lowercase name must also be accepted.");
        Require(!PatchLayout.IsMissionIdentity(referenceHash, "assassin_valet"), "Different mission name must be rejected.");
        Require(!PatchLayout.IsMissionIdentity(0, referenceName), "Wrong hash must still be rejected.");
        Require(!PatchLayout.IsMissionIdentity(referenceHash, "Assassin_Multi_extra"), "A prefix match must not pass.");
        Console.WriteLine("PASS: actual mixed-case header, lowercase header, rejection of wrong mission/hash/suffix.");
        int length = BitConverter.ToInt32(file, 0x1C), table = BitConverter.ToInt32(file, 0x10) & 0xFFFFFF;
        var code = new byte[length];
        for (int p = 0; p * 0x4000 < length; p++)
            Array.Copy(file, BitConverter.ToInt32(file, table + 8 * p) & 0xFFFFFF, code, p * 0x4000, Math.Min(0x4000, length - p * 0x4000));
        var changes = PatchLayout.Create(code, 5000, true);
        Require(changes.Count == 9, "Expected 8 operands and one escape guard.");
        var patched = (byte[])code.Clone();
        foreach (var c in changes) Array.Copy(c.After, 0, patched, c.Offset, c.After.Length);
        foreach (var c in changes) Array.Copy(c.Before, 0, patched, c.Offset, c.Before.Length);
        Require(PatchLayout.Equal(patched, code), "Restore must reproduce original bytes exactly.");
        patched[changes[0].Offset] ^= 1;
        bool rejected = false;
        try { PatchLayout.Create(patched, 5000, true); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Changed spawn operands must be rejected.");
        Require(PatchLayout.Create(code, 3000, false).Count == 8, "Escape guard can be omitted.");
        for (int stage = 0; stage < 4; stage++)
        {
            var staged = PatchLayout.Create(code, 5000, false, stage);
            for (int j = 0; j < 8; j++)
                Require(BitConverter.ToSingle(staged[j].After, 0) == (j / 2 == stage ? 5000f : j / 2 == 3 ? 500f : 400f), "Only the prepared stage may spawn early.");
        }
        Require(PatchLayout.Match(new byte[0], "01 02").Length == 0, "Short input handling.");
        Console.WriteLine("PASS: reference mission gate recognition, escape guard, exact restoration, conflict rejection, optional guard, short input.");
        foreach (var c in changes) Console.WriteLine("Validated bytecode operand/body offset: 0x" + c.Offset.ToString("X"));
        if (args.Length > 1)
        {
            byte[] exe = File.ReadAllBytes(args[1]);
            int[] hits = PatchLayout.Match(exe, "48 03 15 ?? ?? ?? ?? 4C 23 C2 49 8B 08");
            Console.WriteLine("Local GTA5.exe script-table signature matches on disk: " + hits.Length + " (runtime scan is separately validated).");
        }
    }
}
