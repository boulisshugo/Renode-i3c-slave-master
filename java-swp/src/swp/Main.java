package swp;

import java.util.Arrays;
import java.util.Random;

/**
 * Reliability harness for the Renode SWP LPDU bridge, with the CLF's protocol layers in this client.
 *
 * It connects to the bridge, runs the ACT activation sequence and the SHDLC RSET/UA handshake itself
 * (see {@link ClfStack}), then exchanges random payloads with the target and checks every answer.
 * Against the firmware UICC in this repository (firmware-swp/main.c) the application reverses the
 * request, which is what {@code -reverse} expects; {@code -echo} is for a target that echoes.
 *
 * Usage: java swp.Main [host] [port] [iterations] [payloadSize] [timeoutMs] [-echo|-reverse] [-v]
 */
public final class Main {

    public static void main(String[] args) throws Exception {
        String host = "127.0.0.1";
        int port = 33672;
        int iterations = 200;
        int payloadSize = 16;
        int timeoutMs = 5000;
        boolean reverse = true;
        boolean verbose = false;

        int positional = 0;
        for (String arg : args) {
            if (arg.equals("-v")) {
                verbose = true;
            } else if (arg.equals("-echo")) {
                reverse = false;
            } else if (arg.equals("-reverse")) {
                reverse = true;
            } else {
                switch (positional++) {
                    case 0: host = arg; break;
                    case 1: port = Integer.parseInt(arg); break;
                    case 2: iterations = Integer.parseInt(arg); break;
                    case 3: payloadSize = Integer.parseInt(arg); break;
                    case 4: timeoutMs = Integer.parseInt(arg); break;
                    default: throw new IllegalArgumentException("unexpected argument: " + arg);
                }
            }
        }

        System.out.printf("Connecting to the Renode SWP LPDU bridge at %s:%d%n", host, port);
        Random random = new Random(0xC0FFEE);
        int ok = 0;
        int fail = 0;
        long totalLatencyNs = 0;
        long maxLatencyNs = 0;

        try (LpduLink link = new LpduLink(host, port)) {
            ClfStack clf = new ClfStack(link).verbose(verbose).fullPower(true).window(4, false);

            long activationStart = System.nanoTime();
            clf.activate(timeoutMs, 3);
            System.out.printf("Activated in %.1f ms: %s, window %d%n",
                    (System.nanoTime() - activationStart) / 1e6,
                    clf.getTargetCapabilities(), clf.getWindowSize());

            for (int i = 0; i < iterations; i++) {
                byte[] payload = new byte[payloadSize];
                random.nextBytes(payload);
                byte[] expected = reverse ? reversed(payload) : payload;

                long start = System.nanoTime();
                byte[] response = clf.send(payload, timeoutMs);
                long elapsed = System.nanoTime() - start;

                totalLatencyNs += elapsed;
                maxLatencyNs = Math.max(maxLatencyNs, elapsed);

                if (Arrays.equals(response, expected)) {
                    ok++;
                } else {
                    fail++;
                    System.err.printf("Mismatch at iteration %d: sent %s expected %s got %s%n",
                            i, SWPProtocol.toHex(payload), SWPProtocol.toHex(expected),
                            SWPProtocol.toHex(response));
                }
            }
        }

        double avgMs = (totalLatencyNs / 1e6) / Math.max(1, iterations);
        double maxMs = maxLatencyNs / 1e6;
        System.out.printf(
                "iterations=%d ok=%d fail=%d reliability=%.2f%% avgLatencyMs=%.3f maxLatencyMs=%.3f%n",
                iterations, ok, fail, 100.0 * ok / Math.max(1, iterations), avgMs, maxMs);

        System.exit(fail == 0 ? 0 : 1);
    }

    private static byte[] reversed(byte[] data) {
        byte[] out = new byte[data.length];
        for (int i = 0; i < data.length; i++) {
            out[i] = data[data.length - 1 - i];
        }
        return out;
    }

    private Main() {
    }
}
