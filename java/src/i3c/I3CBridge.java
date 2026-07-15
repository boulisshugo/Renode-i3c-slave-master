package i3c;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;

/**
 * A minimal Java client for the Renode I3C TCP bridge.
 *
 * It speaks the bridge's raw byte protocol: bytes written with {@link #sendData} are transmitted by
 * the Renode I3C master to the I3C slave; bytes the slave sends back arrive on the same socket and are
 * retrieved with {@link #receiveData} once {@link #isDataAvailable} reports them.
 */
public class I3CBridge implements AutoCloseable {

    private final Socket socket;
    private final InputStream in;
    private final OutputStream out;

    public I3CBridge(String host, int port) throws IOException {
        this(host, port, 5000);
    }

    public I3CBridge(String host, int port, int connectTimeoutMs) throws IOException {
        socket = new Socket();
        socket.connect(new InetSocketAddress(host, port), connectTimeoutMs);
        socket.setTcpNoDelay(true);
        in = socket.getInputStream();
        out = socket.getOutputStream();
    }

    /** Transmits raw bytes to the I3C slave through the Renode master. */
    public void sendData(byte[] data) throws IOException {
        out.write(data);
        out.flush();
    }

    /** Returns true if the slave has sent bytes that are ready to be read (non-blocking poll). */
    public boolean isDataAvailable() throws IOException {
        return in.available() > 0;
    }

    /** Reads whatever bytes the slave has currently sent back (empty array if none are ready). */
    public byte[] receiveData() throws IOException {
        int available = in.available();
        if (available <= 0) {
            return new byte[0];
        }
        byte[] buffer = new byte[available];
        int read = in.read(buffer, 0, available);
        if (read <= 0) {
            return new byte[0];
        }
        if (read == buffer.length) {
            return buffer;
        }
        byte[] trimmed = new byte[read];
        System.arraycopy(buffer, 0, trimmed, 0, read);
        return trimmed;
    }

    @Override
    public void close() throws IOException {
        socket.close();
    }
}
