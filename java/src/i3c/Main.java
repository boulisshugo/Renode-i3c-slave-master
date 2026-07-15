package i3c;

import java.io.ByteArrayOutputStream;
import java.util.Arrays;
import java.util.Random;

/**
 * Reliability/consistency harness for the Renode I3C TCP bridge and the firmware-managed slave.
 *
 * It connects to the Renode I3C TCP bridge and, for many iterations, sends a random payload to the
 * Renode I3C master (which forwards it to the firmware-managed I3C slave), then polls for and reads
 * back the slave's response, checking it byte-for-byte. At the end it prints reliability statistics.
 *
 * Usage: java i3c.Main [host] [port] [iterations] [payloadSize] [timeoutMs]
 */
public final class Main {

    public static void main(String[] args) throws Exception {
        String host = args.length > 0 ? args[0] : "127.0.0.1";
        int port = args.length > 1 ? Integer.parseInt(args[1]) : 33570;
        int iterations = args.length > 2 ? Integer.parseInt(args[2]) : 500;
        int payloadSize = args.length > 3 ? Integer.parseInt(args[3]) : 16;
        long timeoutMs = args.length > 4 ? Long.parseLong(args[4]) : 5000;

        Random random = new Random(0xC0FFEE);
        int ok = 0;
        int fail = 0;
        long totalLatencyNs = 0;
        long maxLatencyNs = 0;

        System.out.printf("Connecting to Renode I3C bridge at %s:%d%n", host, port);
        try (I3CBridge bridge = new I3CBridge(host, port)) {
            for (int i = 0; i < iterations; i++) {
                byte[] payload = new byte[payloadSize];
                random.nextBytes(payload);

                long start = System.nanoTime();
                bridge.sendData(payload);
                byte[] response = receiveExactly(bridge, payloadSize, timeoutMs);
                long elapsed = System.nanoTime() - start;

                totalLatencyNs += elapsed;
                maxLatencyNs = Math.max(maxLatencyNs, elapsed);

                if (Arrays.equals(response, payload)) {
                    ok++;
                } else {
                    fail++;
                    System.err.printf("Mismatch at iteration %d: sent %s got %s%n",
                            i, toHex(payload), toHex(response));
                }
            }
        }

        double avgMs = (totalLatencyNs / 1e6) / iterations;
        double maxMs = maxLatencyNs / 1e6;
        System.out.printf(
                "iterations=%d ok=%d fail=%d reliability=%.2f%% avgLatencyMs=%.3f maxLatencyMs=%.3f%n",
                iterations, ok, fail, 100.0 * ok / iterations, avgMs, maxMs);

        System.exit(fail == 0 ? 0 : 1);
    }

    private static byte[] receiveExactly(I3CBridge bridge, int count, long timeoutMs) throws Exception {
        ByteArrayOutputStream accumulator = new ByteArrayOutputStream();
        long deadline = System.currentTimeMillis() + timeoutMs;
        while (accumulator.size() < count && System.currentTimeMillis() < deadline) {
            if (bridge.isDataAvailable()) {
                accumulator.write(bridge.receiveData());
            } else {
                Thread.sleep(1);
            }
        }
        return accumulator.toByteArray();
    }

    private static String toHex(byte[] data) {
        StringBuilder sb = new StringBuilder();
        for (byte b : data) {
            sb.append(String.format("%02x", b));
        }
        return sb.toString();
    }

    private Main() {
    }
}
