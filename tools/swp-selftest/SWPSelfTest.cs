// Self-test for the SWP models. They are a TRANSPORT: the checks below exercise power gating,
// full-duplex byte carriage in both directions, unsolicited data from the target, transparency
// (bytes come out exactly as they went in), and the raw trace. There is deliberately nothing here
// about framing, CRC, ACT or SHDLC - those layers are not in the peripherals. The reference
// implementation of them in tools/swp-reference/ is exercised by tools/swp-reference/selftest.
//
// It compiles against the stubs in RenodeStubs.cs rather than a Renode checkout, so it runs in
// seconds with nothing but Mono installed - see run.sh. It complements, and does not replace, the
// robot suites in renode-overlay/tests/peripherals/, which are what exercise Renode itself.
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Mocks;
using Antmicro.Renode.Peripherals.SWP;

public static class SWPSelfTest
{
    private static int failures;
    private static readonly Random Rng = new Random(1234);

    public static int Main()
    {
        Power();
        Transport();
        Unsolicited();
        Trace();
        Transparency();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL C# SCENARIOS PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ----------------------------------------------------------------------------------------
    private static void Power()
    {
        Section("Power - the CLF owns it, and it gates the wire");

        var uicc = new DummySWPTarget();
        var clf = Build(uicc);

        Check("a line starts unpowered", !clf.Powered && !uicc.Powered);
        Logger.Entries.Clear();
        Check("a transfer on an unpowered line carries nothing",
            clf.Transfer(0, Hex("DEAD")).Length == 0 && uicc.ReceivedCount == 0);
        Check("  and it says why", Logged("is not powered"));

        clf.PowerUp(0);
        Check("PowerUp powers both sides", clf.Powered && uicc.Powered);
        Check("  and exchanges no bytes doing it - there is no activation sequence",
            clf.BytesSent == 0 && clf.BytesReceived == 0 && uicc.BytesReceived == 0 && uicc.BytesSent == 0);

        clf.Transfer(0, Hex("01"));
        clf.PowerDown(0);
        Check("PowerDown unpowers both sides", !clf.Powered && !uicc.Powered);
        Check("  and the target drops its per-session state", uicc.LastReceivedHex == "[]");
        Check("re-powering works", Reapply(clf, uicc));

        Logger.Entries.Clear();
        Check("a missing line is refused", clf.Transfer(7, Hex("AA")).Length == 0);
        Check("  and it says why", Logged("No SWP target registered on line 7"));
    }

    // ----------------------------------------------------------------------------------------
    private static void Transport()
    {
        Section("Full-duplex byte carriage");

        var uicc = new DummySWPTarget();
        var clf = Build(uicc);
        clf.PowerUp(0);

        Check("bytes driven on S1 reach the target",
            clf.Transfer(0, Hex("DEADBEEF")).Length == 0 && uicc.LastReceivedHex == "[0xDE, 0xAD, 0xBE, 0xEF]");

        uicc.EnqueueResponseHex("010203");
        Check("bytes the target drives on S2 come back in the same slot",
            clf.TransferHex(0, "AA") == "[0x1, 0x2, 0x3]");

        uicc.EnqueueResponseHex("77");
        Check("an empty S1 slot still lets the target talk (Receive)",
            clf.ReceiveHex(0) == "[0x77]");

        Check("byte counters add up",
            clf.BytesSent == 4 + 1 + 0 && clf.BytesReceived == 3 + 1);

        var echo = new EchoSWPDevice();
        var clfEcho = Build(echo);
        clfEcho.PowerUp(0);
        var all = true;
        for(var i = 0; i < 200; i++)
        {
            var payload = RandomBytes(1 + (i % 64));
            all &= clfEcho.Transfer(0, payload).SequenceEqual(payload);
        }
        Check("200 echo round trips carry every byte intact", all);

        var big = RandomBytes(4096);
        Check("a 4096-byte block is carried whole, with no size limit imposed",
            clfEcho.Transfer(0, big).SequenceEqual(big));
    }

    // ----------------------------------------------------------------------------------------
    private static void Unsolicited()
    {
        Section("The target driving S2 unprompted");

        var uicc = new DummySWPTarget();
        var clf = Build(uicc);
        clf.PowerUp(0);

        Check("IRQ is clear to start", !clf.IRQ.IsSet);
        uicc.SendDataHex("112233");
        Check("unsolicited data raises IRQ", clf.IRQ.IsSet);
        Check("  carrying the bytes and the line",
            clf.LastReceivedHex == "[0x11, 0x22, 0x33]" && clf.LastReceivedLine == 0);
        clf.AcknowledgeInterrupt();
        Check("the interrupt can be acknowledged", !clf.IRQ.IsSet);

        clf.PowerDown(0);
        Logger.Entries.Clear();
        uicc.SendDataHex("44");
        Check("an unpowered target cannot drive S2", !clf.IRQ.IsSet && Logged("not powered"));
    }

    // ----------------------------------------------------------------------------------------
    private static void Trace()
    {
        Section("Raw byte trace on the target");

        var uicc = new DummySWPTarget();
        var clf = Build(uicc);
        clf.PowerUp(0);
        uicc.EnqueueResponseHex("BB");
        clf.Transfer(0, Hex("AA"));
        uicc.SendDataHex("CC");

        var lines = uicc.TraceHex.Split('\n');
        Check("the trace holds every block, both directions", lines.Length == 3, lines.Length + " lines");
        Check("  in, with the raw bytes", lines[0].Trim() == "in   AA", lines[0]);
        Check("  out, answered in the same slot", lines[1].Trim() == "out  BB", lines[1]);
        Check("  out, unsolicited", lines[2].Trim() == "out  CC", lines[2]);
        Check("last in / last out are readable",
            uicc.LastReceivedHex == "[0xAA]" && uicc.LastSentHex == "[0xCC]");

        uicc.ClearTrace();
        Check("the trace can be cleared", uicc.TraceHex == "(nothing traced)");

        var quiet = new DummySWPTarget { TraceDepth = 0 };
        var clfQuiet = Build(quiet);
        clfQuiet.PowerUp(0);
        clfQuiet.Transfer(0, Hex("01"));
        Check("TraceDepth 0 disables recording", quiet.TraceHex == "(nothing traced)");
        Check("  but the last block stays observable", quiet.LastReceivedHex == "[0x1]");

        var capped = new DummySWPTarget { TraceDepth = 2 };
        var clfCapped = Build(capped);
        clfCapped.PowerUp(0);
        clfCapped.Transfer(0, Hex("01"));
        clfCapped.Transfer(0, Hex("02"));
        clfCapped.Transfer(0, Hex("03"));
        Check("the trace is bounded by TraceDepth", capped.TraceHex.Split('\n').Length == 2);
    }

    // ----------------------------------------------------------------------------------------
    private static void Transparency()
    {
        Section("Transparency - the transport adds and removes nothing");

        var echo = new EchoSWPDevice();
        var clf = Build(echo);
        clf.PowerUp(0);

        // Bytes that a framing layer would have to escape or stuff must pass through untouched,
        // because this transport does no framing at all.
        foreach(var awkward in new[] { "7E", "7F", "7E7F7E7F", "FFFFFFFFFFFFFFFF", "00", "0000000000" })
        {
            var payload = Hex(awkward);
            Check("carries " + awkward + " unchanged", clf.Transfer(0, payload).SequenceEqual(payload));
        }

        var every = Enumerable.Range(0, 256).Select(x => (byte)x).ToArray();
        Check("carries all 256 byte values unchanged", clf.Transfer(0, every).SequenceEqual(every));

        var target = clf.GetTarget(0);
        Check("an empty transfer is legal and carries nothing", target.Transfer(new byte[0]).Length == 0);
    }

    // ----------------------------------------------------------------------------------------
    private static bool Reapply(SimpleSWPController clf, DummySWPTarget uicc)
    {
        clf.PowerUp(0);
        uicc.EnqueueResponseHex("42");
        return clf.TransferHex(0, "11") == "[0x42]";
    }

    private static SimpleSWPController Build(ISWPPeripheral target)
    {
        var controller = new SimpleSWPController(null);
        controller.Register(target, new NumberRegistrationPoint<int>(0));
        return controller;
    }

    private static byte[] Hex(string hex) { return Antmicro.Renode.Utilities.Misc.HexStringToByteArray(hex); }

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        Rng.NextBytes(data);
        return data;
    }

    private static bool Logged(string fragment)
    {
        return Logger.Entries.Any(x => x.Contains(fragment));
    }

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
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
