#!/usr/bin/env python3
"""A UICC that speaks the SWP LPDU bridge protocol, for testing the Java CLF client without Renode.

It stands in for the far end of `emulation CreateSWPLpduBridge`: length-prefixed LPDUs on a socket,
with the target's ACT and SHDLC layers implemented here, independently of the C# and the C. That
independence is the point - it cross-checks the Java client's sequencing against a third
implementation rather than against itself. The full stack (framing, CRC, Renode) is what
run-integration.sh exercises; this is the fast loop.

The application layer reverses the request, matching firmware-swp/main.c.

Usage: fake-uicc.py <port> [--eager] [--max-frame N] [--verbose]
  --eager   send ACT_SYNC as soon as the client connects. Without it the ACT_SYNC is treated as
            already lost (which is what happens when Renode powers S1 before the client connects),
            so the client has to recover with the frame-resend bit.
"""
import socket
import struct
import sys

ACT_SYNC, ACT_POWER_MODE, ACT_READY = 0x01, 0x02, 0x03
ACT_PM_FULL_POWER, ACT_PM_FRAME_RESEND = 0x01, 0x02
HEAD_MASK, HEAD_I, HEAD_I2, HEAD_S, HEAD_U = 0xE0, 0x80, 0xA0, 0xC0, 0xE0
S_RR, S_REJ = 0x00, 0x01
U_UA, U_RSET = 0x06, 0x19


class FakeUicc:
    def __init__(self, conn, max_frame=256, eager=False, verbose=False):
        self.conn = conn
        self.max_frame = max_frame
        self.verbose = verbose
        self.send_seq = 0
        self.recv_seq = 0
        self.link_up = False
        self.last_act = None
        self.full_power = False
        self.act_sync = bytes([ACT_SYNC, 1, 0x05, max_frame >> 8, max_frame & 0xFF, 0x03])
        if eager:
            self.send(self.act_sync, act=True)
        else:
            # S1 came up and ACT_SYNC went out before anyone was listening: remember it as the last
            # ACT frame so a frame-resend request can recover it, exactly as the real target does.
            self.last_act = self.act_sync

    def log(self, message):
        if self.verbose:
            print("[uicc] " + message, flush=True)

    def send(self, lpdu, act=False):
        if act:
            self.last_act = lpdu
        self.log("-> " + lpdu.hex())
        self.conn.sendall(struct.pack(">H", len(lpdu)) + lpdu)

    def receive(self):
        header = self.read_exactly(2)
        if header is None:
            return None
        (length,) = struct.unpack(">H", header)
        lpdu = self.read_exactly(length)
        if lpdu is not None:
            self.log("<- " + lpdu.hex())
        return lpdu

    def read_exactly(self, count):
        data = b""
        while len(data) < count:
            chunk = self.conn.recv(count - len(data))
            if not chunk:
                return None
            data += chunk
        return data

    def run(self):
        while True:
            lpdu = self.receive()
            if lpdu is None:
                return
            if lpdu[0] < HEAD_I:
                self.handle_act(lpdu)
            else:
                self.handle_shdlc(lpdu)

    def handle_act(self, lpdu):
        if lpdu[0] != ACT_POWER_MODE:
            return
        parameter = lpdu[1] if len(lpdu) > 1 else 0
        if parameter & ACT_PM_FRAME_RESEND:
            if self.last_act is not None:
                self.send(self.last_act)
            return
        self.full_power = bool(parameter & ACT_PM_FULL_POWER)
        self.send(bytes([ACT_READY]), act=True)

    def handle_shdlc(self, lpdu):
        control = lpdu[0]
        head = control & HEAD_MASK
        if head == HEAD_U:
            if control & 0x1F != U_RSET:
                return
            window = lpdu[1] if len(lpdu) > 1 else 4
            self.send_seq = 0
            self.recv_seq = 0
            self.link_up = True
            self.send(bytes([HEAD_U | U_UA, min(window, 4) or 1, 0]))
            return
        if head == HEAD_S:
            return
        if head not in (HEAD_I, HEAD_I2) or not self.link_up:
            return
        if ((control >> 3) & 0x07) != self.recv_seq:
            self.send(bytes([HEAD_S | (S_REJ << 3) | self.recv_seq]))
            return
        self.recv_seq = (self.recv_seq + 1) % 8
        response = bytes(reversed(lpdu[1:]))
        if not response:
            self.send(bytes([HEAD_S | (S_RR << 3) | self.recv_seq]))
            return
        self.send(bytes([HEAD_I | (self.send_seq << 3) | self.recv_seq]) + response)
        self.send_seq = (self.send_seq + 1) % 8


def main():
    port = int(sys.argv[1])
    eager = "--eager" in sys.argv
    verbose = "--verbose" in sys.argv
    max_frame = 256
    if "--max-frame" in sys.argv:
        max_frame = int(sys.argv[sys.argv.index("--max-frame") + 1])

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", port))
    server.listen(4)
    print("fake UICC listening on %d (%s)" % (port, "eager" if eager else "ACT_SYNC already missed"),
          flush=True)
    # Serve clients one at a time until killed, rather than exiting after the first. A caller waiting
    # for the port to open usually probes it with a connection of its own, and that probe must not
    # consume the accept the real client needs.
    try:
        while True:
            conn, _ = server.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            try:
                FakeUicc(conn, max_frame=max_frame, eager=eager, verbose=verbose).run()
            except (ConnectionResetError, BrokenPipeError):
                pass
            finally:
                conn.close()
    finally:
        server.close()


if __name__ == "__main__":
    main()
