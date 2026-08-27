package swp;

/**
 * Checks the LPDU encodings this client builds against the golden values the C# side asserts, so a
 * divergence between SWPProtocol.java and SWPProtocol.cs shows up here rather than as a mysterious
 * REJ inside a simulation. Runs in milliseconds and needs neither Renode nor a socket.
 *
 * Usage: java swp.SelfTest
 */
public final class SelfTest {

    private static int failures;

    public static void main(String[] args) {
        // ACT LLC.
        check("ACT_POWER_MODE full power", "0201",
                SWPProtocol.buildActPowerMode(true, false));
        check("ACT_POWER_MODE low power", "0200",
                SWPProtocol.buildActPowerMode(false, false));
        check("ACT_POWER_MODE with the frame-resend bit", "0203",
                SWPProtocol.buildActPowerMode(true, true));

        // The ACT_SYNC a default UICC sends - the same vector the C# self-test frames.
        SWPProtocol.ActInformation info = SWPProtocol.parseActSync(SWPProtocol.fromHex("010105100003"));
        check("ACT_SYNC advertises version 1", info.version == 1);
        check("  SHDLC and ACT", info.supportedLlcs == 0x05);
        check("  a 4096-byte maximum frame payload", info.maxFramePayloadSize == 4096);
        check("  both power modes", info.powerModes == 0x03);

        // The 256-byte maximum firmware-swp/main.c advertises.
        check("a firmware ACT_SYNC advertises 256 bytes",
                SWPProtocol.parseActSync(SWPProtocol.fromHex("010105010003")).maxFramePayloadSize == 256);

        // SHDLC control bytes.
        check("RSET with window 4, no SREJ", "f90400", SWPProtocol.buildReset(4, false));
        check("RR N(R)=1 is one byte", "c1", SWPProtocol.buildSupervisory(SWPProtocol.S_RR, 1));
        check("REJ N(R)=3", "cb", SWPProtocol.buildSupervisory(SWPProtocol.S_REJ, 3));
        check("I N(S)=0 N(R)=0 +2B", "80dead",
                SWPProtocol.buildInformation(0, 0, SWPProtocol.fromHex("dead")));
        check("I N(S)=1 N(R)=1", "89", SWPProtocol.buildInformation(1, 1, new byte[0]));
        check("N(S) and N(R) wrap modulo 8", "b8", SWPProtocol.buildInformation(15, 8, new byte[0]));

        // Classification: the ACT opcodes sit below the SHDLC range, so no state is needed.
        check("ACT_SYNC classifies as ACT", SWPProtocol.isAct(SWPProtocol.fromHex("01")));
        check("an I-frame classifies as information",
                SWPProtocol.isInformation(SWPProtocol.fromHex("80")));
        check("the A0 head is information too",
                SWPProtocol.isInformation(SWPProtocol.fromHex("a0")));
        check("an S-frame classifies as supervisory",
                SWPProtocol.isSupervisory(SWPProtocol.fromHex("c0")));
        check("a U-frame classifies as unnumbered", SWPProtocol.isUnnumbered(SWPProtocol.fromHex("e6")));
        check("UA is recognised by its modifier",
                SWPProtocol.modifier(SWPProtocol.fromHex("e6")) == SWPProtocol.U_UA);
        check("RSET is recognised by its modifier",
                SWPProtocol.modifier(SWPProtocol.fromHex("f9")) == SWPProtocol.U_RSET);

        // Sequence extraction round-trips against the builder.
        boolean sequencesMatch = true;
        for (int ns = 0; ns < 8 && sequencesMatch; ns++) {
            for (int nr = 0; nr < 8 && sequencesMatch; nr++) {
                byte[] lpdu = SWPProtocol.buildInformation(ns, nr, new byte[0]);
                sequencesMatch = SWPProtocol.sendSequence(lpdu) == ns
                        && SWPProtocol.receiveSequence(lpdu) == nr;
            }
        }
        check("all 64 N(S)/N(R) combinations round-trip", sequencesMatch);

        // The descriptions the trace prints, matching the C# Describe output.
        check("describes an I-frame like the model does",
                "I   N(S)=0 N(R)=1 +2B".equals(SWPProtocol.describe(SWPProtocol.fromHex("81dead"))));
        check("describes ACT_POWER_MODE like the model does",
                "ACT_POWER_MODE full power".equals(SWPProtocol.describe(SWPProtocol.fromHex("0201"))));

        System.out.println();
        System.out.println(failures == 0 ? "ALL JAVA LPDU CHECKS PASS" : failures + " FAILURE(S)");
        System.exit(failures == 0 ? 0 : 1);
    }

    private static void check(String what, String expectedHex, byte[] actual) {
        check(what + " -> " + expectedHex, expectedHex.equals(SWPProtocol.toHex(actual)),
                SWPProtocol.toHex(actual));
    }

    private static void check(String what, boolean ok) {
        check(what, ok, null);
    }

    private static void check(String what, boolean ok, String detail) {
        if (!ok) {
            failures++;
        }
        System.out.println((ok ? "  ok   " : "  FAIL ") + what
                + (ok || detail == null ? "" : "  (got " + detail + ")"));
    }

    private SelfTest() {
    }
}
