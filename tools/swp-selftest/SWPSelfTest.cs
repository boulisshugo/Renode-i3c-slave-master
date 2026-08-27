// Self-test for the SWP models: drives the real SimpleSWPController and the target models through the
// data link layer, the ACT activation sequence, SHDLC and the error-recovery paths, and checks the
// frame codec against golden wire vectors produced by an independent implementation.
//
// Two sections guard the layering the models are built on. Layering() asserts that a bare
// SimpleSWPPeripheral - the SWP transceiver - answers ACT_POWER_MODE, RSET and an I-frame with
// nothing at all, because the protocol is the target's firmware, not its hardware. Firmware() then
// drives an InventedSWPTarget entirely through its register window from a stand-in firmware and
// checks that the CLF only ever sees frames that firmware built.
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
        Layering();
        Activation();
        Shdlc();
        Recovery();
        Firmware();
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
    // The transport must not answer for a protocol layer it does not have. Every check here is a
    // regression guard on that: a SimpleSWPPeripheral on its own is a transceiver, and a CLF talking
    // to one hears silence, not a helpfully invented ACT_SYNC.
    private static void Layering()
    {
        Section("Layering: the transport invents nothing");

        var bare = new SimpleSWPPeripheral();
        Check("powering S1 sends nothing on its own", bare.Activate().Length == 0);
        Check("  but the contact is powered", bare.InterfacePowered
            && bare.InterfaceState != SWPInterfaceState.Deactivated);

        var powerMode = SWPFrame.Encode(SWPProtocol.BuildActPowerMode(SWPPowerMode.FullPower, false));
        Check("ACT_POWER_MODE draws no ACT_READY", bare.ExchangeFrame(powerMode).Length == 0);

        var reset = SWPFrame.Encode(SWPProtocol.BuildUnnumbered(
            SWPProtocol.UnnumberedFrameModifier.Reset, SWPProtocol.BuildResetParameters(4, false)));
        Check("RSET draws no UA", bare.ExchangeFrame(reset).Length == 0);

        var information = SWPFrame.Encode(SWPProtocol.BuildInformation(0, 0, Hex("DEAD")));
        Check("an I-frame draws neither RR nor an answer", bare.ExchangeFrame(information).Length == 0);

        Check("all three frames were received and checked", bare.FramesReceived == 3 && bare.FrameErrors == 0);
        Check("and not one frame was sent", bare.FramesSent == 0);

        // The wire still works: what the transport is asked to send, it sends - framed and CRC'd.
        var listener = new List<byte[]>();
        bare.FrameAvailable += (_, wire) => listener.Add(wire);
        bare.TransmitPayload(SWPProtocol.BuildActReady());
        Check("a payload handed to the transport does go out, framed",
            bare.FramesSent == 1 && listener.Count == 1
                && SWPFrame.TryDecode(listener[0], out var sent, out _)
                && sent.SequenceEqual(SWPProtocol.BuildActReady()));

        // And a CLF pointed at a bare transport must report a failed activation rather than pretend.
        var clf = Build(new SimpleSWPPeripheral());
        Check("a CLF gets no activation out of a bare transport", !clf.Activate(0) && !clf.LinkEstablished);
        Check("  and says it is still waiting rather than claiming failure",
            clf.IsActivationPending(0));
    }

    // ----------------------------------------------------------------------------------------
    // The case the whole layering exists for: ACT and SHDLC running on the CPU side of a register
    // interface. FirmwareUnderTest below is a stand-in for the target's firmware - it owns an
    // SWPTargetStack and drives InventedSWPTarget's registers, exactly as C firmware does.
    private static void Firmware()
    {
        Section("Firmware-managed target (InventedSWPTarget)");

        var swp = new InventedSWPTarget();
        var clf = Build(swp);
        var firmware = new FirmwareUnderTest(swp);

        Check("activation does not complete in the call - the CPU has not run yet", !clf.Activate(0));
        Check("  the CLF is waiting, not failed", clf.IsActivationPending(0));
        Check("  and the hardware has interrupted the CPU", swp.IRQ.IsSet);
        Check("  with nothing sent on S2 by the model itself", swp.FramesSent == 0);

        // Now let the "CPU" run. Each pass drains what the hardware has for it and answers.
        firmware.Run();
        Check("the firmware saw the activation event", firmware.ActivationEvents == 1);
        Check("the firmware's ACT_SYNC brought the link all the way up", clf.IsLinkEstablished(0));
        Check("  through the real sequence", firmware.LlcState == SWPLlcState.Established
            && swp.LlcState == SWPLlcState.Established);
        Check("  and the CLF read the firmware's own ACT_INFORMATION",
            clf.GetTargetMaxFramePayloadSize(0) == 256);
        Check("the interrupt is cleared once the firmware has drained everything", !swp.IRQ.IsSet);

        // Data: the answer cannot ride the frame that asked for it, so it arrives later.
        var received = new List<byte[]>();
        clf.PayloadReceived += (_, payload) => received.Add(payload);
        firmware.Application = request => request.Reverse().ToArray();

        var answer = clf.Send(0, Hex("0102030405"));
        Check("the slot that carried the request answers with nothing", answer.Length == 0);
        Check("  because the firmware has not run yet", received.Count == 0 && swp.PendingRxFrames == 1);
        firmware.Run();
        Check("the answer arrives once the CPU has run",
            received.Count == 1 && received[0].SequenceEqual(Hex("0504030201")));
        Check("  and the CLF publishes it", clf.LastReceivedPayloadHex == "[0x5, 0x4, 0x3, 0x2, 0x1]");

        var matched = 0;
        for(var i = 0; i < 100; i++)
        {
            var request = RandomBytes(1 + (i % 32));
            received.Clear();
            clf.Send(0, request);
            firmware.Run();
            if(received.Count == 1 && received[0].SequenceEqual(request.Reverse().ToArray()))
            {
                matched++;
            }
        }
        Check("100 firmware round trips stay in sequence", matched == 100);
        Check("  with no CRC error, REJ or retransmission",
            clf.CrcErrors == 0 && clf.RejectsReceived == 0 && clf.Retransmissions == 0);

        // A firmware that stops answering is visible as silence, not papered over.
        firmware.Stopped = true;
        received.Clear();
        Check("a stalled firmware answers nothing at all", clf.Send(0, Hex("AA")).Length == 0);
        firmware.Run();
        Check("  and nothing is invented on its behalf", received.Count == 0);
        firmware.Stopped = false;

        // Deactivation must reach the firmware and drop every scrap of link state.
        firmware.Run();
        clf.Deactivate(0);
        Check("  the hardware interrupts the CPU on deactivation too", swp.IRQ.IsSet);
        firmware.Run();
        Check("the firmware saw the deactivation event", firmware.DeactivationEvents == 1);
        Check("  and closed its LLC", swp.LlcState == SWPLlcState.Closed
            && swp.InterfaceState == SWPInterfaceState.Deactivated);
        Check("  buffered frames are dropped with the power", swp.PendingRxFrames == 0);

        // And it all comes back up from scratch.
        clf.Activate(0);
        firmware.Run();
        Check("re-activation runs the whole sequence again", clf.IsLinkEstablished(0)
            && firmware.ActivationEvents == 2);
    }

    // A stand-in for the target's firmware: it owns the protocol (an SWPTargetStack, the same code a
    // C port would transcribe) and reaches the wire only through InventedSWPTarget's registers -
    // read STATUS, drain RX_DATA, push TX_DATA, write TX_COMMIT. Nothing here can touch the frame
    // codec directly, which is the point.
    private class FirmwareUnderTest
    {
        public FirmwareUnderTest(InventedSWPTarget device)
        {
            this.device = device;
            stack.MaxFramePayloadSize = 256;
            stack.InformationHandler = request => Application?.Invoke(request);
        }

        // The application layer above SHDLC: request in, response out.
        public Func<byte[], byte[]> Application { get; set; }

        // Simulates a firmware that has hung: the CPU stops servicing the peripheral.
        public bool Stopped { get; set; }

        public int ActivationEvents { get; private set; }
        public int DeactivationEvents { get; private set; }
        public SWPLlcState LlcState { get; private set; } = SWPLlcState.Closed;

        // One pass of the firmware's service loop: handle the pending events, then every frame the
        // hardware has buffered. Called where a real firmware would be scheduled by its interrupt.
        public void Run()
        {
            if(Stopped)
            {
                return;
            }
            for(var guard = 0; guard < 64; guard++)
            {
                var status = device.ReadDoubleWord(Status);
                if((status & StatusActivationEvent) != 0)
                {
                    device.WriteDoubleWord(StatusClear, StatusActivationEvent);
                    ActivationEvents++;
                    Open();
                    continue;
                }
                if((status & StatusDeactivationEvent) != 0)
                {
                    device.WriteDoubleWord(StatusClear, StatusDeactivationEvent);
                    DeactivationEvents++;
                    stack.Deactivate();
                    Publish(SWPLlcState.Closed);
                    continue;
                }
                if((status & StatusRxFrame) == 0)
                {
                    return;
                }
                HandleFrame((int)((status >> RxCountShift) & 0xFFFF));
            }
        }

        // The ACT_EVT handler: open the LLC and announce ourselves. The hardware sent nothing until
        // this ran, and what it sends now is what we built.
        private void Open()
        {
            Publish(SWPLlcState.Opened);
            Transmit(stack.Activate());
            Publish(SWPLlcState.ActSyncSent);
        }

        private void HandleFrame(int length)
        {
            var payload = new byte[length];
            for(var i = 0; i < length; i++)
            {
                payload[i] = (byte)device.ReadDoubleWord(RxData);
            }
            var answer = stack.HandlePayload(payload);
            if(answer != null)
            {
                Transmit(answer);
            }
            Publish(stack.LinkEstablished
                ? SWPLlcState.Established
                : stack.InterfaceState == SWPInterfaceState.ActReady
                    ? SWPLlcState.ActReadySent
                    : SWPLlcState.ActSyncSent);
        }

        private void Transmit(byte[] payload)
        {
            if(payload == null || payload.Length == 0)
            {
                return;
            }
            foreach(var b in payload)
            {
                device.WriteDoubleWord(TxData, b);
            }
            device.WriteDoubleWord(TxCommit, 1);
        }

        private void Publish(SWPLlcState state)
        {
            LlcState = state;
            device.WriteDoubleWord(LlcStateRegister, (uint)state);
        }

        private readonly InventedSWPTarget device;
        private readonly SWPTargetStack stack = new SWPTargetStack();

        // The register map InventedSWPTarget documents.
        private const long Status = 0x00;
        private const long StatusClear = 0x04;
        private const long RxData = 0x0C;
        private const long TxData = 0x14;
        private const long TxCommit = 0x18;
        private const long LlcStateRegister = 0x20;

        private const uint StatusActivationEvent = 1u << 0;
        private const uint StatusDeactivationEvent = 1u << 1;
        private const uint StatusRxFrame = 1u << 2;
        private const int RxCountShift = 8;
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
    private class TracingUicc : SoftwareSWPTarget
    {
        public readonly List<string> Received = new List<string>();
        public readonly List<string> Sent = new List<string>();
        public int Events;

        public TracingUicc()
        {
            FrameTraced += (_, __) => Events++;
        }

        public void Notify(byte[] payload) { SendInformation(payload); }

        protected override byte[] OnInformation(byte[] information) { return information; }

        protected override void OnFrameReceived(SWPFrameRecord frame) { Received.Add(frame.Description); }

        protected override void OnFrameSent(SWPFrameRecord frame) { Sent.Add(frame.Description); }
    }

    // ----------------------------------------------------------------------------------------
    // A UICC that stays silent on S2 when the CLF powers the interface - which is exactly what the
    // bare transport does, since it has no protocol layer to answer with.
    private class MuteTarget : SimpleSWPPeripheral
    {
    }

    // A UICC whose very first answer is lost on the wire - the CLF must recover with FR = 1.
    private class DropFirstAnswerTarget : SoftwareSWPTarget
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

        protected override byte[] OnInformation(byte[] information)
        {
            return information;
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
