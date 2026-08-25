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
        //   that answers within the frame it is answering (e.g. EchoSWPDevice).
        // - forward-on-unsolicited-frame: for a UICC whose answer is not ready in that slot (a
        //   firmware-managed target). The client's bytes are sent as an I-frame and nothing is
        //   returned yet; when the UICC later transmits on S2 on its own initiative, that payload is
        //   forwarded to the client. SWP is full duplex, so this needs no polling from the host.
        //
        // Determinism: every access the bridge makes to the controller and the target is marshalled
        // onto the emulation's time-domain thread (see SWPTCPBridge.HandleDataReceived). The CLF
        // drives the UICC in the SAME simulation time as the emulated CPU, never concurrently with
        // it, so a run is reproducible regardless of host socket timing - which is also why the
        // emulation must be running (`start`) for a bridge exchange to execute.
        public static void CreateSWPTCPBridge(this Emulation emulation, SimpleSWPController controller,
            int line, int port, bool forwardOnUnsolicitedFrame = false, string name = "swpBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(
                new SWPTCPBridge(controller, line, port, forwardOnUnsolicitedFrame), name);
        }
    }

    // Bridges a UICC on a SimpleSWPController to a raw TCP socket. See CreateSWPTCPBridge for the two
    // response-delivery modes and the determinism guarantee.
    [Transient]
    public class SWPTCPBridge : IExternal, IDisposable
    {
        public SWPTCPBridge(SimpleSWPController controller, int line, int port,
            bool forwardOnUnsolicitedFrame = false)
        {
            this.controller = controller;
            this.line = line;
            this.forwardOnUnsolicitedFrame = forwardOnUnsolicitedFrame;
            // The machine that owns the controller - used to run every exchange inside its time domain.
            machine = controller.GetMachine();

            server = new SocketServerProvider(telnetMode: false, serverName: "SWPBridge");
            // Read up to a full chunk per recv so a message is delivered as one block, not byte-by-byte.
            server.BufferSize = 4096;
            server.ConnectionAccepted += _ => this.Log(LogLevel.Info, "TCP client connected on the SWP bridge for line {0}", line);
            server.ConnectionClosed += () => this.Log(LogLevel.Info, "TCP client disconnected from the SWP bridge");
            server.DataBlockReceived += HandleDataReceived;

            if(forwardOnUnsolicitedFrame)
            {
                target = controller.GetTarget(line);
                if(target != null)
                {
                    target.FrameAvailable += HandleTargetFrame;
                }
                else
                {
                    this.Log(LogLevel.Warning, "No SWP target on line {0} to subscribe for unsolicited frames", line);
                }
            }

            server.Start(port);
            this.Log(LogLevel.Info, "SWP TCP bridge for line {0} listening on port {1} ({2})",
                line, port, forwardOnUnsolicitedFrame ? "forward-on-unsolicited-frame" : "synchronous");
        }

        public void Dispose()
        {
            server.DataBlockReceived -= HandleDataReceived;
            if(target != null)
            {
                target.FrameAvailable -= HandleTargetFrame;
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

            this.Log(LogLevel.Debug, "Bridge received {0} bytes from TCP for SWP line {1}: {2}",
                data.Length, line, Misc.PrettyPrintCollectionHex(data));

            machine.HandleTimeDomainEvent<byte[]>(DriveExchange, data, timeDomainInternalEvent: false);
        }

        // Runs on the emulation's time-domain thread. Sends the client's bytes as one SHDLC I-frame
        // and, in synchronous mode, streams the payload the UICC piggybacked straight back.
        private void DriveExchange(byte[] data)
        {
            var answer = controller.Send(line, data);
            if(forwardOnUnsolicitedFrame)
            {
                // The response arrives later, when the UICC transmits on S2 by itself (see
                // HandleTargetFrame). Anything acknowledged in this slot is a bare RR - ignore it.
                return;
            }
            ForwardToClient(answer);
        }

        // Fired from the emulation thread when the UICC transmits a frame on its own initiative. The
        // controller has already decoded and sequence-checked it; decode the payload out of the wire
        // frame here so the client gets exactly the application bytes.
        private void HandleTargetFrame(ISWPPeripheral source, byte[] wireFrame)
        {
            if(!SWPFrame.TryDecode(wireFrame, out var payload, out var error))
            {
                this.Log(LogLevel.Warning, "Bridge could not decode an unsolicited frame: {0}", error);
                return;
            }
            if(payload.Length < 2
                || SWPProtocol.GetFrameKind(payload[0]) != SWPProtocol.ShdlcFrameKind.Information)
            {
                return;
            }
            var information = new byte[payload.Length - 1];
            Array.Copy(payload, 1, information, 0, information.Length);
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
        private readonly ISWPPeripheral target;
        private readonly int line;
        private readonly bool forwardOnUnsolicitedFrame;
    }
}
