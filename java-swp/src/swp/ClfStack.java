package swp;

import java.io.IOException;

/**
 * The CLF's ACT and SHDLC layers, in Java.
 *
 * THIS IS THE POINT OF java-swp/. On a real contactless front-end these layers are host software, so
 * a simulation is only worth something if the software under test is the thing building the frames.
 * Renode's SimpleSWPController, with ProtocolOwner = External, contributes nothing above the wire: it
 * powers S1, adds the SOF/stuffing/CRC/EOF to what this class hands it, and hands back what it
 * receives. Every ACT_POWER_MODE, every RSET, every N(S) and N(R) below is built here.
 *
 * It is the mirror of firmware-swp/main.c at the other end of the link: target LLC in C on the
 * emulated CPU, CLF LLC in Java on the host, and Renode modelling only the wire between them.
 *
 * The sequence {@link #activate} runs (clause 11, then clause 10):
 *
 *   UICC -> CLF   ACT_SYNC + ACT_INFORMATION      (the target talks first, when it is ready)
 *   CLF  -> UICC  ACT_POWER_MODE                  built here
 *   UICC -> CLF   ACT_READY
 *   CLF  -> UICC  RSET (window, SREJ)             built here
 *   UICC -> CLF   UA
 *
 * If no ACT_SYNC is waiting - because the target sent it before this client connected, which is the
 * normal race when Renode powers S1 at startup - it asks for the last ACT frame again with the
 * frame-resend bit, which is the specification's own recovery for a lost ACT frame.
 */
public class ClfStack {

    private final LpduLink link;

    private boolean fullPower = true;
    private int proposedWindow = 4;
    private boolean selectiveReject;

    private int sendSequence;
    private int receiveSequence;
    private boolean linkEstablished;
    private int windowSize;
    private SWPProtocol.ActInformation targetCapabilities;

    private boolean verbose;

    public ClfStack(LpduLink link) {
        this.link = link;
    }

    /** Logs every LPDU in and out, the way the Renode-side frame trace does. */
    public ClfStack verbose(boolean value) {
        this.verbose = value;
        return this;
    }

    /** Power mode to request in ACT_POWER_MODE. Default: full power. */
    public ClfStack fullPower(boolean value) {
        this.fullPower = value;
        return this;
    }

    /** SHDLC window to propose in RSET, and whether to offer selective reject. */
    public ClfStack window(int size, boolean srej) {
        this.proposedWindow = size;
        this.selectiveReject = srej;
        return this;
    }

    public boolean isLinkEstablished() {
        return linkEstablished;
    }

    public int getWindowSize() {
        return windowSize;
    }

    /** What the target advertised in ACT_SYNC, or null if activation has not got that far. */
    public SWPProtocol.ActInformation getTargetCapabilities() {
        return targetCapabilities;
    }

    /**
     * Runs the whole activation sequence and leaves the SHDLC link ready to carry data.
     *
     * @param timeoutMs how long to wait for each answer. Generous is right against a firmware target:
     *                  the answer only exists once the emulated CPU has run.
     * @param retries   how many times to ask for a repeat with FR = 1 before giving up.
     * @throws IOException if the sequence does not complete.
     */
    public void activate(int timeoutMs, int retries) throws IOException {
        reset();

        byte[] sync = awaitActSync(timeoutMs, retries);
        targetCapabilities = SWPProtocol.parseActSync(sync);
        log("<- " + SWPProtocol.describe(sync) + "  (" + targetCapabilities + ")");

        byte[] ready = null;
        for (int attempt = 0; attempt <= retries && ready == null; attempt++) {
            // The first attempt selects the power mode; a later one repeats the request with FR = 1,
            // which asks the target to re-send whatever ACT frame we failed to get.
            send(SWPProtocol.buildActPowerMode(fullPower, attempt > 0));
            byte[] answer = receive(timeoutMs);
            if (answer == null) {
                continue;
            }
            if (SWPProtocol.control(answer) == SWPProtocol.ACT_READY) {
                ready = answer;
            } else if (SWPProtocol.control(answer) == SWPProtocol.ACT_SYNC) {
                // It repeated its ACT_SYNC because we asked; take the capabilities again and retry
                // the power-mode selection cleanly.
                targetCapabilities = SWPProtocol.parseActSync(answer);
            }
        }
        if (ready == null) {
            throw new IOException("activation failed: no ACT_READY from the target");
        }

        send(SWPProtocol.buildReset(proposedWindow, selectiveReject));
        byte[] ua = receive(timeoutMs);
        if (ua == null || !SWPProtocol.isUnnumbered(ua) || SWPProtocol.modifier(ua) != SWPProtocol.U_UA) {
            throw new IOException("activation failed: the RSET was not acknowledged with a UA");
        }
        windowSize = ua.length > 1 ? Math.min(ua[1] & 0xFF, proposedWindow) : proposedWindow;
        if (windowSize < 1) {
            windowSize = 1;
        }
        sendSequence = 0;
        receiveSequence = 0;
        linkEstablished = true;
        log("SHDLC link established (window " + windowSize + ")");
    }

    /**
     * Sends one application payload in an I-frame and returns what the target answered with.
     *
     * @return the target's payload, or an empty array when it only acknowledged (a bare RR) or said
     *         nothing in time.
     */
    public byte[] send(byte[] payload, int timeoutMs) throws IOException {
        if (!linkEstablished) {
            throw new IllegalStateException("the SHDLC link is not established - call activate() first");
        }
        int maximum = targetCapabilities != null ? targetCapabilities.maxFramePayloadSize : Integer.MAX_VALUE;
        if (payload.length > maximum) {
            throw new IllegalArgumentException("payload of " + payload.length
                    + " bytes exceeds the " + maximum + "-byte maximum the target advertised");
        }

        int sequence = sendSequence;
        sendSequence = (sequence + 1) % SWPProtocol.SEQUENCE_MODULO;
        send(SWPProtocol.buildInformation(sequence, receiveSequence, payload));

        byte[] answer = receive(timeoutMs);
        if (answer == null) {
            return new byte[0];
        }
        if (SWPProtocol.isSupervisory(answer)
                && SWPProtocol.supervisoryType(answer) == SWPProtocol.S_REJ) {
            // The target wants a retransmission starting at its N(R). Resynchronise to it rather
            // than reusing the N(S) it just refused, then send the frame again.
            int wanted = SWPProtocol.receiveSequence(answer);
            sendSequence = (wanted + 1) % SWPProtocol.SEQUENCE_MODULO;
            log("REJ received, retransmitting with N(S)=" + wanted);
            send(SWPProtocol.buildInformation(wanted, receiveSequence, payload));
            answer = receive(timeoutMs);
            if (answer == null) {
                return new byte[0];
            }
        }
        return accept(answer);
    }

    /** Sends a bare RR: acknowledges what we have, and gives the target a slot to talk in. */
    public byte[] poll(int timeoutMs) throws IOException {
        send(SWPProtocol.buildSupervisory(SWPProtocol.S_RR, receiveSequence));
        byte[] answer = receive(timeoutMs);
        return answer == null ? new byte[0] : accept(answer);
    }

    /** Takes one LPDU the target sent and returns its application payload, if it carries one. */
    private byte[] accept(byte[] lpdu) {
        if (!SWPProtocol.isInformation(lpdu)) {
            return new byte[0];
        }
        if (SWPProtocol.sendSequence(lpdu) != receiveSequence) {
            log("out-of-sequence I-frame N(S)=" + SWPProtocol.sendSequence(lpdu)
                    + ", expected " + receiveSequence);
            return new byte[0];
        }
        receiveSequence = (receiveSequence + 1) % SWPProtocol.SEQUENCE_MODULO;
        byte[] payload = new byte[lpdu.length - 1];
        System.arraycopy(lpdu, 1, payload, 0, payload.length);
        return payload;
    }

    // Waits for the target's opening ACT_SYNC. It may already have been sent - Renode powers S1 when
    // it is told to, which can be before this client connects - so after the first silence we ask for
    // the last ACT frame again with FR = 1 instead of assuming the target is dead.
    private byte[] awaitActSync(int timeoutMs, int retries) throws IOException {
        for (int attempt = 0; attempt <= retries; attempt++) {
            if (attempt > 0) {
                log("no ACT_SYNC yet; asking for the last ACT frame again (FR = 1)");
                send(SWPProtocol.buildActPowerMode(fullPower, true));
            }
            byte[] lpdu = receive(timeoutMs);
            if (lpdu == null) {
                continue;
            }
            if (SWPProtocol.control(lpdu) == SWPProtocol.ACT_SYNC) {
                return lpdu;
            }
            log("ignoring " + SWPProtocol.describe(lpdu) + " while waiting for ACT_SYNC");
        }
        throw new IOException("activation failed: no ACT_SYNC from the target");
    }

    private void reset() {
        sendSequence = 0;
        receiveSequence = 0;
        linkEstablished = false;
        windowSize = proposedWindow;
        targetCapabilities = null;
    }

    private void send(byte[] lpdu) throws IOException {
        log("-> " + SWPProtocol.describe(lpdu));
        link.send(lpdu);
    }

    private byte[] receive(int timeoutMs) throws IOException {
        byte[] lpdu = link.receive(timeoutMs);
        if (lpdu != null) {
            log("<- " + SWPProtocol.describe(lpdu));
        }
        return lpdu;
    }

    private void log(String message) {
        if (verbose) {
            System.out.println("[clf] " + message);
        }
    }
}
