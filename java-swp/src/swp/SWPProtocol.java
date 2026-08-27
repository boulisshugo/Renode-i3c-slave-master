package swp;

/**
 * The SWP LLC encodings, CLF side (ETSI TS 102 613 clauses 10 and 11).
 *
 * This is the Java twin of SWPProtocol.cs, and it exists for the same reason firmware-swp/main.c
 * exists on the target side: the ACT and SHDLC layers are software, so they belong in the software
 * that owns them, not in the model of the wire. Renode's SimpleSWPController, when its ProtocolOwner
 * is External, adds nothing to what this class builds except the frame delimiters and the CRC.
 *
 * Everything here operates on an LPDU: the LLC payload of an SWP frame, control field first.
 *
 * Fidelity note, carried over from the C# side: the SHDLC control-byte encoding is the ETSI one as
 * implemented by shipping stacks (the Linux kernel's net/nfc/hci/llc_shdlc.c). The numeric ACT
 * opcodes and the ACT_INFORMATION layout are this repository's profile - change them here and in
 * SWPProtocol.cs together.
 */
public final class SWPProtocol {

    /* ---- ACT LLC (clause 11) ---- */

    public static final int ACT_SYNC = 0x01;
    public static final int ACT_POWER_MODE = 0x02;
    public static final int ACT_READY = 0x03;

    /** ACT_POWER_MODE parameter byte: 0 = low power, 1 = full power. */
    public static final int ACT_PM_FULL_POWER = 0x01;
    /** ACT_POWER_MODE parameter byte: FR, "repeat your last ACT frame". */
    public static final int ACT_PM_FRAME_RESEND = 0x02;

    /* ---- SHDLC LLC (clause 10) ---- */

    public static final int HEAD_MASK = 0xE0;
    public static final int HEAD_I = 0x80;
    public static final int HEAD_I2 = 0xA0;
    public static final int HEAD_S = 0xC0;
    public static final int HEAD_U = 0xE0;

    public static final int S_RR = 0x00;
    public static final int S_REJ = 0x01;
    public static final int S_RNR = 0x02;
    public static final int S_SREJ = 0x03;

    public static final int U_UA = 0x06;
    public static final int U_RSET = 0x19;

    /** Sequence numbers are modulo 8. */
    public static final int SEQUENCE_MODULO = 8;

    public static int control(byte[] lpdu) {
        return lpdu[0] & 0xFF;
    }

    public static boolean isInformation(byte[] lpdu) {
        int head = control(lpdu) & HEAD_MASK;
        return head == HEAD_I || head == HEAD_I2;
    }

    public static boolean isSupervisory(byte[] lpdu) {
        return (control(lpdu) & HEAD_MASK) == HEAD_S;
    }

    public static boolean isUnnumbered(byte[] lpdu) {
        return (control(lpdu) & HEAD_MASK) == HEAD_U;
    }

    /** True for an ACT LPDU: the ACT opcodes sit below the SHDLC range, so no state is needed. */
    public static boolean isAct(byte[] lpdu) {
        return control(lpdu) < HEAD_I;
    }

    public static int sendSequence(byte[] lpdu) {
        return (control(lpdu) >> 3) & 0x07;
    }

    public static int receiveSequence(byte[] lpdu) {
        return control(lpdu) & 0x07;
    }

    public static int supervisoryType(byte[] lpdu) {
        return (control(lpdu) >> 3) & 0x03;
    }

    public static int modifier(byte[] lpdu) {
        return control(lpdu) & 0x1F;
    }

    /** ACT_POWER_MODE: selects the power mode, or asks for the last ACT frame again (FR). */
    public static byte[] buildActPowerMode(boolean fullPower, boolean frameResend) {
        int parameter = (fullPower ? ACT_PM_FULL_POWER : 0) | (frameResend ? ACT_PM_FRAME_RESEND : 0);
        return new byte[] { (byte) ACT_POWER_MODE, (byte) parameter };
    }

    /** An I-frame carrying N(S), N(R) and the application payload. */
    public static byte[] buildInformation(int sendSequence, int receiveSequence, byte[] payload) {
        byte[] body = payload == null ? new byte[0] : payload;
        byte[] lpdu = new byte[body.length + 1];
        lpdu[0] = (byte) (HEAD_I
                | ((sendSequence % SEQUENCE_MODULO) << 3)
                | (receiveSequence % SEQUENCE_MODULO));
        System.arraycopy(body, 0, lpdu, 1, body.length);
        return lpdu;
    }

    /** An S-frame (RR / REJ / RNR / SREJ) acknowledging up to receiveSequence. */
    public static byte[] buildSupervisory(int type, int receiveSequence) {
        return new byte[] { (byte) (HEAD_S | (type << 3) | (receiveSequence % SEQUENCE_MODULO)) };
    }

    /** RSET, proposing a window size and whether we support selective reject. */
    public static byte[] buildReset(int windowSize, boolean selectiveReject) {
        return new byte[] { (byte) (HEAD_U | U_RSET), (byte) windowSize, (byte) (selectiveReject ? 1 : 0) };
    }

    /** What ACT_SYNC advertises: version, supported LLCs, maximum frame payload, power modes. */
    public static final class ActInformation {
        public final int version;
        public final int supportedLlcs;
        public final int maxFramePayloadSize;
        public final int powerModes;

        ActInformation(int version, int supportedLlcs, int maxFramePayloadSize, int powerModes) {
            this.version = version;
            this.supportedLlcs = supportedLlcs;
            this.maxFramePayloadSize = maxFramePayloadSize;
            this.powerModes = powerModes;
        }

        @Override
        public String toString() {
            return String.format("version=%d llcs=0x%02X maxFrame=%d powerModes=0x%02X",
                    version, supportedLlcs, maxFramePayloadSize, powerModes);
        }
    }

    /** Parses the ACT_INFORMATION out of an ACT_SYNC LPDU. Missing fields keep sane defaults. */
    public static ActInformation parseActSync(byte[] lpdu) {
        int version = lpdu.length > 1 ? lpdu[1] & 0xFF : 0;
        int llcs = lpdu.length > 2 ? lpdu[2] & 0xFF : 0;
        int maxFrame = lpdu.length > 4 ? ((lpdu[3] & 0xFF) << 8) | (lpdu[4] & 0xFF) : 0;
        int powerModes = lpdu.length > 5 ? lpdu[5] & 0xFF : 0;
        return new ActInformation(version, llcs, maxFrame <= 0 ? 4096 : maxFrame, powerModes);
    }

    /** Names an LPDU for a log line: "ACT_SYNC", "I N(S)=0 N(R)=1 +2B", "RR N(R)=2". */
    public static String describe(byte[] lpdu) {
        if (lpdu == null || lpdu.length == 0) {
            return "(empty)";
        }
        String extra = lpdu.length > 1 ? " +" + (lpdu.length - 1) + "B" : "";
        switch (control(lpdu)) {
            case ACT_SYNC:
                return "ACT_SYNC" + extra;
            case ACT_READY:
                return "ACT_READY" + extra;
            case ACT_POWER_MODE: {
                int parameter = lpdu.length > 1 ? lpdu[1] & 0xFF : 0;
                return "ACT_POWER_MODE " + (((parameter & ACT_PM_FULL_POWER) != 0) ? "full power" : "low power")
                        + (((parameter & ACT_PM_FRAME_RESEND) != 0) ? " FR=1" : "");
            }
            default:
                break;
        }
        if (isAct(lpdu)) {
            return String.format("unknown control 0x%02X%s", control(lpdu), extra);
        }
        if (isInformation(lpdu)) {
            return String.format("I   N(S)=%d N(R)=%d%s", sendSequence(lpdu), receiveSequence(lpdu), extra);
        }
        if (isSupervisory(lpdu)) {
            String[] names = { "ReceiveReady", "Reject", "ReceiveNotReady", "SelectiveReject" };
            return String.format("%s N(R)=%d%s", names[supervisoryType(lpdu)], receiveSequence(lpdu), extra);
        }
        int mod = modifier(lpdu);
        return (mod == U_UA ? "UnnumberedAcknowledgement" : mod == U_RSET ? "Reset" : "U 0x" + Integer.toHexString(mod))
                + extra;
    }

    public static String toHex(byte[] data) {
        StringBuilder sb = new StringBuilder();
        for (byte b : data) {
            sb.append(String.format("%02x", b));
        }
        return sb.toString();
    }

    public static byte[] fromHex(String hex) {
        byte[] out = new byte[hex.length() / 2];
        for (int i = 0; i < out.length; i++) {
            out[i] = (byte) Integer.parseInt(hex.substring(i * 2, i * 2 + 2), 16);
        }
        return out;
    }

    private SWPProtocol() {
    }
}
