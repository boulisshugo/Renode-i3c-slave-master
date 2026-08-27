//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

namespace Antmicro.Renode.Peripherals.SWP
{
    // The UICC-side protocol layers of an ETSI TS 102 613 link - the ACT activation LLC (clause 11)
    // and SHDLC (clause 10) - as a plain state machine over LLC payloads.
    //
    // WHERE THIS BELONGS
    //
    // On real silicon this code is FIRMWARE. The SWP contact does the wire (SOF/EOF, bit stuffing,
    // CRC); the chip's firmware decides what ACT_SYNC advertises, when ACT_READY goes out, whether
    // an RSET is accepted, and what N(R) acknowledges. SimpleSWPPeripheral therefore does not
    // contain any of it, and InventedSWPTarget - the firmware-in-the-loop model - hands raw payloads
    // to the emulated CPU and lets the firmware do exactly this.
    //
    // This class exists for the case where there is no firmware to run: mocks, the consistency
    // suites, a bench that wants a well-behaved UICC on the far end. It is a HOST-SIDE STAND-IN for
    // that firmware, kept in a class of its own so it is obvious which side of the hardware/firmware
    // line a given behaviour is on. SoftwareSWPTarget is SimpleSWPPeripheral plus one of these.
    //
    // It is also the reference: a firmware port that implements the same transitions in C will
    // interoperate with SimpleSWPController, byte for byte.
    //
    // No Renode types are used here on purpose - it is a pure state machine, which makes it trivial
    // to unit-test and to transcribe into firmware. Feed it with HandlePayload and it returns the
    // payload to answer with, or null for silence.
    public class SWPTargetStack
    {
        // --------------------------------------------------------------------------------------
        // Capabilities advertised in ACT_INFORMATION
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
        // State
        // --------------------------------------------------------------------------------------

        public SWPInterfaceState InterfaceState { get; private set; } = SWPInterfaceState.Deactivated;

        // Power mode the CLF selected in ACT_POWER_MODE.
        public SWPPowerMode PowerMode { get; private set; } = SWPPowerMode.LowPower;

        // True once the SHDLC RSET/UA handshake has completed.
        public bool LinkEstablished { get; private set; }

        // SHDLC window size agreed with the CLF (the smaller of the two proposals).
        public int WindowSize { get; private set; } = SWPProtocol.DefaultWindowSize;

        // Number of REJ frames sent (an out-of-sequence I-frame arrived).
        public int RejectsSent { get; private set; }

        // Information field of the last I-frame accepted (the control byte stripped).
        public byte[] LastReceivedInformation { get; private set; } = new byte[0];

        // --------------------------------------------------------------------------------------
        // Hooks
        // --------------------------------------------------------------------------------------

        // Called with the information field of a well-sequenced I-frame. Return a payload to answer
        // with an I-frame of our own (the acknowledgement rides along in its N(R)), or null/empty to
        // answer with a bare RR. Not set: always answer with RR.
        public Func<byte[], byte[]> InformationHandler { get; set; }

        // Called once the SHDLC RSET/UA handshake has completed.
        public Action LinkEstablishedHandler { get; set; }

        // Optional tracing, wired by the hosting peripheral to Renode's logger.
        public Action<string> DebugLog { get; set; }
        public Action<string> WarningLog { get; set; }

        // --------------------------------------------------------------------------------------
        // Driving the state machine
        // --------------------------------------------------------------------------------------

        public void Reset()
        {
            ResetLinkState();
            InterfaceState = SWPInterfaceState.Deactivated;
            PowerMode = SWPPowerMode.LowPower;
            RejectsSent = 0;
        }

        // S1 has come up. The UICC announces itself with ACT_SYNC carrying its ACT_INFORMATION;
        // the returned payload is what goes out on S2.
        public byte[] Activate()
        {
            ResetLinkState();
            InterfaceState = SWPInterfaceState.ActSync;
            var payload = SWPProtocol.BuildActSync(ProtocolVersion, SupportedLlcs,
                (ushort)MaxFramePayloadSize, SupportedPowerModes);
            lastActPayload = payload;
            Debug("Interface activated by the CLF; sending ACT_SYNC");
            return payload;
        }

        // S1 has gone low: the interface is unpowered and keeps no state.
        public void Deactivate()
        {
            ResetLinkState();
            InterfaceState = SWPInterfaceState.Deactivated;
            PowerMode = SWPPowerMode.LowPower;
        }

        // Feeds one received LLC payload - control field first - through ACT or SHDLC as the current
        // interface state dictates. Returns the payload to answer with, or null for silence.
        public byte[] HandlePayload(byte[] payload)
        {
            if(payload == null || payload.Length == 0)
            {
                return null;
            }
            if(InterfaceState == SWPInterfaceState.Deactivated)
            {
                Warning("Payload received while the interface is deactivated - ignored");
                return null;
            }
            if(InterfaceState == SWPInterfaceState.Activated)
            {
                return HandleShdlcPayload(payload);
            }
            if(InterfaceState == SWPInterfaceState.ActReady && payload[0] != SWPProtocol.ActPowerMode)
            {
                // ACT_READY has been sent and the CLF has moved on to SHDLC, so it clearly got it.
                // Until such a frame arrives the UICC stays in ActReady and keeps answering a
                // frame-resend request with ACT_READY again.
                InterfaceState = SWPInterfaceState.Activated;
                return HandleShdlcPayload(payload);
            }
            return HandleActPayload(payload);
        }

        // Builds the next I-frame payload to transmit on the UICC's own initiative and advances the
        // send sequence. SWP is full duplex, so the UICC does not have to wait to be polled.
        // Returns null when the SHDLC link is not up - there is no legal frame to send then.
        public byte[] BuildInformation(byte[] information)
        {
            if(!LinkEstablished)
            {
                Warning("Cannot send an I-frame: the SHDLC link is not established");
                return null;
            }
            return BuildAndRecordInformation(information);
        }

        // --------------------------------------------------------------------------------------
        // ACT LLC (clause 11)
        // --------------------------------------------------------------------------------------

        private byte[] HandleActPayload(byte[] payload)
        {
            var control = payload[0];
            if(control != SWPProtocol.ActPowerMode)
            {
                Warning($"Unexpected ACT control field 0x{control:X2} in state {InterfaceState}");
                return null;
            }

            var parameter = payload.Length > 1 ? payload[1] : (byte)0;
            if((parameter & SWPProtocol.ActPowerModeFrameResendBit) != 0)
            {
                // FR = 1: the CLF did not get our last ACT frame intact, so repeat it verbatim.
                Debug("ACT_POWER_MODE with FR = 1; repeating the last ACT frame");
                return lastActPayload;
            }

            PowerMode = (parameter & SWPProtocol.ActPowerModeFullPowerBit) != 0
                ? SWPPowerMode.FullPower
                : SWPPowerMode.LowPower;
            InterfaceState = SWPInterfaceState.ActPowerMode;

            // Acknowledge with ACT_READY and rest in ActReady. The UICC cannot know that frame
            // arrived intact: if the CLF asks again with FR = 1 we repeat it, and only the first
            // non-ACT frame proves the CLF is done activating (see HandlePayload).
            var ready = SWPProtocol.BuildActReady();
            lastActPayload = ready;
            InterfaceState = SWPInterfaceState.ActReady;
            Debug($"ACT_POWER_MODE ({PowerMode}); answering ACT_READY");
            return ready;
        }

        // --------------------------------------------------------------------------------------
        // SHDLC LLC (clause 10)
        // --------------------------------------------------------------------------------------

        private byte[] HandleShdlcPayload(byte[] payload)
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
                Warning($"Unhandled U-frame modifier 0x{(int)modifier:X2}");
                return null;
            }

            // RSET: restart the link. The parameters the CLF proposes are a window size and whether
            // it supports selective reject; we accept the smaller window and the intersection of the
            // SREJ support, and echo what we accepted back in the UA.
            var proposedWindow = payload.Length > 1 ? payload[1] : SWPProtocol.DefaultWindowSize;
            var proposedSrej = payload.Length > 2 && (payload[2] & 0x01) != 0;

            ResetLinkState();
            InterfaceState = SWPInterfaceState.Activated;
            WindowSize = Math.Max(1, Math.Min(proposedWindow, MaxWindowSize));
            var srej = proposedSrej && SelectiveRejectSupport;
            LinkEstablished = true;

            Debug($"SHDLC RSET accepted (window {WindowSize}, SREJ {srej}); answering UA");
            var ua = SWPProtocol.BuildUnnumbered(SWPProtocol.UnnumberedFrameModifier.UnnumberedAcknowledgement,
                SWPProtocol.BuildResetParameters((byte)WindowSize, srej));
            LinkEstablishedHandler?.Invoke();
            return ua;
        }

        private byte[] HandleSupervisory(byte control)
        {
            var type = SWPProtocol.GetSupervisoryType(control);
            AcknowledgeUpTo(SWPProtocol.GetReceiveSequence(control));
            switch(type)
            {
            case SWPProtocol.SupervisoryFrameType.ReceiveReady:
                return null;
            case SWPProtocol.SupervisoryFrameType.Reject:
                // The CLF missed our last I-frame. Its REJ names the N(R) it wants next, so
                // resynchronise to that and send the buffered payload again.
                if(lastInformationPayload == null)
                {
                    return null;
                }
                var wanted = SWPProtocol.GetReceiveSequence(control);
                Debug($"REJ received; retransmitting I-frame with N(S) = {wanted}");
                sendSequence = (wanted + 1) % SWPProtocol.SequenceModulo;
                return SWPProtocol.BuildInformation(wanted, receiveSequence, lastInformationPayload);
            default:
                Debug($"S-frame {type} received");
                return null;
            }
        }

        private byte[] HandleInformation(byte control, byte[] payload)
        {
            if(!LinkEstablished)
            {
                Warning("I-frame received before the SHDLC link was established - ignored");
                return null;
            }

            var theirSendSequence = SWPProtocol.GetSendSequence(control);
            AcknowledgeUpTo(SWPProtocol.GetReceiveSequence(control));

            if(theirSendSequence != receiveSequence)
            {
                // Out of sequence: ask for a retransmission from the frame we do expect.
                RejectsSent++;
                Warning($"Out-of-sequence I-frame: N(S) = {theirSendSequence}, expected {receiveSequence}; sending REJ");
                return SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.Reject, receiveSequence);
            }

            var information = new byte[payload.Length - 1];
            Array.Copy(payload, 1, information, 0, information.Length);
            LastReceivedInformation = information;
            receiveSequence = (receiveSequence + 1) % SWPProtocol.SequenceModulo;

            var response = InformationHandler?.Invoke(information);
            if(response == null || response.Length == 0)
            {
                // Nothing to say: acknowledge with a bare RR carrying our updated N(R).
                return SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.ReceiveReady, receiveSequence);
            }
            // Piggyback the acknowledgement on our own I-frame.
            return BuildAndRecordInformation(response);
        }

        // --------------------------------------------------------------------------------------

        private byte[] BuildAndRecordInformation(byte[] information)
        {
            information = information ?? new byte[0];
            lastInformationPayload = information;
            var frame = SWPProtocol.BuildInformation(sendSequence, receiveSequence, information);
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

        private void ResetLinkState()
        {
            LinkEstablished = false;
            WindowSize = SWPProtocol.DefaultWindowSize;
            sendSequence = 0;
            receiveSequence = 0;
            lastInformationPayload = null;
            lastActPayload = null;
            LastReceivedInformation = new byte[0];
        }

        private void Debug(string message) => DebugLog?.Invoke(message);

        private void Warning(string message) => WarningLog?.Invoke(message);

        private int sendSequence;
        private int receiveSequence;
        private byte[] lastInformationPayload;
        private byte[] lastActPayload;
    }
}
