//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // A simple, agnostic SWP target (the UICC side of an ETSI TS 102 613 link).
    //
    // It implements the standard behaviour a UICC owes the CLF, so a proprietary model only has to
    // supply application logic:
    //   - the ACT activation LLC (clause 11): announces itself with ACT_SYNC + ACT_INFORMATION,
    //     answers ACT_POWER_MODE with ACT_READY, and honours the CLF's frame-resend (FR) request;
    //   - SHDLC (clause 10): the RSET/UA link-establishment handshake with window and SREJ
    //     negotiation, modulo-8 N(S)/N(R) sequencing, RR acknowledgements, REJ on an out-of-sequence
    //     I-frame and retransmission of the last I-frame on receiving a REJ;
    //   - the data link layer (clause 8): every frame in and out goes through SWPFrame, so the
    //     bit stuffing and the CRC really are computed and checked.
    //
    // Out of the box it answers each I-frame with the next payload queued by EnqueueResponsePayload
    // (or a bare RR acknowledgement when the queue is empty). Subclass it and override OnInformation
    // for proprietary behaviour, and call SendInformation to transmit on the UICC's own initiative -
    // SWP is full duplex, so the UICC does not have to wait to be polled.
    //
    // NOTE (same gotcha as the I3C/SPI models): the constructor calls the virtual Reset(), so every
    // field Reset() touches must be a field initializer, not a constructor-body assignment.
    public class SimpleSWPPeripheral : ISWPPeripheral
    {
        public SimpleSWPPeripheral()
        {
            Reset();
        }

        public virtual void Reset()
        {
            lock(locker)
            {
                responseQueue.Clear();
                ResetLinkState();
                InterfaceState = SWPInterfaceState.Deactivated;
                PowerMode = SWPPowerMode.LowPower;
            }
        }

        // --------------------------------------------------------------------------------------
        // ISWPPeripheral - physical / activation control driven by the CLF
        // --------------------------------------------------------------------------------------

        public SWPInterfaceState InterfaceState { get; private set; } = SWPInterfaceState.Deactivated;

        // The CLF starts driving S1. The UICC signals that it is ready to communicate by sending the
        // first ACT_SYNC frame, carrying its ACT_INFORMATION capabilities.
        public byte[] Activate()
        {
            lock(locker)
            {
                ResetLinkState();
                InterfaceState = SWPInterfaceState.ActSync;
                var payload = SWPProtocol.BuildActSync(ProtocolVersion, SupportedLlcs,
                    (ushort)MaxFramePayloadSize, SupportedPowerModes);
                lastActPayload = payload;
                this.Log(LogLevel.Debug, "Interface activated by the CLF; sending ACT_SYNC");
                return Transmit(payload);
            }
        }

        // The CLF drives S1 low. The interface is unpowered and keeps no state.
        public void Deactivate()
        {
            lock(locker)
            {
                if(InterfaceState == SWPInterfaceState.Deactivated)
                {
                    return;
                }
                ResetLinkState();
                InterfaceState = SWPInterfaceState.Deactivated;
            }
            this.Log(LogLevel.Debug, "Interface deactivated by the CLF");
            OnDeactivated();
        }

        // One full-duplex frame slot: the CLF's frame arrives on S1, the UICC's answer leaves on S2.
        public byte[] ExchangeFrame(byte[] wireFrame)
        {
            lock(locker)
            {
                if(InterfaceState == SWPInterfaceState.Deactivated)
                {
                    this.Log(LogLevel.Warning, "Frame received while the interface is deactivated - ignored");
                    return Nothing;
                }
                if(!SWPFrame.TryDecode(wireFrame, out var payload, out var error))
                {
                    FrameErrors++;
                    // A corrupted frame is simply not acknowledged: the CLF recovers by re-sending
                    // ACT_POWER_MODE with FR = 1 during activation, or by its SHDLC timeout after.
                    this.Log(LogLevel.Warning, "Discarding a malformed frame: {0}", error);
                    return Nothing;
                }
                FramesReceived++;
                if(payload.Length == 0)
                {
                    this.Log(LogLevel.Warning, "Discarding an empty frame (no control field)");
                    return Nothing;
                }
                if(InterfaceState == SWPInterfaceState.Activated)
                {
                    return HandleShdlcFrame(payload);
                }
                if(InterfaceState == SWPInterfaceState.ActReady && payload[0] != SWPProtocol.ActPowerMode)
                {
                    // ACT_READY has been sent and the CLF has moved on to SHDLC, so it clearly got
                    // it. Until such a frame arrives the UICC stays in ActReady and keeps answering
                    // a frame-resend request with ACT_READY again.
                    InterfaceState = SWPInterfaceState.Activated;
                    return HandleShdlcFrame(payload);
                }
                return HandleActFrame(payload);
            }
        }

        public event Action<ISWPPeripheral, byte[]> FrameAvailable;

        // --------------------------------------------------------------------------------------
        // Capabilities advertised in ACT_INFORMATION - settable from a .repl
        // --------------------------------------------------------------------------------------

        // SWP protocol version this UICC supports.
        public byte ProtocolVersion { get; set; } = 1;

        // The LLCs this UICC supports. SHDLC and ACT are mandatory in both the CLF and the UICC.
        public SWPProtocol.SupportedLlc SupportedLlcs { get; set; } =
            SWPProtocol.SupportedLlc.Shdlc | SWPProtocol.SupportedLlc.Act;

        // Largest LLC payload the UICC can receive in one frame, advertised in ACT_INFORMATION.
        public int MaxFramePayloadSize { get; set; } = 4096;

        // Power modes the UICC supports: bit0 low power, bit1 full power.
        public byte SupportedPowerModes { get; set; } = 0x03;

        // Largest SHDLC window the UICC will accept in the RSET handshake.
        public int MaxWindowSize { get; set; } = SWPProtocol.DefaultWindowSize;

        // Whether the UICC offers selective reject in the RSET handshake.
        public bool SelectiveRejectSupport { get; set; } = SWPProtocol.DefaultSelectiveRejectSupport;

        // --------------------------------------------------------------------------------------
        // Observable state - monitor and robot friendly
        // --------------------------------------------------------------------------------------

        // Power mode the CLF selected in ACT_POWER_MODE.
        public SWPPowerMode PowerMode { get; private set; } = SWPPowerMode.LowPower;

        // True once the SHDLC RSET/UA handshake has completed.
        public bool LinkEstablished { get; private set; }

        // SHDLC window size agreed with the CLF (the smaller of the two proposals).
        public int WindowSize { get; private set; } = SWPProtocol.DefaultWindowSize;

        // Payload of the last I-frame received, hex-encoded.
        public string LastReceivedPayloadHex => Misc.PrettyPrintCollectionHex(lastReceivedPayload);

        public int FramesReceived { get; private set; }
        public int FramesSent { get; private set; }

        // Frames dropped because their CRC or framing was bad.
        public int FrameErrors { get; private set; }

        // Number of REJ frames this target has sent (an out-of-sequence I-frame arrived).
        public int RejectsSent { get; private set; }

        // Queues one payload to be returned in an I-frame in answer to the next I-frame received.
        public void EnqueueResponsePayload(IEnumerable<byte> payload)
        {
            lock(locker)
            {
                responseQueue.Enqueue(payload.ToArray());
            }
        }

        // Monitor-friendly helper: queue one response payload from a hex string, e.g. "0102ab".
        public void EnqueueResponsePayloadHex(string hexPayload)
        {
            EnqueueResponsePayload(Misc.HexStringToByteArray(hexPayload));
        }

        // --------------------------------------------------------------------------------------
        // Hooks for proprietary targets
        // --------------------------------------------------------------------------------------

        // Called with the payload of a well-sequenced SHDLC I-frame. Return a payload to answer with
        // an I-frame of our own (the acknowledgement rides along in its N(R)); return null or an
        // empty array to answer with a bare RR acknowledgement.
        //
        // Default: the next payload queued with EnqueueResponsePayload, else null.
        protected virtual byte[] OnInformation(byte[] payload)
        {
            return responseQueue.Count > 0 ? responseQueue.Dequeue() : null;
        }

        // Called once the SHDLC RSET/UA handshake has completed. Default: no-op.
        protected virtual void OnLinkEstablished()
        {
        }

        // Called when the CLF deactivates the interface. Default: no-op.
        protected virtual void OnDeactivated()
        {
        }

        // Transmits an I-frame on the UICC's own initiative (SWP is full duplex, so the UICC may
        // transmit on S2 without being polled). Raises FrameAvailable with the complete wire frame.
        protected void SendInformation(byte[] payload)
        {
            byte[] wire;
            lock(locker)
            {
                if(!LinkEstablished)
                {
                    this.Log(LogLevel.Warning, "Cannot send an I-frame: the SHDLC link is not established");
                    return;
                }
                wire = Transmit(BuildAndRecordInformation(payload));
            }
            this.Log(LogLevel.Debug, "Transmitting an unsolicited {0}-byte I-frame", payload?.Length ?? 0);
            FrameAvailable?.Invoke(this, wire);
        }

        // --------------------------------------------------------------------------------------
        // ACT LLC
        // --------------------------------------------------------------------------------------

        private byte[] HandleActFrame(byte[] payload)
        {
            var control = payload[0];
            if(control != SWPProtocol.ActPowerMode)
            {
                this.Log(LogLevel.Warning, "Unexpected ACT control field 0x{0:X2} in state {1}", control, InterfaceState);
                return Nothing;
            }

            var parameter = payload.Length > 1 ? payload[1] : (byte)0;
            if((parameter & SWPProtocol.ActPowerModeFrameResendBit) != 0)
            {
                // FR = 1: the CLF did not get our last ACT frame intact, so repeat it verbatim.
                this.Log(LogLevel.Debug, "ACT_POWER_MODE with FR = 1; repeating the last ACT frame");
                return lastActPayload != null ? Transmit(lastActPayload) : Nothing;
            }

            PowerMode = (parameter & SWPProtocol.ActPowerModeFullPowerBit) != 0
                ? SWPPowerMode.FullPower
                : SWPPowerMode.LowPower;
            InterfaceState = SWPInterfaceState.ActPowerMode;

            // Acknowledge with ACT_READY and rest in ActReady. The UICC cannot know that frame
            // arrived intact: if the CLF asks again with FR = 1 we repeat it, and only the first
            // non-ACT frame proves the CLF is done activating (see ExchangeFrame).
            var ready = SWPProtocol.BuildActReady();
            lastActPayload = ready;
            InterfaceState = SWPInterfaceState.ActReady;
            this.Log(LogLevel.Debug, "ACT_POWER_MODE ({0}); answering ACT_READY", PowerMode);
            return Transmit(ready);
        }

        // --------------------------------------------------------------------------------------
        // SHDLC LLC
        // --------------------------------------------------------------------------------------

        private byte[] HandleShdlcFrame(byte[] payload)
        {
            var control = payload[0];
            switch(SWPProtocol.GetFrameKind(control))
            {
            case SWPProtocol.ShdlcFrameKind.Unnumbered:
                return HandleUnnumbered(control, payload);
            case SWPProtocol.ShdlcFrameKind.Supervisory:
                return HandleSupervisory(control);
            default:
                return HandleInformation(control, payload);
            }
        }

        private byte[] HandleUnnumbered(byte control, byte[] payload)
        {
            var modifier = SWPProtocol.GetModifier(control);
            if(modifier != SWPProtocol.UnnumberedFrameModifier.Reset)
            {
                this.Log(LogLevel.Warning, "Unhandled U-frame modifier 0x{0:X2}", (int)modifier);
                return Nothing;
            }

            // RSET: restart the link. The parameters the CLF proposes are a window size and whether
            // it supports selective reject; we accept the smaller window and the intersection of the
            // SREJ support, and echo what we accepted back in the UA.
            var proposedWindow = payload.Length > 1 ? payload[1] : SWPProtocol.DefaultWindowSize;
            var proposedSrej = payload.Length > 2 && (payload[2] & 0x01) != 0;

            ResetLinkState();
            WindowSize = Math.Max(1, Math.Min(proposedWindow, MaxWindowSize));
            var srej = proposedSrej && SelectiveRejectSupport;
            LinkEstablished = true;

            this.Log(LogLevel.Debug, "SHDLC RSET accepted (window {0}, SREJ {1}); answering UA", WindowSize, srej);
            var ua = SWPProtocol.BuildUnnumbered(SWPProtocol.UnnumberedFrameModifier.UnnumberedAcknowledgement,
                SWPProtocol.BuildResetParameters((byte)WindowSize, srej));
            var wire = Transmit(ua);
            OnLinkEstablished();
            return wire;
        }

        private byte[] HandleSupervisory(byte control)
        {
            var type = SWPProtocol.GetSupervisoryType(control);
            AcknowledgeUpTo(SWPProtocol.GetReceiveSequence(control));
            switch(type)
            {
            case SWPProtocol.SupervisoryFrameType.ReceiveReady:
                return Nothing;
            case SWPProtocol.SupervisoryFrameType.Reject:
                // The CLF missed our last I-frame. Its REJ names the N(R) it wants next, so
                // resynchronise to that and send the buffered payload again.
                if(lastInformationPayload == null)
                {
                    return Nothing;
                }
                var wanted = SWPProtocol.GetReceiveSequence(control);
                this.Log(LogLevel.Debug, "REJ received; retransmitting I-frame with N(S) = {0}", wanted);
                lastInformationSequence = wanted;
                sendSequence = (wanted + 1) % SWPProtocol.SequenceModulo;
                return Transmit(SWPProtocol.BuildInformation(wanted, receiveSequence, lastInformationPayload));
            default:
                this.Log(LogLevel.Debug, "S-frame {0} received", type);
                return Nothing;
            }
        }

        private byte[] HandleInformation(byte control, byte[] payload)
        {
            if(!LinkEstablished)
            {
                this.Log(LogLevel.Warning, "I-frame received before the SHDLC link was established - ignored");
                return Nothing;
            }

            var theirSendSequence = SWPProtocol.GetSendSequence(control);
            AcknowledgeUpTo(SWPProtocol.GetReceiveSequence(control));

            if(theirSendSequence != receiveSequence)
            {
                // Out of sequence: ask for a retransmission from the frame we do expect.
                RejectsSent++;
                this.Log(LogLevel.Warning, "Out-of-sequence I-frame: N(S) = {0}, expected {1}; sending REJ",
                    theirSendSequence, receiveSequence);
                return Transmit(SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.Reject,
                    receiveSequence));
            }

            var information = new byte[payload.Length - 1];
            Array.Copy(payload, 1, information, 0, information.Length);
            lastReceivedPayload = information;
            receiveSequence = (receiveSequence + 1) % SWPProtocol.SequenceModulo;

            var response = OnInformation(information);
            if(response == null || response.Length == 0)
            {
                // Nothing to say: acknowledge with a bare RR carrying our updated N(R).
                return Transmit(SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.ReceiveReady,
                    receiveSequence));
            }
            // Piggyback the acknowledgement on our own I-frame.
            return Transmit(BuildAndRecordInformation(response));
        }

        // --------------------------------------------------------------------------------------

        private byte[] BuildAndRecordInformation(byte[] payload)
        {
            payload = payload ?? new byte[0];
            lastInformationPayload = payload;
            lastInformationSequence = sendSequence;
            var frame = SWPProtocol.BuildInformation(sendSequence, receiveSequence, payload);
            sendSequence = (sendSequence + 1) % SWPProtocol.SequenceModulo;
            return frame;
        }

        // The CLF's N(R) acknowledges everything it has received; once it covers our last I-frame we
        // no longer need to keep it for a possible retransmission.
        private void AcknowledgeUpTo(int theirReceiveSequence)
        {
            if(lastInformationPayload != null && theirReceiveSequence == sendSequence)
            {
                lastInformationPayload = null;
            }
        }

        private byte[] Transmit(byte[] payload)
        {
            FramesSent++;
            return SWPFrame.Encode(payload);
        }

        private void ResetLinkState()
        {
            LinkEstablished = false;
            WindowSize = SWPProtocol.DefaultWindowSize;
            sendSequence = 0;
            receiveSequence = 0;
            lastInformationPayload = null;
            lastInformationSequence = 0;
            lastActPayload = null;
            lastReceivedPayload = new byte[0];
        }

        private int sendSequence;
        private int receiveSequence;
        private int lastInformationSequence;
        private byte[] lastInformationPayload;
        private byte[] lastActPayload;
        private byte[] lastReceivedPayload = new byte[0];

        private readonly Queue<byte[]> responseQueue = new Queue<byte[]>();
        private readonly object locker = new object();

        private static readonly byte[] Nothing = new byte[0];
    }
}
