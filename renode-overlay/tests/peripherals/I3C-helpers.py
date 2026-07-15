import socket
import time


def transfer_over_i3c_bridge(port, hex_data, expected_len=None, timeout=3.0):
    """Connect to the I3C TCP bridge, send raw bytes (given as a hex string), and return the
    response bytes as a hex string.

    The bridge transmits the sent bytes to the I3C target as a private write and streams the
    target's read response back, so this keyword exercises both directions of the bridge.
    """
    port = int(port)
    timeout = float(timeout)
    payload = bytes.fromhex(hex_data)
    want = int(expected_len) if expected_len is not None else len(payload)

    deadline = time.time() + timeout
    last_err = None
    sock = None
    while time.time() < deadline:
        try:
            sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
            break
        except OSError as e:
            last_err = e
            time.sleep(0.1)
    if sock is None:
        raise AssertionError("Could not connect to the I3C bridge on port %d: %s" % (port, last_err))

    try:
        sock.sendall(payload)
        sock.settimeout(timeout)
        data = b""
        while len(data) < want:
            try:
                chunk = sock.recv(want - len(data))
            except socket.timeout:
                break
            if not chunk:
                break
            data += chunk
        return data.hex()
    finally:
        sock.close()
