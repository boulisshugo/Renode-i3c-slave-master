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
    raise AssertionError("Could not connect to the SWP bridge on port %s: %s" % (port, last_err))


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


def transfer_over_swp_bridge(port, hex_data, expected_len=None, timeout=3.0):
    """Connect to the SWP TCP bridge, send raw LLC payload bytes (given as a hex string), and return
    the payload the UICC answered with. The SWP framing, CRC and SHDLC control byte are added and
    removed inside the emulation - the client only ever sees application bytes."""
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


def swp_crc(hex_payload):
    """Independent reference CRC-16 (X^16 + X^12 + X^5 + 1, init 0xFFFF, MSB first) over a
    hex-encoded payload, used to cross-check the C# codec inside Renode."""
    crc = 0xFFFF
    for b in bytes.fromhex(hex_payload):
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return "0x%04X" % crc


def bridge_sequential_reverse(port, count, size, timeout=10.0):
    """Like bridge_sequential_echo, but for a UICC whose application reverses the request - which is
    what firmware-swp/main.c does. A plain loopback would pass an echo check without the firmware
    ever having looked at the bytes; reversing them proves it did. Returns the number of matches."""
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
            if data == payload[::-1]:
                matched += 1
    finally:
        sock.close()
    return matched
