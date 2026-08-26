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
    // link. It is the counterpart of SimpleSWPPeripheral and speaks the same three layers:
    //
    //   - data link layer (clause 8): every frame it sends is built by SWPFrame (SOF, bit-stuffed
    //     payload and CRC, EOF) and every frame it receives is decoded and CRC-checked;
    //   - ACT LLC (clause 11): Activate() runs the activation sequence - it powers S1, receives the
    //     UICC's ACT_SYNC + ACT_INFORMATION, answers ACT_POWER_MODE with the selected power mode,
    //     and waits for ACT_READY. A corrupted or missing answer is retried by re-sending
    //     ACT_POWER_MODE with the frame-resend (FR) bit set, exactly as the specification prescribes;
    //   - SHDLC LLC (clause 10): after activation it establishes the link with RSET/UA (negotiating
    //     the window size and SREJ support), then carries data in modulo-8 sequenced I-frames,
    //     acknowledging with RR and recovering a lost frame with REJ.
    //
    // SWP is point to point, but a CLF commonly has more than one SWP line (one to the UICC, one to
    // an embedded SE). Targets therefore register by SWP *line number*, like any Renode bus child:
    //
    //     swp:  SWP.SimpleSWPController @ sysbus
    //     uicc: SWP.SimpleSWPPeripheral @ swp 0
    //
    // It registers on the sysbus WITHOUT an address. The CLF is a separate chip on the far end of the SWP line, not a block inside the SoC:
    // it has no register map, so claiming an address range would be fiction and would make the bus
    // lie about what is actually memory-mapped. The monitor still reaches it as `sysbus.<name>`.
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
        public int ActivationRetries { get; set; } = 3;

        // SHDLC window size the CLF proposes in RSET.
        public int WindowSize { get; set; } = SWPProtocol.DefaultWindowSize;

        // Whether the CLF offers selective reject in RSET.
        public bool SelectiveRejectSupport { get; set; } = SWPProtocol.DefaultSelectiveRejectSupport;

        // Runs the full interface activation on one SWP line: S1 up, ACT_SYNC in, ACT_POWER_MODE out,
        // ACT_READY in, then the SHDLC RSET/UA handshake. Returns true once data can flow.
        public bool Activate(int line)
        {
            if(!TryGetTarget(line, out var target) || !TryGetLink(line, out var link))
            {
                return false;
            }

            link.Reset();
            link.State = SWPInterfaceState.Deactivated;

            // The UICC announces itself with ACT_SYNC as soon as the CLF starts driving S1.
            if(!TryReceive(line, target.Activate(), out var sync) || sync.Length == 0
                || sync[0] != SWPProtocol.ActSync)
            {
                this.Log(LogLevel.Warning, "Activation of SWP line {0} failed: no valid ACT_SYNC frame", line);
                target.Deactivate();
                return false;
            }
            ParseActInformation(link, sync);
            link.State = SWPInterfaceState.ActSync;
            this.Log(LogLevel.Info,
                "SWP line {0}: ACT_SYNC received (version {1}, LLCs {2}, max frame {3} bytes)",
                line, link.TargetProtocolVersion, link.TargetSupportedLlcs, link.TargetMaxFramePayloadSize);

            // ACT_POWER_MODE selects the power mode. If the answer is lost or corrupted, ask the UICC
            // to repeat its last ACT frame by setting FR - that is the spec's recovery mechanism.
            byte[] ready = null;
            var frameResend = false;
            for(var attempt = 0; attempt <= Math.Max(0, ActivationRetries); attempt++)
            {
                link.State = SWPInterfaceState.ActPowerMode;
                var answer = Exchange(line, target, SWPProtocol.BuildActPowerMode(PowerMode, frameResend));
                if(TryReceive(line, answer, out var payload) && payload.Length > 0)
                {
                    if(payload[0] == SWPProtocol.ActReady)
                    {
                        ready = payload;
                        break;
                    }
                    if(payload[0] == SWPProtocol.ActSync)
                    {
                        // The UICC repeated its last ACT frame because we asked it to; now that we
                        // have it intact, ask for the power mode again without the FR bit.
                        ParseActInformation(link, payload);
                        frameResend = false;
                        continue;
                    }
                }
                // Nothing usable came back - ask the UICC to repeat its last ACT frame (FR = 1).
                frameResend = true;
                this.Log(LogLevel.Warning,
                    "SWP line {0}: no ACT_READY (attempt {1}); re-sending ACT_POWER_MODE with FR = 1",
                    line, attempt + 1);
            }

            if(ready == null)
            {
                this.Log(LogLevel.Warning, "Activation of SWP line {0} failed: no ACT_READY frame", line);
                target.Deactivate();
                link.State = SWPInterfaceState.Deactivated;
                return false;
            }

            // ACT_READY completes the sequence; the interface is now available for data transfer.
            link.PowerMode = PowerMode;
            link.State = SWPInterfaceState.Activated;
            this.Log(LogLevel.Info, "SWP line {0} activated in {1} mode", line, PowerMode);

            return EstablishLink(line, target, link);
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

        // Sends one LLC payload in an SHDLC I-frame and returns the payload the UICC answered with
        // (empty when it only acknowledged with an RR). A REJ is honoured by retransmitting once.
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
            var answer = Exchange(line, target, SWPProtocol.BuildInformation(sequence, link.ReceiveSequence, payload));
            link.SendSequence = (sequence + 1) % SWPProtocol.SequenceModulo;

            var result = ProcessResponse(line, link, answer, out var rejectSequence);
            if(rejectSequence < 0)
            {
                return result;
            }

            // A REJ asks for retransmission starting at its N(R). Resynchronise our send sequence to
            // it and send the frame again - blindly reusing the refused N(S) would just be rejected.
            Retransmissions++;
            this.Log(LogLevel.Debug, "SWP line {0}: REJ received, retransmitting I-frame with N(S) = {1}",
                line, rejectSequence);
            link.SendSequence = (rejectSequence + 1) % SWPProtocol.SequenceModulo;
            answer = Exchange(line, target, SWPProtocol.BuildInformation(rejectSequence, link.ReceiveSequence, payload));
            return ProcessResponse(line, link, answer, out _);
        }

        // Monitor-friendly helper: send hex-encoded data, get the hex-encoded answer back.
        public string SendHex(int line, string hexPayload)
        {
            return Misc.PrettyPrintCollectionHex(Send(line, Misc.HexStringToByteArray(hexPayload)));
        }

        // Sends a bare RR - a poll that acknowledges what we have received and gives the UICC a slot
        // in which to answer. Returns any payload it sends back.
        public byte[] Poll(int line)
        {
            if(!TryGetTarget(line, out var target) || !TryGetLink(line, out var link) || !link.Established)
            {
                return Nothing;
            }
            var answer = Exchange(line, target,
                SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.ReceiveReady, link.ReceiveSequence));
            return ProcessResponse(line, link, answer, out _);
        }

        public string PollHex(int line)
        {
            return Misc.PrettyPrintCollectionHex(Poll(line));
        }

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

        // SWP line the most recent unsolicited frame came from, or -1 if none since reset.
        public int LastReceivedLine { get; private set; } = -1;

        // Payload of the most recent frame received, hex-encoded (monitor-readable).
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

        private bool EstablishLink(int line, ISWPPeripheral target, Link link)
        {
            var reset = SWPProtocol.BuildUnnumbered(SWPProtocol.UnnumberedFrameModifier.Reset,
                SWPProtocol.BuildResetParameters((byte)WindowSize, SelectiveRejectSupport));
            if(!TryReceive(line, Exchange(line, target, reset), out var payload) || payload.Length == 0
                || SWPProtocol.GetFrameKind(payload[0]) != SWPProtocol.ShdlcFrameKind.Unnumbered
                || SWPProtocol.GetModifier(payload[0]) != SWPProtocol.UnnumberedFrameModifier.UnnumberedAcknowledgement)
            {
                this.Log(LogLevel.Warning, "SWP line {0}: the SHDLC RSET was not acknowledged with a UA", line);
                return false;
            }

            link.WindowSize = payload.Length > 1 ? Math.Max(1, Math.Min(payload[1], WindowSize)) : WindowSize;
            link.SendSequence = 0;
            link.ReceiveSequence = 0;
            link.Established = true;
            this.Log(LogLevel.Info, "SWP line {0}: SHDLC link established (window {1})", line, link.WindowSize);
            return true;
        }

        // Sends one LLC payload as a wire frame and returns the target's wire answer.
        private byte[] Exchange(int line, ISWPPeripheral target, byte[] payload)
        {
            FramesSent++;
            this.Log(LogLevel.Noisy, "SWP line {0}: sending control 0x{1:X2} with {2} payload byte(s)",
                line, payload.Length > 0 ? payload[0] : 0, Math.Max(0, payload.Length - 1));
            return target.ExchangeFrame(SWPFrame.Encode(payload));
        }

        // Decodes a wire frame from a target, counting CRC/framing errors.
        private bool TryReceive(int line, byte[] wire, out byte[] payload)
        {
            payload = Nothing;
            if(wire == null || wire.Length == 0)
            {
                return false;
            }
            if(!SWPFrame.TryDecode(wire, out payload, out var error))
            {
                CrcErrors++;
                this.Log(LogLevel.Warning, "SWP line {0}: discarding a malformed frame: {1}", line, error);
                payload = Nothing;
                return false;
            }
            FramesReceived++;
            return true;
        }

        // Handles the SHDLC frame a target sent back in the same slot. Sets rejectSequence to the
        // N(R) of a REJ frame - the sequence number the target wants retransmitted - or to -1 when
        // the target did not ask for a retransmission.
        private byte[] ProcessResponse(int line, Link link, byte[] wire, out int rejectSequence)
        {
            rejectSequence = -1;
            if(!TryReceive(line, wire, out var payload) || payload.Length == 0)
            {
                return Nothing;
            }

            var control = payload[0];
            switch(SWPProtocol.GetFrameKind(control))
            {
            case SWPProtocol.ShdlcFrameKind.Supervisory:
                var type = SWPProtocol.GetSupervisoryType(control);
                if(type == SWPProtocol.SupervisoryFrameType.Reject)
                {
                    RejectsReceived++;
                    rejectSequence = SWPProtocol.GetReceiveSequence(control);
                }
                else if(type == SWPProtocol.SupervisoryFrameType.ReceiveNotReady)
                {
                    this.Log(LogLevel.Warning, "SWP line {0}: the UICC signalled RNR (busy)", line);
                }
                return Nothing;

            case SWPProtocol.ShdlcFrameKind.Information:
                var sendSequence = SWPProtocol.GetSendSequence(control);
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
                return information;

            default:
                this.Log(LogLevel.Debug, "SWP line {0}: U-frame 0x{1:X2} received outside link establishment",
                    line, control);
                return Nothing;
            }
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

        // A target transmitted on S2 on its own initiative (SWP is full duplex). Decode it, advance
        // the SHDLC receive sequence and raise the IRQ line so firmware or a test can react.
        private void HandleTargetFrame(ISWPPeripheral target, byte[] wire)
        {
            var line = ChildCollection.Where(x => ReferenceEquals(x.Value, target))
                .Select(x => (int?)x.Key).FirstOrDefault() ?? -1;
            if(line < 0 || !TryGetLink(line, out var link))
            {
                return;
            }

            var payload = ProcessResponse(line, link, wire, out _);
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
