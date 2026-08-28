// Checks the standalone SWP protocol reference: the frame codec's golden vectors, a round-trip fuzz,
// and the SHDLC / ACT control-field encodings. No Renode involved - this code is not part of the
// peripherals. See run via selftest.sh.
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Peripherals.SWP;

public static class ReferenceSelfTest
{
    private static int failures;
    private static readonly Random Rng = new Random(7);

    public static int Main()
    {
        Console.WriteLine("== Data link layer (clause 8)");
        Check("CRC check value for \"123456789\" is 0x29B1",
            SWPFrame.ComputeCrc(Hex("313233343536373839")) == 0x29B1);
        Wire("C001", "7EC0011B7A7F");
        Wire("80DEADBEEF", "7E80DEADBE77DDE2DFC0");
        Wire("FFFFFFFF", "7EFBEFBEFBEC743DFC");
        Wire("", "7EFBEFAFE0");

        var ok = true;
        for(var n = 0; n < 64 && ok; n++)
        {
            for(var t = 0; t < 50 && ok; t++)
            {
                var payload = RandomBytes(n);
                ok = SWPFrame.TryDecode(SWPFrame.Encode(payload), out var back, out _) && back.SequenceEqual(payload);
            }
        }
        Check("round-trips 3200 random payloads of 0..63 bytes", ok);

        foreach(var awkward in new[] { "7E", "7F", "7E7F7E7F", "FFFFFFFFFFFFFFFF" })
        {
            var p = Hex(awkward);
            Check("round-trips flag-imitating payload " + awkward,
                SWPFrame.TryDecode(SWPFrame.Encode(p), out var b, out _) && b.SequenceEqual(p));
        }

        var corrupt = SWPFrame.Encode(Hex("80DEADBEEF"));
        corrupt[2] ^= 0x40;
        Check("rejects a flipped payload bit",
            !SWPFrame.TryDecode(corrupt, out _, out var why) && why.Contains("CRC mismatch"));

        Console.WriteLine();
        Console.WriteLine("== LLC control fields (clauses 10 and 11)");
        Check("I-frame carries N(S) and N(R)",
            SWPProtocol.BuildInformation(3, 5, Hex("AA"))[0] == 0x9D);
        Check("  and decodes back",
            SWPProtocol.GetSendSequence(0x9D) == 3 && SWPProtocol.GetReceiveSequence(0x9D) == 5
            && SWPProtocol.GetFrameKind(0x9D) == SWPProtocol.ShdlcFrameKind.Information);
        Check("RR is an S-frame carrying N(R)",
            SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.ReceiveReady, 1)[0] == 0xC1);
        Check("RSET is a U-frame",
            SWPProtocol.BuildUnnumbered(SWPProtocol.UnnumberedFrameModifier.Reset)[0] == 0xF9);
        Check("ACT_POWER_MODE encodes the mode and the FR bit",
            SWPProtocol.BuildActPowerMode(SWPPowerMode.FullPower, true)[1] == 0x03);
        Check("Describe names an I-frame", SWPProtocol.Describe(Hex("81AA")).StartsWith("I   N(S)=0 N(R)=1"));
        Check("Describe names ACT_READY", SWPProtocol.Describe(Hex("03")) == "ACT_READY");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL REFERENCE CHECKS PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static void Wire(string payload, string expected)
    {
        var got = string.Concat(SWPFrame.Encode(Hex(payload)).Select(x => x.ToString("X2")));
        Check("frames " + (payload.Length == 0 ? "(empty)" : payload) + " as " + expected, got == expected, got);
    }

    private static byte[] Hex(string hex) { return Antmicro.Renode.Utilities.Misc.HexStringToByteArray(hex); }

    private static byte[] RandomBytes(int n)
    {
        var d = new byte[n];
        Rng.NextBytes(d);
        return d;
    }

    private static void Check(string what, bool ok, string detail = null)
    {
        if(!ok)
        {
            failures++;
        }
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + (ok || detail == null ? "" : "  (got " + detail + ")"));
    }
}
