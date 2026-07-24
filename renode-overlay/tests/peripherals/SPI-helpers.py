import os
import socket
import time


def _connect(port, timeout):
    deadline = time.time() + timeout
    last_err = None
    while time.time() < deadline:
        try:
            return socket.create_connection(("127.0.0.1", int(port)), timeout=timeout)
        except OSError as e:
            last_err = e
            time.sleep(0.1)
    raise AssertionError("Could not connect to the SPI bridge on port %s: %s" % (port, last_err))


def _recv_n(sock, want, timeout):
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
    return data


def transfer_over_spi_bridge(port, hex_data, expected_len=None, timeout=3.0):
    """Connect to the SPI TCP bridge, clock out raw bytes (given as a hex string), and return the
    bytes received back (the MISO bytes, or the interrupt payload in forward-on-interrupt mode)."""
    payload = bytes.fromhex(hex_data)
    want = int(expected_len) if expected_len is not None else len(payload)
    sock = _connect(port, float(timeout))
    try:
        sock.sendall(payload)
        return _recv_n(sock, want, float(timeout)).hex()
    finally:
        sock.close()


def random_hex(n):
    """Return n random bytes encoded as a hex string."""
    return os.urandom(int(n)).hex()


def bridge_sequential_echo(port, count, size, timeout=10.0):
    """Open a single connection and perform `count` echo exchanges of `size` random bytes each,
    checking every response byte-for-byte. Returns the number of exchanges that matched."""
    count = int(count)
    size = int(size)
    timeout = float(timeout)
    sock = _connect(port, timeout)
    matched = 0
    try:
        for _ in range(count):
            payload = os.urandom(size)
            sock.sendall(payload)
            data = _recv_n(sock, size, timeout)
            if data == payload:
                matched += 1
    finally:
        sock.close()
    return matched


def normalize_pretty_hex(pretty):
    """Convert Renode's PrettyPrintCollectionHex output (e.g. '[0x1, 0xAB]') to a plain hex string."""
    pretty = pretty.strip()
    if pretty in ("[]", ""):
        return ""
    inner = pretty[pretty.index("[") + 1:pretty.rindex("]")]
    parts = [p.strip() for p in inner.split(",") if p.strip()]
    return "".join("%02x" % int(p, 16) for p in parts)
