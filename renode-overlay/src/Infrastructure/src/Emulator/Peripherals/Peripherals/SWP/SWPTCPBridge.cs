//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Migrant;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    public static class SWPTCPBridgeExtensions
    {
        // Creates a raw TCP bridge to a UICC on a SimpleSWPController.
        //
        // Monitor usage:
        //   emulation CreateSWPTCPBridge sysbus.swp 0 3456          # synchronous mode (default)
        //   emulation CreateSWPTCPBridge sysbus.swp 0 3456 true     # forward-on-unsolicited-frame mode
        //
        // The client speaks RAW LLC payload bytes in both directions - the bridge adds no framing of
        // its own. The SWP framing (SOF, bit stuffing, CRC, EOF) and the SHDLC control byte are put on
        // and taken off by the controller and the target, exactly as they are on a real link, so the
        // client sees only the payload the UICC's application layer would see.
        //
        // - synchronous: the bytes the client sends become one SHDLC I-frame; whatever the UICC
        //   piggybacks on its acknowledgement is streamed straight back. This is the mode for a UICC
        //   that answers within the frame it is answering - which in practice means a host-side
        //   stack (SoftwareSWPTarget, EchoSWPDevice), since firmware cannot answer that fast.
        // - forward-on-unsolicited-frame: for a UICC whose answer is not ready in that slot - a
        //   firmware-managed target (InventedSWPTarget), where the answer only exists once the
        //   emulated CPU has run. The client's bytes are sent as an I-frame and nothing is returned
        //   yet; when the UICC later transmits on S2 on its own initiative, the controller decodes
        //   and sequence-checks that frame and the payload is forwarded to the client. SWP is full
        //   duplex, so this needs no polling from the host.
        //
        // Determinism: every access the bridge makes to the controller and the target is marshalled
        // onto the emulation's time-domain thread (see SWPTCPBridge.HandleDataReceived). The CLF
        // drives the UICC in the SAME simulation time as the emulated CPU, never concurrently with
        // it, so a run is reproducible regardless of host socket timing - which is also why the
        // emulation must be running (`start`) for a bridge exchange to execute.
        public static void CreateSWPTCPBridge(this Emulation emulation, SimpleSWPController controller,
            int iface, int port, bool forwardOnUnsolicitedFrame = false, string name = "swpBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(
                new SWPTCPBridge(controller, iface, port, forwardOnUnsolicitedFrame), name);
        }
    }

    // Bridges a UICC on a SimpleSWPController to a raw TCP socket. See CreateSWPTCPBridge for the two
    // response-delivery modes and the determinism guarantee.
    [Transient]
    public class SWPTCPBridge : IExternal, IDisposable
    {
        public SWPTCPBridge(SimpleSWPController controller, int iface, int port,
            bool forwardOnUnsolicitedFrame = false)
        {
            this.controller = controller;
            this.iface = iface;
            this.forwardOnUnsolicitedFrame = forwardOnUnsolicitedFrame;
            // The machine that owns the controller - used to run every exchange inside its time domain.
            machine = controller.GetMachine();

            server = new SocketServerProvider(telnetMode: false, serverName: "SWPBridge");
            // Read up to a full chunk per recv so a message is delivered as one block, not byte-by-byte.
            server.BufferSize = 4096;
            server.ConnectionAccepted += _ => this.Log(LogLevel.Info, "TCP client connected on the SWP bridge for interface {0}", iface);
            server.ConnectionClosed += () => this.Log(LogLevel.Info, "TCP client disconnected from the SWP bridge");
            server.DataBlockReceived += HandleDataReceived;

            if(forwardOnUnsolicitedFrame)
            {
                // Subscribe to the controller, not to the target: by the time the controller
                // publishes a payload it has decoded the frame, checked its CRC and its N(S), and
                // stripped the SHDLC control field - so the client gets exactly the application
                // bytes, and an out-of-sequence or corrupt frame never reaches it.
                controller.PayloadReceived += HandleControllerPayload;
            }

            server.Start(port);
            this.Log(LogLevel.Info, "SWP TCP bridge for interface {0} listening on port {1} ({2})",
                iface, port, forwardOnUnsolicitedFrame ? "forward-on-unsolicited-frame" : "synchronous");
        }

        public void Dispose()
        {
            server.DataBlockReceived -= HandleDataReceived;
            if(forwardOnUnsolicitedFrame)
            {
                controller.PayloadReceived -= HandleControllerPayload;
            }
            server.Stop();
        }

        // Called on the host socket thread when the client sends raw bytes. We do NOT touch the
        // controller or the target here: instead we hand the exchange to the machine's time domain so
        // it runs on the emulation thread, serialised with (never concurrent to) CPU execution.
        private void HandleDataReceived(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            this.Log(LogLevel.Debug, "Bridge received {0} bytes from TCP for SWP interface {1}: {2}",
                data.Length, iface, Misc.PrettyPrintCollectionHex(data));

            machine.HandleTimeDomainEvent<byte[]>(DriveExchange, data, timeDomainInternalEvent: false);
        }

        // Runs on the emulation's time-domain thread. Sends the client's bytes as one SHDLC I-frame
        // and, in synchronous mode, streams the payload the UICC piggybacked straight back.
        private void DriveExchange(byte[] data)
        {
            var answer = controller.Send(iface, data);
            if(forwardOnUnsolicitedFrame)
            {
                // The response arrives later, when the UICC transmits on S2 by itself (see
                // HandleTargetFrame). Anything acknowledged in this slot is a bare RR - ignore it.
                return;
            }
            ForwardToClient(answer);
        }

        // Fired from the emulation thread when the controller has accepted an I-frame payload from a
        // target - the answer a firmware-managed UICC built after the slot that asked for it.
        private void HandleControllerPayload(int sourceLine, byte[] information)
        {
            if(sourceLine != iface)
            {
                return;
            }
            ForwardToClient(information);
        }

        // Sends raw bytes to the connected TCP client, exactly as the UICC's application produced them.
        private void ForwardToClient(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }
            this.Log(LogLevel.Debug, "Bridge forwarding {0} raw bytes to TCP: {1}",
                data.Length, Misc.PrettyPrintCollectionHex(data));
            server.Send(data);
        }

        private readonly IMachine machine;
        private readonly SocketServerProvider server;
        private readonly SimpleSWPController controller;
        private readonly int iface;
        private readonly bool forwardOnUnsolicitedFrame;
    }
}
