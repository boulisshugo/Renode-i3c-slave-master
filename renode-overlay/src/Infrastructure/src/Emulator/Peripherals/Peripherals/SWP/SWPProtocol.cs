//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

namespace Antmicro.Renode.Peripherals.SWP
{
    // The SWP Logical Link Control layers (ETSI TS 102 613 clauses 10 and 11).
    //
    // The control field is the first byte of an SWP frame payload. Which LLC that byte belongs to
    // follows from the interface state: the ACT LLC carries the activation sequence, and once the
    // interface is ACTIVATED the generic LLC is SHDLC. The two encodings do not overlap - SHDLC
    // occupies '80'..'FF' (its three head bits are 100/101/110/111) and ACT lives below it - so a
    // model can also sanity-check a control byte against the state it is in.
    //
    // Fidelity note: the SHDLC control-byte encoding, the RSET/UA handshake and its window/SREJ
    // parameters below are the ETSI encoding as implemented by, e.g., the Linux kernel NFC stack
    // (net/nfc/hci/llc_shdlc.c). For the ACT LLC the frame set, the fields they carry and the
    // sequencing are per the specification, but the numeric opcodes and the ACT_INFORMATION layout
    // are this model's profile - they are gathered here so that matching real silicon is a matter of
    // changing these constants and nothing else.
    public static class SWPProtocol
    {
        // ------------------------------------------------------------------------------------------
        // ACT LLC (clause 11) - the interface activation sequence.
        //
        //   UICC -> CLF   ACT_SYNC        + ACT_INFORMATION (the UICC's capabilities)
        //   CLF  -> UICC  ACT_POWER_MODE  + parameter byte (power mode, FR)
        //   UICC -> CLF   ACT_READY
        //
        // If the CLF receives a corrupted frame it re-sends ACT_POWER_MODE with FR = 1, asking the
        // UICC to repeat its last ACT frame.
        // ------------------------------------------------------------------------------------------

        public const byte ActSync = 0x01;
        public const byte ActPowerMode = 0x02;
        public const byte ActReady = 0x03;

        // ACT_POWER_MODE parameter byte.
        public const byte ActPowerModeFullPowerBit = 0x01; // 0 = low power mode, 1 = full power mode
        public const byte ActPowerModeFrameResendBit = 0x02; // FR: repeat the last ACT frame

        // Bitmap of the LLCs a UICC advertises in ACT_INFORMATION.
        [Flags]
        public enum SupportedLlc : byte
        {
            None = 0x00,
            Shdlc = 0x01,
            Clt = 0x02,
            Act = 0x04,
        }

        // Builds the ACT_SYNC payload: the control byte followed by the ACT_INFORMATION field.
        //
        // ACT_INFORMATION here is
        //   [0]    SWP protocol version supported by the UICC
        //   [1]    supported-LLC bitmap (see SupportedLlc)
        //   [2..3] maximum frame payload the UICC can receive, in bytes, most significant byte first
        //   [4]    supported power modes (bit0 low power, bit1 full power)
        public static byte[] BuildActSync(byte version, SupportedLlc llcs, ushort maxFrameSize, byte powerModes)
        {
            return new byte[]
            {
                ActSync,
                version,
                (byte)llcs,
                (byte)(maxFrameSize >> 8),
                (byte)maxFrameSize,
                powerModes,
            };
        }

        // Builds the ACT_POWER_MODE payload the CLF sends in reply to ACT_SYNC.
        public static byte[] BuildActPowerMode(SWPPowerMode mode, bool frameResend)
        {
            byte parameter = 0;
            if(mode == SWPPowerMode.FullPower)
            {
                parameter |= ActPowerModeFullPowerBit;
            }
            if(frameResend)
            {
                parameter |= ActPowerModeFrameResendBit;
            }
            return new byte[] { ActPowerMode, parameter };
        }

        // Builds the ACT_READY payload the UICC sends to complete activation.
        public static byte[] BuildActReady()
        {
            return new byte[] { ActReady };
        }

        // ------------------------------------------------------------------------------------------
        // SHDLC LLC (clause 10) - the generic data link used once the interface is ACTIVATED.
        //
        // Control byte:
        //   I-frame   1 0 x  N(S)2..0  N(R)2..0   ('80' / 'A0' head)
        //   S-frame   1 1 0  type1..0  N(R)2..0   ('C0' head; type: RR, REJ, RNR, SREJ)
        //   U-frame   1 1 1  modifier4..0         ('E0' head; modifier: RSET, UA)
        // ------------------------------------------------------------------------------------------

        public const byte ControlHeadMask = 0xE0;
        public const byte ControlHeadInformation = 0x80;
        public const byte ControlHeadInformation2 = 0xA0;
        public const byte ControlHeadSupervisory = 0xC0;
        public const byte ControlHeadUnnumbered = 0xE0;

        public const byte ControlSendSequenceMask = 0x38;  // N(S), bits 5..3
        public const byte ControlReceiveSequenceMask = 0x07; // N(R), bits 2..0
        public const byte ControlSupervisoryTypeMask = 0x18; // S-frame type, bits 4..3
        public const byte ControlModifierMask = 0x1F;        // U-frame modifier, bits 4..0

        // Sequence numbers are modulo 8.
        public const int SequenceModulo = 8;

        // Default SHDLC window size negotiated in RSET, and whether selective reject is offered.
        public const byte DefaultWindowSize = 4;
        public const bool DefaultSelectiveRejectSupport = false;

        public enum SupervisoryFrameType
        {
            ReceiveReady = 0x00,
            Reject = 0x01,
            ReceiveNotReady = 0x02,
            SelectiveReject = 0x03,
        }

        public enum UnnumberedFrameModifier
        {
            UnnumberedAcknowledgement = 0x06,
            Reset = 0x19,
        }

        public enum ShdlcFrameKind
        {
            Information,
            Supervisory,
            Unnumbered,
        }

        public static ShdlcFrameKind GetFrameKind(byte control)
        {
            switch(control & ControlHeadMask)
            {
            case ControlHeadInformation:
            case ControlHeadInformation2:
                return ShdlcFrameKind.Information;
            case ControlHeadSupervisory:
                return ShdlcFrameKind.Supervisory;
            default:
                return ShdlcFrameKind.Unnumbered;
            }
        }

        public static int GetSendSequence(byte control) => (control & ControlSendSequenceMask) >> 3;

        public static int GetReceiveSequence(byte control) => control & ControlReceiveSequenceMask;

        public static SupervisoryFrameType GetSupervisoryType(byte control)
            => (SupervisoryFrameType)((control & ControlSupervisoryTypeMask) >> 3);

        public static UnnumberedFrameModifier GetModifier(byte control)
            => (UnnumberedFrameModifier)(control & ControlModifierMask);

        // Builds an I-frame: the control byte carrying N(S) and N(R), followed by the LLC payload
        // (an HCI packet in a real stack; any opaque bytes here).
        public static byte[] BuildInformation(int sendSequence, int receiveSequence, byte[] payload)
        {
            payload = payload ?? new byte[0];
            var frame = new byte[payload.Length + 1];
            frame[0] = (byte)(ControlHeadInformation
                | ((sendSequence % SequenceModulo) << 3)
                | (receiveSequence % SequenceModulo));
            Array.Copy(payload, 0, frame, 1, payload.Length);
            return frame;
        }

        // Builds an S-frame (RR / REJ / RNR / SREJ) acknowledging up to receiveSequence.
        public static byte[] BuildSupervisory(SupervisoryFrameType type, int receiveSequence)
        {
            return new byte[]
            {
                (byte)(ControlHeadSupervisory | ((int)type << 3) | (receiveSequence % SequenceModulo)),
            };
        }

        // Builds a U-frame. RSET carries the proposed window size and SREJ support; UA echoes the
        // parameters the responder accepts.
        public static byte[] BuildUnnumbered(UnnumberedFrameModifier modifier, byte[] parameters = null)
        {
            parameters = parameters ?? new byte[0];
            var frame = new byte[parameters.Length + 1];
            frame[0] = (byte)(ControlHeadUnnumbered | ((int)modifier & ControlModifierMask));
            Array.Copy(parameters, 0, frame, 1, parameters.Length);
            return frame;
        }

        // Builds the RSET / UA parameter field: window size then SREJ support.
        public static byte[] BuildResetParameters(byte windowSize, bool selectiveRejectSupport)
        {
            return new byte[] { windowSize, (byte)(selectiveRejectSupport ? 1 : 0) };
        }

        // ------------------------------------------------------------------------------------------
        // Decoding a frame for a human
        // ------------------------------------------------------------------------------------------

        // Names an LLC payload from its control field: "ACT_SYNC", "I N(S)=0 N(R)=1 +2B", "RR N(R)=2".
        // Used by the frame trace, and useful from a test or a proprietary model reading a capture.
        //
        // Which LLC a control byte belongs to follows from its value: the ACT opcodes sit at the bottom
        // of the range and SHDLC occupies '80'..'FF', so the two can be told apart without knowing the
        // interface state.
        public static string Describe(byte[] payload)
        {
            if(payload == null || payload.Length == 0)
            {
                return "(empty)";
            }

            var control = payload[0];
            var extra = payload.Length > 1 ? $" +{payload.Length - 1}B" : string.Empty;

            switch(control)
            {
            case ActSync:
                return "ACT_SYNC" + extra;
            case ActReady:
                return "ACT_READY" + extra;
            case ActPowerMode:
                var parameter = payload.Length > 1 ? payload[1] : (byte)0;
                var mode = (parameter & ActPowerModeFullPowerBit) != 0 ? "full power" : "low power";
                var resend = (parameter & ActPowerModeFrameResendBit) != 0 ? " FR=1" : string.Empty;
                return $"ACT_POWER_MODE {mode}{resend}";
            }

            if(control < ControlHeadInformation)
            {
                return $"unknown control 0x{control:X2}{extra}";
            }

            switch(GetFrameKind(control))
            {
            case ShdlcFrameKind.Information:
                return $"I   N(S)={GetSendSequence(control)} N(R)={GetReceiveSequence(control)}{extra}";
            case ShdlcFrameKind.Supervisory:
                return $"{GetSupervisoryType(control)} N(R)={GetReceiveSequence(control)}{extra}";
            default:
                return $"{GetModifier(control)}{extra}";
            }
        }
    }
}
