package swp;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;

/**
 * The transport under the CLF's LLC layers: a socket to Renode's SWP LPDU bridge
 * ({@code emulation CreateSWPLpduBridge}), carrying whole LPDUs in both directions.
 *
 * Each LPDU travels as a 2-byte big-endian length followed by that many bytes. The prefix is not part
 * of SWP - on the wire an LPDU is delimited by the frame's SOF and EOF, which the Renode model puts on
 * and takes off. It is here because TCP has no record boundaries and an LPDU boundary matters: the
 * control field is the first byte of one, so a client that let two LPDUs run together would read a
 * sequence number as a payload byte.
 *
 * Reads block up to a timeout, which is what lets {@link ClfStack} wait for a target whose answer is
 * built by firmware and therefore arrives only once the emulated CPU has run.
 */
public class LpduLink implements AutoCloseable {

    /** Refuses anything larger, matching the bridge's own cap. */
    public static final int MAX_LPDU = 8192;

    private final Socket socket;
    private final DataInputStream in;
    private final OutputStream out;

    public LpduLink(String host, int port) throws IOException {
        this(host, port, 5000);
    }

    public LpduLink(String host, int port, int connectTimeoutMs) throws IOException {
        socket = new Socket();
        socket.connect(new InetSocketAddress(host, port), connectTimeoutMs);
        socket.setTcpNoDelay(true);
        in = new DataInputStream(socket.getInputStream());
        out = socket.getOutputStream();
    }

    /** Sends one complete LPDU - control field first. The Renode model adds the frame and the CRC. */
    public void send(byte[] lpdu) throws IOException {
        if (lpdu == null || lpdu.length == 0) {
            throw new IllegalArgumentException("an LPDU must carry at least a control field");
        }
        if (lpdu.length > MAX_LPDU) {
            throw new IllegalArgumentException("LPDU of " + lpdu.length + " bytes exceeds " + MAX_LPDU);
        }
        byte[] framed = new byte[lpdu.length + 2];
        framed[0] = (byte) (lpdu.length >> 8);
        framed[1] = (byte) lpdu.length;
        System.arraycopy(lpdu, 0, framed, 2, lpdu.length);
        out.write(framed);
        out.flush();
    }

    /**
     * Reads one complete LPDU, waiting up to timeoutMs for it.
     *
     * @return the LPDU, or null if nothing arrived in time - which is a normal answer here, not an
     *         error: a target whose protocol is firmware may simply not have got there yet.
     */
    public byte[] receive(int timeoutMs) throws IOException {
        socket.setSoTimeout(Math.max(1, timeoutMs));
        int length;
        try {
            length = in.readUnsignedShort();
        } catch (SocketTimeoutException e) {
            return null;
        }
        if (length <= 0 || length > MAX_LPDU) {
            throw new IOException("bridge announced an implausible LPDU length: " + length);
        }
        byte[] lpdu = new byte[length];
        // The length prefix is already consumed, so the body must arrive: read it without a partial
        // timeout, or the stream would be left mid-LPDU and every later read would be misaligned.
        in.readFully(lpdu);
        return lpdu;
    }

    @Override
    public void close() throws IOException {
        socket.close();
    }
}
