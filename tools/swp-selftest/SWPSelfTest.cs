// Self-test for the SWP models: drives the real SimpleSWPController and SimpleSWPPeripheral through
// the data link layer, the ACT activation sequence, SHDLC and the error-recovery paths, and checks the
// frame codec against golden wire vectors produced by an independent implementation.
//
// It compiles against the stubs in RenodeStubs.cs rather than a Renode checkout, so it runs in seconds
// with nothing but Mono installed - see run.sh. It complements, and does not replace, the robot suites
// in renode-overlay/tests/peripherals/, which are what exercise Renode itself.
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
        Codec();
        Activation();
        Shdlc();
        Recovery();
        FrameTrace();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL C# SCENARIOS PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ----------------------------------------------------------------------------------------
    private static void Codec()
    {
        Section("Data link layer (clause 8)");

        Check("CRC check value for \"123456789\" is 0x29B1",
            SWPFrame.ComputeCrc(Hex("313233343536373839")) == 0x29B1);

        // Golden wire vectors, computed independently by the Python reference model.
        CheckWire("C001", "7EC0011B7A7F");
        CheckWire("80DEADBEEF", "7E80DEADBE77DDE2DFC0");
        CheckWire("FFFFFFFF", "7EFBEFBEFBEC743DFC");
        CheckWire("", "7EFBEFAFE0");                  // empty payload: CRC 'FFFF', end-of-CRC stuffing exception
        CheckWire("010105100003", "7E0101051000032EA47F");   // the ACT_SYNC a default UICC sends

        var ok = true;
        for(var length = 0; length < 64 && ok; length++)
        {
            for(var trial = 0; trial < 50 && ok; trial++)
            {
                var payload = RandomBytes(length);
                ok = SWPFrame.TryDecode(SWPFrame.Encode(payload), out var decoded, out _)
                    && decoded.SequenceEqual(payload);
            }
        }
        Check("round-trips 3200 random payloads of 0..63 bytes", ok);

        foreach(var awkward in new[] { "FF", "7E", "7F", "7E7F7E7F", "FFFFFFFFFFFFFFFF", "00FF00FF7E7F" })
        {
            var payload = Hex(awkward);
            Check("round-trips a flag-imitating payload " + awkward,
                SWPFrame.TryDecode(SWPFrame.Encode(payload), out var back, out _) && back.SequenceEqual(payload));
        }

        var corrupted = SWPFrame.Encode(Hex("80DEADBEEF"));
        corrupted[2] ^= 0x40;
        Check("rejects a frame with a flipped payload bit",
            !SWPFrame.TryDecode(corrupted, out _, out var why) && why.Contains("CRC mismatch"));
        Check("rejects a wire image with no SOF",
            !SWPFrame.TryDecode(new byte[] { 0x00, 0x00 }, out _, out _));
    }

    // ----------------------------------------------------------------------------------------
    private static void Activation()
    {
        Section("ACT LLC (clause 11)");

        var uicc = new DummySWPTarget();
        var clf = Build(uicc);

        Check("starts DEACTIVATED", clf.InterfaceState == SWPInterfaceState.Deactivated
            && uicc.InterfaceState == SWPInterfaceState.Deactivated);
        Check("Activate runs the whole sequence", clf.Activate(0));
        Check("both sides reach ACTIVATED", clf.InterfaceState == SWPInterfaceState.Activated
            && uicc.InterfaceState == SWPInterfaceState.Activated);
        Check("the power mode selected in ACT_POWER_MODE reached the UICC",
            uicc.PowerMode == SWPPowerMode.FullPower);
        Check("the CLF read the maximum frame size out of ACT_SYNC",
            clf.GetTargetMaxFramePayloadSize(0) == 4096);

        var small = new DummySWPTarget { MaxFramePayloadSize = 8 };
        var clfSmall = Build(small);
        clfSmall.Activate(0);
        Check("the CLF honours a small advertised maximum",
            clfSmall.GetTargetMaxFramePayloadSize(0) == 8);
        Logger.Entries.Clear();
        clfSmall.Send(0, Hex("0102030405060708090A"));
        Check("a payload above the advertised maximum never reaches the UICC", small.ReceivedCount == 0);
        Check("  and it logs what the robot suite waits for", Logged("exceeds the 8-byte maximum"));
        clfSmall.Send(0, RandomBytes(8));
        Check("a payload at exactly the advertised maximum is accepted", small.ReceivedCount == 1);

        clf.Deactivate(0);
        Check("deactivation drops both sides", clf.InterfaceState == SWPInterfaceState.Deactivated
            && uicc.InterfaceState == SWPInterfaceState.Deactivated && !clf.LinkEstablished);
        Check("re-activation works", clf.Activate(0) && clf.LinkEstablished);
    }

    // ----------------------------------------------------------------------------------------
    private static void Shdlc()
    {
        Section("SHDLC LLC (clause 10)");

        var echo = new EchoSWPDevice();
        var clf = Build(echo);
        clf.Activate(0);

        Check("RSET/UA established the link", clf.LinkEstablished && echo.LinkEstablished);
        Check("the window was negotiated", clf.GetWindowSize(0) == 4 && echo.WindowSize == 4);

        var narrow = new EchoSWPDevice { MaxWindowSize = 2 };
        var clfNarrow = Build(narrow);
        clfNarrow.Activate(0);
        Check("the UICC can cap the window below the CLF's proposal",
            clfNarrow.GetWindowSize(0) == 2 && narrow.WindowSize == 2);

        var allMatched = true;
        for(var i = 0; i < 200; i++)
        {
            var payload = RandomBytes(1 + (i % 64));
            allMatched &= clf.Send(0, payload).SequenceEqual(payload);
        }
        Check("200 echo round trips (N(S)/N(R) wrap 25 times)", allMatched);
        Check("  with no REJ, retransmission or CRC error",
            clf.CrcErrors == 0 && clf.RejectsReceived == 0 && clf.Retransmissions == 0);

        var big = RandomBytes(2000);
        Check("a 2000-byte payload survives framing and stuffing", clf.Send(0, big).SequenceEqual(big));

        var quiet = new DummySWPTarget();
        var clfQuiet = Build(quiet);
        clfQuiet.Activate(0);
        Check("a UICC with nothing to say answers with a bare RR",
            clfQuiet.Send(0, Hex("DEAD")).Length == 0 && quiet.LastReceivedPayloadHex == "[0xDE, 0xAD]");
        quiet.EnqueueResponsePayloadHex("010203");
        Check("a queued payload is piggybacked on the acknowledgement",
            clfQuiet.SendHex(0, "BEEF") == "[0x1, 0x2, 0x3]");

        Logger.Entries.Clear();
        var unactivated = Build(new DummySWPTarget());
        Check("sending before activation is refused", unactivated.Send(0, Hex("AABB")).Length == 0);
        Check("  and it logs what the robot suite waits for",
            Logged("the SHDLC link is not established"));

        Logger.Entries.Clear();
        Check("a missing SWP line is refused", !unactivated.Activate(7));
        Check("  and it logs what the robot suite waits for",
            Logged("No SWP target registered on line 7"));
    }

    // ----------------------------------------------------------------------------------------
    private static void Recovery()
    {
        Section("Error recovery and unsolicited traffic");

        // An unsolicited I-frame from the UICC drives the CLF's IRQ and stays in sequence.
        var uicc = new DummySWPTarget();
        var clf = Build(uicc);
        clf.Activate(0);
        Check("IRQ is clear before anything happens", !clf.IRQ.IsSet);
        uicc.RequestServiceWithData("112233");
        Check("an unsolicited UICC frame raises IRQ", clf.IRQ.IsSet);
        Check("  carrying the payload and the line",
            clf.LastReceivedPayloadHex == "[0x11, 0x22, 0x33]" && clf.LastReceivedLine == 0);
        clf.AcknowledgeInterrupt();
        Check("the interrupt can be acknowledged", !clf.IRQ.IsSet);
        uicc.EnqueueResponsePayloadHex("77");
        Check("the link stays in sequence afterwards", clf.SendHex(0, "88") == "[0x77]"
            && uicc.RejectsSent == 0);

        // A CLF whose send sequence has slipped must be pulled back into step by the UICC's REJ.
        var slipped = new DummySWPTarget();
        var desynced = Build(slipped);
        desynced.Activate(0);
        desynced.Send(0, Hex("01"));

        // Feed the UICC one I-frame behind the CLF's back, so its expected N(S) runs one ahead.
        slipped.ExchangeFrame(SWPFrame.Encode(SWPProtocol.BuildInformation(1, 0, Hex("AA"))));

        desynced.Send(0, Hex("02"));
        Check("an out-of-sequence I-frame draws exactly one REJ",
            desynced.RejectsReceived == 1 && slipped.RejectsSent == 1);
        Check("the CLF resynchronises to the REJ's N(R) and retransmits once",
            desynced.Retransmissions == 1);
        Check("  and the payload does arrive", slipped.LastReceivedPayloadHex == "[0x2]");
        var delivered = slipped.ReceivedCount;
        for(var i = 0; i < 20; i++)
        {
            desynced.Send(0, RandomBytes(8));
        }
        Check("the link is healthy after the recovery",
            slipped.ReceivedCount == delivered + 20 && slipped.RejectsSent == 1
                && desynced.Retransmissions == 1);

        // A corrupted frame must be dropped, never accepted.
        var strict = new DummySWPTarget();
        var clfStrict = Build(strict);
        clfStrict.Activate(0);
        var received = strict.FramesReceived;
        var bad = SWPFrame.Encode(new byte[] { 0x80, 0x01, 0x02 });
        bad[2] ^= 0x40;
        Check("a corrupted frame gets no answer", strict.ExchangeFrame(bad).Length == 0);
        Check("  is counted as an error and not accepted",
            strict.FrameErrors == 1 && strict.FramesReceived == received && strict.ReceivedCount == 0);

        // A UICC that never answers must fail activation rather than hang or claim success.
        var mute = new MuteTarget();
        Check("activation fails when ACT_SYNC never arrives", !Build(mute).Activate(0));

        // A UICC whose ACT_READY is lost must be recovered by the FR = 1 frame-resend request.
        var flaky = new DropFirstAnswerTarget();
        var clfFlaky = Build(flaky);
        Logger.Entries.Clear();
        Check("activation recovers when the first ACT_READY is lost", clfFlaky.Activate(0));
        Check("  by re-sending ACT_POWER_MODE with FR = 1", Logged("FR = 1"));
        var probe = RandomBytes(16);
        Check("  and data flows afterwards", clfFlaky.Send(0, probe).SequenceEqual(probe));
    }

    // ----------------------------------------------------------------------------------------
    private static void FrameTrace()
    {
        Section("Raw frame trace on the slave");

        var uicc = new TracingUicc();
        var clf = Build(uicc);
        clf.Activate(0);
        clf.Send(0, Hex("DEAD"));
        uicc.Notify(Hex("5A"));

        // Every layer must appear, and the ACT_SYNC and the unsolicited I-frame are the two that do
        // not pass through ExchangeFrame - a trace that hooks only that method silently loses them.
        Check("the trace captures the UICC's opening ACT_SYNC", uicc.Sent.Any(x => x.StartsWith("ACT_SYNC")));
        Check("  the CLF's ACT_POWER_MODE, decoded",
            uicc.Received.Any(x => x == "ACT_POWER_MODE full power"));
        Check("  the UICC's ACT_READY", uicc.Sent.Contains("ACT_READY"));
        Check("  the SHDLC RSET and UA",
            uicc.Received.Any(x => x.StartsWith("Reset")) && uicc.Sent.Any(x => x.StartsWith("Unnumbered")));
        Check("  the I-frame in, with its sequence numbers",
            uicc.Received.Any(x => x.StartsWith("I   N(S)=0 N(R)=0")));
        Check("  the I-frame out", uicc.Sent.Any(x => x.StartsWith("I   N(S)=0 N(R)=1")));
        Check("  the unsolicited I-frame, which never passes through ExchangeFrame",
            uicc.Sent.Any(x => x.StartsWith("I   N(S)=1 N(R)=1")));

        Check("the hooks and the FrameTraced event agree",
            uicc.Events == uicc.Sent.Count + uicc.Received.Count);

        // The raw wire image is what a capture would show.
        Check("the last frame out is the unsolicited I-frame, raw",
            uicc.LastFrameOutHex == "[0x7E, 0x89, 0x5A, 0x47, 0xB0, 0x7F]", uicc.LastFrameOutHex);
        Check("  and its decoded payload keeps the control field",
            uicc.LastPayloadOutHex == "[0x89, 0x5A]", uicc.LastPayloadOutHex);

        // Eight frames: ACT_SYNC, ACT_POWER_MODE, ACT_READY, RSET, UA, then the I-frame each way,
        // then the unsolicited one.
        var lines = uicc.FrameTraceHex.Split('\n').Length;
        Check("the rolling trace holds every frame of the session", lines == 8, lines + " lines");

        // A malformed frame is the case a trace exists for, so it must not be dropped silently.
        var bad = SWPFrame.Encode(new byte[] { 0x80, 0x01 });
        bad[2] ^= 0x40;
        uicc.ClearFrameTrace();
        uicc.ExchangeFrame(bad);
        Check("a malformed frame is still traced", uicc.Received.Any(x => x.StartsWith("malformed")));
        Check("  and is flagged as such rather than decoded", uicc.LastPayloadInHex == "[]");

        // Depth 0 turns recording off without disturbing the Last* properties.
        var quiet = new TracingUicc { FrameTraceDepth = 0 };
        var clfQuiet = Build(quiet);
        clfQuiet.Activate(0);
        Check("FrameTraceDepth 0 disables the rolling trace",
            quiet.FrameTraceHex == "(no frames traced)");
        Check("  but the last frame is still observable", quiet.LastFrameOut == "UnnumberedAcknowledgement +2B",
            quiet.LastFrameOut);

        var capped = new TracingUicc { FrameTraceDepth = 3 };
        var clfCapped = Build(capped);
        clfCapped.Activate(0);
        clfCapped.Send(0, Hex("01"));
        Check("the trace is bounded by FrameTraceDepth",
            capped.FrameTraceHex.Split('\n').Length == 3);
    }

    // A UICC that records what the frame hooks hand it - the shape a real tracing model would take.
    private class TracingUicc : SimpleSWPPeripheral
    {
        public readonly List<string> Received = new List<string>();
        public readonly List<string> Sent = new List<string>();
        public int Events;

        public TracingUicc()
        {
            FrameTraced += (_, __) => Events++;
        }

        public void Notify(byte[] payload) { SendInformation(payload); }

        protected override byte[] OnInformation(byte[] payload) { return payload; }

        protected override void OnFrameReceived(SWPFrameRecord frame) { Received.Add(frame.Description); }

        protected override void OnFrameSent(SWPFrameRecord frame) { Sent.Add(frame.Description); }
    }

    // ----------------------------------------------------------------------------------------
    // A UICC that stays silent on S2 when the CLF powers the interface.
    private class MuteTarget : SimpleSWPPeripheral
    {
        public override byte[] Activate()
        {
            base.Activate();
            return new byte[0];
        }
    }

    // A UICC whose very first answer is lost on the wire - the CLF must recover with FR = 1.
    private class DropFirstAnswerTarget : SimpleSWPPeripheral
    {
        public override byte[] ExchangeFrame(byte[] wireFrame)
        {
            var answer = base.ExchangeFrame(wireFrame);
            if(!dropped)
            {
                dropped = true;
                return new byte[0];
            }
            return answer;
        }

        protected override byte[] OnInformation(byte[] payload)
        {
            return payload;
        }

        private bool dropped;
    }

    // ----------------------------------------------------------------------------------------
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

    private static void CheckWire(string hexPayload, string expectedWire)
    {
        var wire = string.Concat(SWPFrame.Encode(Hex(hexPayload)).Select(x => x.ToString("X2")));
        Check("frames " + (hexPayload.Length == 0 ? "(empty)" : hexPayload) + " as " + expectedWire,
            wire == expectedWire, wire);
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
