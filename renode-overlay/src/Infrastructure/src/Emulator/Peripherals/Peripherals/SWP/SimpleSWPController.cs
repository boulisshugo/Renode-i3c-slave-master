//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // A simple, agnostic SWP master - the CLF (Contactless Front-end) side of an ETSI TS 102 613
    // link. It is the counterpart of the target models and speaks the same three layers:
    //
    //   - data link layer (clause 8): every frame it sends is built by SWPFrame (SOF, bit-stuffed
    //     payload and CRC, EOF) and every frame it receives is decoded and CRC-checked;
    //   - ACT LLC (clause 11): Activate() powers S1 and then runs the activation sequence as the
    //     target's frames arrive - ACT_SYNC in, ACT_POWER_MODE out, ACT_READY in;
    //   - SHDLC LLC (clause 10): after activation it establishes the link with RSET/UA (negotiating
    //     the window size and SREJ support), then carries data in modulo-8 sequenced I-frames,
    //     acknowledging with RR and recovering a lost frame with REJ.
    //
    // THE TARGET ANSWERS WHEN IT IS READY, NOT WHEN IT IS ASKED
    //
    // SWP is full duplex and point to point: the UICC drives S2 whenever it has something to say. On
    // a target whose ACT and SHDLC layers are firmware (InventedSWPTarget), that is not a nicety but
    // the only possibility - the firmware does not even run until the receiving slot is over, so no
    // answer can ride the frame that asked for it.
    //
    // This controller is therefore EVENT-DRIVEN. Whether a frame comes back in the same slot
    // (ExchangeFrame) or later on the target's own initiative (FrameAvailable), it goes through the
    // same path and advances the same state machine. Which means:
    //
    //   - Activate(line) returns true only if the link came up within the call, which happens with a
    //     host-side stack (SoftwareSWPTarget) that answers in-slot. With a firmware-managed target it
    //     returns false and activation continues as the firmware answers: poll IsLinkEstablished(line)
    //     while the emulation runs, rather than treating false as a failure.
    //   - Send(line, ...) returns the answer only when it arrives in-slot. Otherwise it arrives later
    //     and is published through the PayloadReceived event, LastReceivedPayloadHex and the IRQ.
    //
    // Nothing here ever spins or sleeps: an exchange is a call into the target and a return, so the
    // CPU keeps running and simulation time keeps advancing between a question and its answer.
    //
    // SWP is point to point, but a CLF commonly has more than one SWP line (one to the UICC, one to
    // an embedded SE). Targets therefore register by SWP *line number*, like any Renode bus child:
    //
    //     swp:  SWP.SimpleSWPController @ sysbus
    //     uicc: SWP.SoftwareSWPTarget @ swp 0
    //
    // It registers on the sysbus WITHOUT an address. The CLF is a separate chip on the far end of the
    // SWP line, not a block inside the SoC: it has no register map, so claiming an address range would
    // be fiction and would make the bus lie about what is actually memory-mapped. The monitor still
    // reaches it as `sysbus.<name>`.
    public class SimpleSWPController : SimpleContainer<ISWPPeripheral>, INumberedGPIOOutput
    {
        public SimpleSWPController(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            Connections = new Dictionary<int, IGPIO> { { 0, IRQ } };
        }

        public override void Register(ISWPPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            base.Register(peripheral, registrationPoint);
            Action<ISWPPeripheral, byte[]> handler = HandleTargetFrame;
            frameHandlers[peripheral] = handler;
            peripheral.FrameAvailable += handler;
            links[registrationPoint.Address] = new Link();
        }

        public override void Unregister(ISWPPeripheral peripheral)
        {
            if(frameHandlers.TryGetValue(peripheral, out var handler))
            {
                peripheral.FrameAvailable -= handler;
                frameHandlers.Remove(peripheral);
            }
            foreach(var line in ChildCollection.Where(x => ReferenceEquals(x.Value, peripheral)).Select(x => x.Key).ToArray())
            {
                links.Remove(line);
            }
            base.Unregister(peripheral);
        }

        public override void Reset()
        {
            IRQ.Unset();
            LastReceivedLine = -1;
            lastReceivedPayload = new byte[0];
            FramesSent = 0;
            FramesReceived = 0;
            CrcErrors = 0;
            RejectsReceived = 0;
            Retransmissions = 0;
            foreach(var line in links.Keys.ToArray())
            {
                links[line] = new Link();
            }
        }

        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // --------------------------------------------------------------------------------------
        // Activation and deactivation (ACT LLC)
        // --------------------------------------------------------------------------------------

        // Power mode the CLF requests in ACT_POWER_MODE. Settable from a .repl or the monitor.
        public SWPPowerMode PowerMode { get; set; } = SWPPowerMode.FullPower;

        // How many times the CLF re-sends ACT_POWER_MODE with FR = 1 before giving up on activation.
        // Only the in-slot answers of a host-side stack can be retried inside Activate(); with a
        // firmware-managed target, use RetryActivation(line) when the firmware has gone quiet.
        public int ActivationRetries { get; set; } = 3;

        // SHDLC window size the CLF proposes in RSET.
        public int WindowSize { get; set; } = SWPProtocol.DefaultWindowSize;

        // Whether the CLF offers selective reject in RSET.
        public bool SelectiveRejectSupport { get; set; } = SWPProtocol.DefaultSelectiveRejectSupport;

        // Powers S1 on one SWP line and starts the activation sequence. Returns true if the link came
        // all the way up inside this call - which needs a target that answers in-slot. A
        // firmware-managed target returns false here and finishes the sequence as its firmware runs;
        // watch IsLinkEstablished(line).
        public bool Activate(int line)
        {
            if(!TryGetTarget(line, out var target) || !TryGetLink(line, out var link))
            {
                return false;
            }

            link.Reset();
            link.State = SWPInterfaceState.Deactivated;
            link.ActivationPending = true;
            link.ActivationAttempts = 0;

            // Drive S1. A target with a host-side stack hands back its ACT_SYNC right away; a
            // firmware-managed one hands back nothing and answers on S2 once its firmware has run.
            var opening = target.Activate();
            if(opening != null && opening.Length > 0)
            {
                link.AnswersInSlot = true;
            }
            Deliver(line, link, opening);

            // A target that has answered in-slot is one that can: if the sequence has stalled, the
            // frame was lost rather than merely late, and the specification's recovery is to ask for
            // it again with FR = 1. A target that has said nothing at all may simply not have run
            // its firmware yet, and retrying at it would be shouting at a chip that is still booting.
            while(!link.Established && link.ActivationPending && link.AnswersInSlot
                && RetryActivation(line))
            {
            }

            if(link.Established)
            {
                return true;
            }
            if(link.ActivationPending)
            {
                this.Log(LogLevel.Info,
                    "SWP line {0}: S1 is up, waiting for the target's ACT_SYNC (it answers when its firmware is ready)",
                    line);
            }
            return false;
        }

        // Activates every registered SWP line. Convenience for the monitor and for platforms with a
        // single UICC.
        public void ActivateAll()
        {
            foreach(var line in ChildCollection.Keys.OrderBy(x => x).ToArray())
            {
                Activate(line);
            }
        }

        // Asks the target to repeat its last ACT frame by re-sending ACT_POWER_MODE with FR = 1 -
        // the recovery the specification prescribes when the CLF did not get an ACT frame intact.
        // Returns false when the line is not mid-activation or the retry budget is spent.
        public bool RetryActivation(int line)
        {
            if(!TryGetTarget(line, out var target) || !TryGetLink(line, out var link))
            {
                return false;
            }
            if(!link.ActivationPending || link.State == SWPInterfaceState.Deactivated)
            {
                this.Log(LogLevel.Warning, "SWP line {0}: no activation in progress to retry", line);
                return false;
            }
            if(link.ActivationAttempts >= Math.Max(0, ActivationRetries))
            {
                this.Log(LogLevel.Warning, "SWP line {0}: activation retries exhausted", line);
                return false;
            }
            link.ActivationAttempts++;
            this.Log(LogLevel.Warning,
                "SWP line {0}: no ACT_READY (attempt {1}); re-sending ACT_POWER_MODE with FR = 1",
                line, link.ActivationAttempts);
            SendPayload(line, link, target, SWPProtocol.BuildActPowerMode(PowerMode, true));
            return true;
        }

        // Drives S1 low: the interface returns to DEACTIVATED and all link state is dropped.
        public void Deactivate(int line)
        {
            if(!TryGetTarget(line, out var target))
            {
                return;
            }
            target.Deactivate();
            if(TryGetLink(line, out var link))
            {
                link.Reset();
                link.State = SWPInterfaceState.Deactivated;
            }
            this.Log(LogLevel.Info, "SWP line {0} deactivated", line);
        }

        // --------------------------------------------------------------------------------------
        // Data transfer (SHDLC LLC)
        // --------------------------------------------------------------------------------------

        // Sends one LLC payload in an SHDLC I-frame. Returns the payload the target answered with in
        // the same slot, or an empty array - which means either a bare acknowledgement or, with a
        // firmware-managed target, that the answer is still being built. A REJ is honoured by
        // retransmitting once.
        public byte[] Send(int line, byte[] payload)
        {
            payload = payload ?? new byte[0];
            if(!TryGetTarget(line, out var target) || !TryGetLink(line, out var link))
            {
                return Nothing;
            }
            if(!link.Established)
            {
                this.Log(LogLevel.Warning, "SWP line {0}: the SHDLC link is not established - call Activate first", line);
                return Nothing;
            }
            if(payload.Length > link.TargetMaxFramePayloadSize)
            {
                this.Log(LogLevel.Warning,
                    "SWP line {0}: payload of {1} bytes exceeds the {2}-byte maximum the UICC advertised in ACT_INFORMATION",
                    line, payload.Length, link.TargetMaxFramePayloadSize);
                return Nothing;
            }

            var sequence = link.SendSequence;
            link.SendSequence = (sequence + 1) % SWPProtocol.SequenceModulo;
            link.PendingReject = -1;
            var delivered = SendPayload(line, link, target,
                SWPProtocol.BuildInformation(sequence, link.ReceiveSequence, payload));

            if(link.PendingReject < 0)
            {
                return delivered;
            }

            // A REJ asks for retransmission starting at its N(R). Resynchronise our send sequence to
            // it and send the frame again - blindly reusing the refused N(S) would just be rejected.
            var rejectSequence = link.PendingReject;
            link.PendingReject = -1;
            Retransmissions++;
            this.Log(LogLevel.Debug, "SWP line {0}: REJ received, retransmitting I-frame with N(S) = {1}",
                line, rejectSequence);
            link.SendSequence = (rejectSequence + 1) % SWPProtocol.SequenceModulo;
            return SendPayload(line, link, target,
                SWPProtocol.BuildInformation(rejectSequence, link.ReceiveSequence, payload));
        }

        // Monitor-friendly helper: send hex-encoded data, get the hex-encoded answer back.
        public string SendHex(int line, string hexPayload)
        {
            return Misc.PrettyPrintCollectionHex(Send(line, Misc.HexStringToByteArray(hexPayload)));
        }

        // Sends a bare RR - a poll that acknowledges what we have received and gives the target a
        // slot in which to answer. Returns any payload it sends back in that slot.
        public byte[] Poll(int line)
        {
            if(!TryGetTarget(line, out var target) || !TryGetLink(line, out var link) || !link.Established)
            {
                return Nothing;
            }
            return SendPayload(line, link, target,
                SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.ReceiveReady, link.ReceiveSequence));
        }

        public string PollHex(int line)
        {
            return Misc.PrettyPrintCollectionHex(Poll(line));
        }

        // Raised whenever an I-frame payload from a target has been accepted, whichever way it
        // arrived. The arguments are the SWP line and the application payload, control field
        // stripped. Fired on the emulation thread that ran the exchange.
        public event Action<int, byte[]> PayloadReceived;

        // --------------------------------------------------------------------------------------
        // Observable state
        // --------------------------------------------------------------------------------------

        // Interface state of one SWP line.
        public SWPInterfaceState GetInterfaceState(int line)
        {
            return TryGetLink(line, out var link) ? link.State : SWPInterfaceState.Deactivated;
        }

        // Interface state of SWP line 0 - the common single-UICC case, readable straight from the monitor.
        public SWPInterfaceState InterfaceState => GetInterfaceState(0);

        // True once the SHDLC RSET/UA handshake on the line has completed.
        public bool IsLinkEstablished(int line)
        {
            return TryGetLink(line, out var link) && link.Established;
        }

        public bool LinkEstablished => IsLinkEstablished(0);

        // True between Activate(line) and the link coming up - the window in which the CLF is
        // waiting on the target's firmware.
        public bool IsActivationPending(int line)
        {
            return TryGetLink(line, out var link) && link.ActivationPending;
        }

        // SHDLC window size agreed on the line.
        public int GetWindowSize(int line)
        {
            return TryGetLink(line, out var link) ? link.WindowSize : 0;
        }

        // Maximum frame payload the UICC on the line advertised in ACT_INFORMATION.
        public int GetTargetMaxFramePayloadSize(int line)
        {
            return TryGetLink(line, out var link) ? link.TargetMaxFramePayloadSize : 0;
        }

        // SWP line the most recent frame came from, or -1 if none since reset.
        public int LastReceivedLine { get; private set; } = -1;

        // Payload of the most recent I-frame received, hex-encoded (monitor-readable).
        public string LastReceivedPayloadHex => Misc.PrettyPrintCollectionHex(lastReceivedPayload);

        public int FramesSent { get; private set; }
        public int FramesReceived { get; private set; }

        // Frames dropped because their CRC or framing was bad.
        public int CrcErrors { get; private set; }

        // REJ frames received from a target (it asked for a retransmission).
        public int RejectsReceived { get; private set; }

        // I-frames the controller re-sent after a REJ.
        public int Retransmissions { get; private set; }

        // Clears the pending indication (drops the IRQ line).
        public void AcknowledgeInterrupt()
        {
            IRQ.Unset();
        }

        // --------------------------------------------------------------------------------------
        // Data link layer helpers, exposed so the framing itself can be exercised and inspected
        // --------------------------------------------------------------------------------------

        // Encodes an LLC payload into a complete SWP wire frame, hex-encoded.
        public string EncodeFrameHex(string hexPayload)
        {
            return Misc.PrettyPrintCollectionHex(SWPFrame.Encode(Misc.HexStringToByteArray(hexPayload)));
        }

        // Decodes a hex-encoded SWP wire frame back to its LLC payload, or reports why it is invalid.
        public string DecodeFrameHex(string hexFrame)
        {
            return SWPFrame.TryDecode(Misc.HexStringToByteArray(hexFrame), out var payload, out var error)
                ? Misc.PrettyPrintCollectionHex(payload)
                : $"invalid frame: {error}";
        }

        // CRC-16 of a hex-encoded payload, as it would be appended to the frame.
        public string ComputeFrameCrc(string hexPayload)
        {
            return $"0x{SWPFrame.ComputeCrc(Misc.HexStringToByteArray(hexPayload)):X4}";
        }

        // Returns the target registered on the given SWP line, or null if there is none.
        public ISWPPeripheral GetTarget(int line)
        {
            return TryGetByAddress(line, out var target) ? target : null;
        }

        protected bool TryGetTarget(int line, out ISWPPeripheral target)
        {
            if(!TryGetByAddress(line, out target))
            {
                this.Log(LogLevel.Warning, "No SWP target registered on line {0}", line);
                return false;
            }
            return true;
        }

        // --------------------------------------------------------------------------------------
        // The one path every frame from a target takes
        // --------------------------------------------------------------------------------------

        // Sends one LLC payload as a wire frame and feeds whatever comes back in the same slot into
        // the state machine. Returns the application payload delivered by that answer, if any.
        private byte[] SendPayload(int line, Link link, ISWPPeripheral target, byte[] payload)
        {
            FramesSent++;
            this.Log(LogLevel.Noisy, "SWP line {0}: sending control 0x{1:X2} with {2} payload byte(s)",
                line, payload.Length > 0 ? payload[0] : 0, Math.Max(0, payload.Length - 1));
            var answer = target.ExchangeFrame(SWPFrame.Encode(payload));
            if(answer != null && answer.Length > 0)
            {
                link.AnswersInSlot = true;
            }
            return Deliver(line, link, answer);
        }

        // Decodes a wire frame from a target and dispatches it. Counts CRC/framing errors. Returns
        // the application payload it carried, or an empty array.
        private byte[] Deliver(int line, Link link, byte[] wire)
        {
            if(wire == null || wire.Length == 0)
            {
                return Nothing;
            }
            if(!SWPFrame.TryDecode(wire, out var payload, out var error))
            {
                CrcErrors++;
                this.Log(LogLevel.Warning, "SWP line {0}: discarding a malformed frame: {1}", line, error);
                return Nothing;
            }
            FramesReceived++;
            if(payload.Length == 0)
            {
                return Nothing;
            }
            return Dispatch(line, link, payload);
        }

        // Advances the CLF state machine by one received payload, whichever layer it belongs to and
        // whichever way it arrived. May send the next frame of a handshake, which recurses back
        // here through SendPayload - bounded by the length of the activation sequence.
        private byte[] Dispatch(int line, Link link, byte[] payload)
        {
            if(!TryGetTarget(line, out var target))
            {
                return Nothing;
            }

            switch(payload[0])
            {
            case SWPProtocol.ActSync:
                // The target has come up and announced itself. It may do so at any time - after S1
                // rises, or again because we asked it to repeat with FR = 1 - so this is accepted
                // whenever it arrives rather than only in the state that expects it.
                ParseActInformation(link, payload);
                link.State = SWPInterfaceState.ActSync;
                link.ActivationPending = true;
                this.Log(LogLevel.Info,
                    "SWP line {0}: ACT_SYNC received (version {1}, LLCs {2}, max frame {3} bytes); answering ACT_POWER_MODE",
                    line, link.TargetProtocolVersion, link.TargetSupportedLlcs, link.TargetMaxFramePayloadSize);
                link.State = SWPInterfaceState.ActPowerMode;
                return SendPayload(line, link, target, SWPProtocol.BuildActPowerMode(PowerMode, false));

            case SWPProtocol.ActReady:
                if(!link.ActivationPending)
                {
                    this.Log(LogLevel.Warning, "SWP line {0}: unexpected ACT_READY - no activation in progress", line);
                    return Nothing;
                }
                // ACT_READY completes the sequence; the interface is available for data transfer.
                link.PowerMode = PowerMode;
                link.State = SWPInterfaceState.Activated;
                this.Log(LogLevel.Info, "SWP line {0} activated in {1} mode; establishing the SHDLC link", line, PowerMode);
                return SendPayload(line, link, target, SWPProtocol.BuildUnnumbered(
                    SWPProtocol.UnnumberedFrameModifier.Reset,
                    SWPProtocol.BuildResetParameters((byte)WindowSize, SelectiveRejectSupport)));

            case SWPProtocol.ActPowerMode:
                this.Log(LogLevel.Warning, "SWP line {0}: a target sent ACT_POWER_MODE, which is the CLF's frame", line);
                return Nothing;
            }

            switch(SWPProtocol.GetFrameKind(payload[0]))
            {
            case SWPProtocol.ShdlcFrameKind.Unnumbered:
                return HandleUnnumbered(line, link, payload);
            case SWPProtocol.ShdlcFrameKind.Supervisory:
                return HandleSupervisory(line, link, payload[0]);
            default:
                return HandleInformation(line, link, payload);
            }
        }

        private byte[] HandleUnnumbered(int line, Link link, byte[] payload)
        {
            if(SWPProtocol.GetModifier(payload[0]) != SWPProtocol.UnnumberedFrameModifier.UnnumberedAcknowledgement)
            {
                this.Log(LogLevel.Debug, "SWP line {0}: U-frame 0x{1:X2} received outside link establishment",
                    line, payload[0]);
                return Nothing;
            }

            // UA: the target accepted our RSET and echoed the parameters it will use.
            link.WindowSize = payload.Length > 1 ? Math.Max(1, Math.Min(payload[1], WindowSize)) : WindowSize;
            link.SendSequence = 0;
            link.ReceiveSequence = 0;
            link.Established = true;
            link.ActivationPending = false;
            link.State = SWPInterfaceState.Activated;
            this.Log(LogLevel.Info, "SWP line {0}: SHDLC link established (window {1})", line, link.WindowSize);
            return Nothing;
        }

        private byte[] HandleSupervisory(int line, Link link, byte control)
        {
            var type = SWPProtocol.GetSupervisoryType(control);
            if(type == SWPProtocol.SupervisoryFrameType.Reject)
            {
                RejectsReceived++;
                link.PendingReject = SWPProtocol.GetReceiveSequence(control);
            }
            else if(type == SWPProtocol.SupervisoryFrameType.ReceiveNotReady)
            {
                this.Log(LogLevel.Warning, "SWP line {0}: the UICC signalled RNR (busy)", line);
            }
            return Nothing;
        }

        private byte[] HandleInformation(int line, Link link, byte[] payload)
        {
            var sendSequence = SWPProtocol.GetSendSequence(payload[0]);
            if(sendSequence != link.ReceiveSequence)
            {
                this.Log(LogLevel.Warning, "SWP line {0}: out-of-sequence I-frame N(S) = {1}, expected {2}",
                    line, sendSequence, link.ReceiveSequence);
                return Nothing;
            }
            link.ReceiveSequence = (link.ReceiveSequence + 1) % SWPProtocol.SequenceModulo;

            var information = new byte[payload.Length - 1];
            Array.Copy(payload, 1, information, 0, information.Length);
            lastReceivedPayload = information;
            LastReceivedLine = line;
            PayloadReceived?.Invoke(line, information);
            return information;
        }

        private void ParseActInformation(Link link, byte[] actSync)
        {
            // ACT_SYNC = control byte + ACT_INFORMATION (version, LLC bitmap, max frame size, power modes).
            if(actSync.Length > 1)
            {
                link.TargetProtocolVersion = actSync[1];
            }
            if(actSync.Length > 2)
            {
                link.TargetSupportedLlcs = (SWPProtocol.SupportedLlc)actSync[2];
            }
            if(actSync.Length > 4)
            {
                var size = (actSync[3] << 8) | actSync[4];
                link.TargetMaxFramePayloadSize = size > 0 ? size : DefaultMaxFramePayloadSize;
            }
            if(actSync.Length > 5)
            {
                link.TargetPowerModes = actSync[5];
            }
        }

        // A target transmitted on S2 on its own initiative - an ACT frame its firmware has just
        // built, an answer to something we sent earlier, or an unsolicited I-frame. It goes through
        // exactly the same state machine as an in-slot answer.
        private void HandleTargetFrame(ISWPPeripheral target, byte[] wire)
        {
            var line = ChildCollection.Where(x => ReferenceEquals(x.Value, target))
                .Select(x => (int?)x.Key).FirstOrDefault() ?? -1;
            if(line < 0 || !TryGetLink(line, out var link))
            {
                return;
            }

            var payload = Deliver(line, link, wire);
            if(payload.Length == 0)
            {
                return;
            }
            this.Log(LogLevel.Info, "Unsolicited frame from the UICC on SWP line {0}, payload {1}",
                line, Misc.PrettyPrintCollectionHex(payload));
            IRQ.Set();
        }

        private bool TryGetLink(int line, out Link link)
        {
            return links.TryGetValue(line, out link);
        }

        private const int DefaultMaxFramePayloadSize = 4096;

        private byte[] lastReceivedPayload = new byte[0];
        // Field initializers, not constructor-body assignments: Reset() touches them.
        private readonly Dictionary<ISWPPeripheral, Action<ISWPPeripheral, byte[]>> frameHandlers =
            new Dictionary<ISWPPeripheral, Action<ISWPPeripheral, byte[]>>();
        private readonly Dictionary<int, Link> links = new Dictionary<int, Link>();

        private static readonly byte[] Nothing = new byte[0];

        // Per-line interface and SHDLC state held by the CLF.
        private class Link
        {
            public void Reset()
            {
                Established = false;
                ActivationPending = false;
                ActivationAttempts = 0;
                AnswersInSlot = false;
                PendingReject = -1;
                SendSequence = 0;
                ReceiveSequence = 0;
                WindowSize = SWPProtocol.DefaultWindowSize;
                PowerMode = SWPPowerMode.LowPower;
                TargetProtocolVersion = 0;
                TargetSupportedLlcs = SWPProtocol.SupportedLlc.None;
                TargetMaxFramePayloadSize = DefaultMaxFramePayloadSize;
                TargetPowerModes = 0;
            }

            public SWPInterfaceState State = SWPInterfaceState.Deactivated;
            public SWPPowerMode PowerMode = SWPPowerMode.LowPower;
            public bool Established;

            // Set between Activate() and the link coming up: the window in which an ACT frame from
            // the target is expected and the FR retry applies.
            public bool ActivationPending;
            public int ActivationAttempts;

            // Set once this target has answered inside a slot. It tells a lost frame (retry now)
            // apart from a target whose firmware has not answered yet (wait for it).
            public bool AnswersInSlot;

            // N(R) of a REJ seen while handling the current Send, or -1. Collected here because the
            // REJ may arrive from a nested dispatch rather than as Send's own return value.
            public int PendingReject = -1;

            public int SendSequence;
            public int ReceiveSequence;
            public int WindowSize = SWPProtocol.DefaultWindowSize;
            public byte TargetProtocolVersion;
            public SWPProtocol.SupportedLlc TargetSupportedLlcs = SWPProtocol.SupportedLlc.None;
            public int TargetMaxFramePayloadSize = DefaultMaxFramePayloadSize;
            public byte TargetPowerModes;
        }
    }
}
