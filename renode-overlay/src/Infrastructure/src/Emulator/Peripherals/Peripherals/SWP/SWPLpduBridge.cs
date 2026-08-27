//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Migrant;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    public static class SWPLpduBridgeExtensions
    {
        // Creates a TCP bridge that puts the CLF's ACT and SHDLC layers in an external client.
        //
        // Monitor usage:
        //   emulation CreateSWPLpduBridge sysbus.swp 0 3457
        //   swp PowerUp 0
        //   start
        //
        // WHAT MAKES THIS DIFFERENT FROM CreateSWPTCPBridge
        //
        // SWPTCPBridge is an APPLICATION channel: the client sends the bytes inside an I-frame, and
        // SimpleSWPController builds the ACT sequence, the RSET/UA handshake and the sequence numbers
        // around them. This bridge is an LPDU channel: the client sends whole LPDUs - the LLC payload,
        // control field first - and the controller adds nothing but the frame. ACT_SYNC arrives at the
        // client; the client decides to answer ACT_POWER_MODE; the client sends RSET and reads the UA;
        // the client owns N(S) and N(R).
        //
        // It is the same split this repository applies to the target, applied to the other end of the
        // wire. On the target side the ACT/SHDLC layers are firmware, so InventedSWPTarget hands raw
        // LPDUs to the CPU. On the CLF side they are host software, so this hands raw LPDUs to a
        // socket. Between the two, the only thing either model does is the wire.
        //
        // Creating this bridge sets the controller's ProtocolOwner to External - two owners answering
        // the same ACT_SYNC would be worse than either.
        //
        // FRAMING ON THE SOCKET
        //
        // Unlike the raw application bridge, this one is LENGTH-PREFIXED: each LPDU travels as a
        // 2-byte big-endian length followed by that many bytes, in both directions. It has to be. TCP
        // is a byte stream with no record boundaries, and an LPDU boundary is load-bearing here - the
        // control field is the first byte of one, so a client that merged two LPDUs would read a
        // sequence number as a payload byte. The SWP frame's own delimiters cannot do the job either:
        // they are bit-stuffed and bit-packed, and undoing that on the client would be putting the
        // data link layer back in the client's lap, which is exactly what this bridge exists to avoid.
        //
        // Determinism: like the application bridge, every access is marshalled onto the emulation's
        // time-domain thread, so the CLF drives the target in the same simulation time as the CPU and
        // a run is reproducible regardless of host socket timing - which is why the emulation must be
        // running for an exchange to execute.
        public static void CreateSWPLpduBridge(this Emulation emulation, SimpleSWPController controller,
            int iface, int port, string name = "swpLpduBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(new SWPLpduBridge(controller, iface, port), name);
        }
    }

    // Bridges one SWP interface of a SimpleSWPController to a TCP client that owns the CLF's ACT and
    // SHDLC layers. See CreateSWPLpduBridge for the protocol split and the socket framing.
    [Transient]
    public class SWPLpduBridge : IExternal, IDisposable
    {
        public SWPLpduBridge(SimpleSWPController controller, int iface, int port)
        {
            this.controller = controller;
            this.iface = iface;
            machine = controller.GetMachine();

            if(controller.ProtocolOwner != SWPProtocolOwner.External)
            {
                // Not a silent fix-up: the controller answering ACT_SYNC while the client also
                // answers it would put two ACT_POWER_MODEs on the wire and desynchronise the target.
                this.Log(LogLevel.Info,
                    "Setting the controller's ProtocolOwner to External: this bridge's client owns ACT and SHDLC");
                controller.ProtocolOwner = SWPProtocolOwner.External;
            }

            server = new SocketServerProvider(telnetMode: false, serverName: "SWPLpduBridge");
            server.BufferSize = 4096;
            server.ConnectionAccepted += _ => this.Log(LogLevel.Info,
                "TCP client connected on the SWP LPDU bridge for interface {0}", iface);
            server.ConnectionClosed += HandleConnectionClosed;
            server.DataBlockReceived += HandleDataReceived;

            controller.LpduReceived += HandleLpduReceived;

            server.Start(port);
            this.Log(LogLevel.Info,
                "SWP LPDU bridge for interface {0} listening on port {1} (the client owns ACT and SHDLC)",
                iface, port);
        }

        public void Dispose()
        {
            server.DataBlockReceived -= HandleDataReceived;
            server.ConnectionClosed -= HandleConnectionClosed;
            controller.LpduReceived -= HandleLpduReceived;
            server.Stop();
        }

        // Number of complete LPDUs forwarded to and from the client since the bridge was created -
        // monitor and robot readable, and the quickest check that the client is actually talking.
        public int LpdusFromClient { get; private set; }
        public int LpdusToClient { get; private set; }

        // Called on the host socket thread. Accumulates the length-prefixed stream and hands each
        // complete LPDU to the emulation's time domain - never touching the controller from here.
        private void HandleDataReceived(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            var complete = new List<byte[]>();
            lock(receiveLocker)
            {
                receiveBuffer.AddRange(data);
                while(true)
                {
                    if(receiveBuffer.Count < LengthPrefixSize)
                    {
                        break;
                    }
                    var length = (receiveBuffer[0] << 8) | receiveBuffer[1];
                    if(length == 0)
                    {
                        // A zero-length LPDU has no control field and cannot be sent. Drop the
                        // prefix rather than stalling the stream on it forever.
                        this.Log(LogLevel.Warning, "Client sent a zero-length LPDU - ignored");
                        receiveBuffer.RemoveRange(0, LengthPrefixSize);
                        continue;
                    }
                    if(length > MaxLpduSize)
                    {
                        // The stream is out of step with the framing; nothing after this can be
                        // trusted, so drop what we have rather than emit garbage LPDUs.
                        this.Log(LogLevel.Error,
                            "Client announced a {0}-byte LPDU, above the {1}-byte maximum - dropping the buffer",
                            length, MaxLpduSize);
                        receiveBuffer.Clear();
                        break;
                    }
                    if(receiveBuffer.Count < LengthPrefixSize + length)
                    {
                        break; // the rest is still in flight
                    }
                    complete.Add(receiveBuffer.GetRange(LengthPrefixSize, length).ToArray());
                    receiveBuffer.RemoveRange(0, LengthPrefixSize + length);
                }
            }

            foreach(var lpdu in complete)
            {
                LpdusFromClient++;
                machine.HandleTimeDomainEvent<byte[]>(SendLpdu, lpdu, timeDomainInternalEvent: false);
            }
        }

        // Runs on the emulation's time-domain thread.
        private void SendLpdu(byte[] lpdu)
        {
            this.Log(LogLevel.Debug, "Bridge sending a client LPDU on interface {0}: {1}",
                iface, SWPProtocol.Describe(lpdu));
            controller.SendLpdu(iface, lpdu);
        }

        // Fired from the emulation thread for every LPDU the target sent, at every layer. The client
        // is the protocol layer, so it gets all of them, unabridged and uninterpreted.
        private void HandleLpduReceived(int sourceInterface, byte[] lpdu)
        {
            if(sourceInterface != iface || lpdu == null || lpdu.Length == 0)
            {
                return;
            }
            if(lpdu.Length > MaxLpduSize)
            {
                this.Log(LogLevel.Warning, "Not forwarding a {0}-byte LPDU: above the {1}-byte maximum",
                    lpdu.Length, MaxLpduSize);
                return;
            }
            var framed = new byte[LengthPrefixSize + lpdu.Length];
            framed[0] = (byte)(lpdu.Length >> 8);
            framed[1] = (byte)lpdu.Length;
            Array.Copy(lpdu, 0, framed, LengthPrefixSize, lpdu.Length);
            LpdusToClient++;
            this.Log(LogLevel.Debug, "Bridge forwarding an LPDU to the client: {0}", SWPProtocol.Describe(lpdu));
            server.Send(framed);
        }

        // A client that goes away mid-LPDU must not leave half a length prefix to be misread as the
        // start of the next client's first LPDU.
        private void HandleConnectionClosed()
        {
            lock(receiveLocker)
            {
                receiveBuffer.Clear();
            }
            this.Log(LogLevel.Info, "TCP client disconnected from the SWP LPDU bridge");
        }

        private readonly IMachine machine;
        private readonly SocketServerProvider server;
        private readonly SimpleSWPController controller;
        private readonly int iface;
        private readonly List<byte> receiveBuffer = new List<byte>();
        private readonly object receiveLocker = new object();

        private const int LengthPrefixSize = 2;

        // Bounds the reassembly buffer. Well above any real LPDU - SHDLC payloads are HCI packets,
        // and the largest MaxFramePayloadSize anything here advertises is 4096.
        private const int MaxLpduSize = 8192;
    }
}
