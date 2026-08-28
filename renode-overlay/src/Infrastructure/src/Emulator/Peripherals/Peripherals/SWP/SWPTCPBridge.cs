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
        //   emulation CreateSWPTCPBridge sysbus.swp 0 3456 true     # forward-on-unsolicited-data mode
        //
        // The client speaks RAW bytes in both directions and the bridge is transparent: whatever it
        // sends is driven on S1 unchanged, and whatever the target drives on S2 is streamed back
        // unchanged. No framing, CRC or protocol byte is added or removed anywhere in the path - the
        // SWP transport carries opaque bytes, so the client and the target own the protocol between
        // them. tools/swp-reference/ has a framing implementation if the client wants one.
        //
        // - synchronous: whatever the target drives on S2 in the same full-duplex slot is returned.
        //   This is the mode for a target that answers within the slot (e.g. EchoSWPDevice).
        // - forward-on-unsolicited-data: for a target whose answer is not ready in that slot (a
        //   firmware-managed one). The client's bytes are driven on S1 and nothing is returned yet;
        //   when the target later drives S2 on its own initiative, those bytes are forwarded. SWP is
        //   full duplex, so this needs no polling from the host.
        //
        // Determinism: every access the bridge makes to the controller and the target is marshalled
        // onto the emulation's time-domain thread (see SWPTCPBridge.HandleDataReceived). The CLF
        // drives the target in the SAME simulation time as the emulated CPU, never concurrently with
        // it, so a run is reproducible regardless of host socket timing - which is also why the
        // emulation must be running (`start`) and the line powered for a bridge transfer to execute.
        public static void CreateSWPTCPBridge(this Emulation emulation, SimpleSWPController controller,
            int line, int port, bool forwardOnUnsolicitedData = false, string name = "swpBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(
                new SWPTCPBridge(controller, line, port, forwardOnUnsolicitedData), name);
        }
    }

    // Bridges a UICC on a SimpleSWPController to a raw TCP socket. See CreateSWPTCPBridge for the two
    // response-delivery modes and the determinism guarantee.
    [Transient]
    public class SWPTCPBridge : IExternal, IDisposable
    {
        public SWPTCPBridge(SimpleSWPController controller, int line, int port,
            bool forwardOnUnsolicitedData = false)
        {
            this.controller = controller;
            this.line = line;
            this.forwardOnUnsolicitedData = forwardOnUnsolicitedData;
            // The machine that owns the controller - used to run every exchange inside its time domain.
            machine = controller.GetMachine();

            server = new SocketServerProvider(telnetMode: false, serverName: "SWPBridge");
            // Read up to a full chunk per recv so a message is delivered as one block, not byte-by-byte.
            server.BufferSize = 4096;
            server.ConnectionAccepted += _ => this.Log(LogLevel.Info, "TCP client connected on the SWP bridge for line {0}", line);
            server.ConnectionClosed += () => this.Log(LogLevel.Info, "TCP client disconnected from the SWP bridge");
            server.DataBlockReceived += HandleDataReceived;

            if(forwardOnUnsolicitedData)
            {
                target = controller.GetTarget(line);
                if(target != null)
                {
                    target.DataAvailable += HandleTargetData;
                }
                else
                {
                    this.Log(LogLevel.Warning, "No SWP target on line {0} to subscribe for unsolicited data", line);
                }
            }

            server.Start(port);
            this.Log(LogLevel.Info, "SWP TCP bridge for line {0} listening on port {1} ({2})",
                line, port, forwardOnUnsolicitedData ? "forward-on-unsolicited-data" : "synchronous");
        }

        public void Dispose()
        {
            server.DataBlockReceived -= HandleDataReceived;
            if(target != null)
            {
                target.DataAvailable -= HandleTargetData;
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

        // Runs on the emulation's time-domain thread. Drives the client's bytes on S1 and, in
        // synchronous mode, streams whatever came back on S2 in the same slot straight to the client.
        private void DriveExchange(byte[] data)
        {
            var answer = controller.Transfer(line, data);
            if(forwardOnUnsolicitedData)
            {
                // The answer arrives later, when the target drives S2 by itself (see
                // HandleTargetData). Anything returned in this slot is ignored.
                return;
            }
            ForwardToClient(answer);
        }

        // Fired from the emulation thread when the target drives S2 on its own initiative. The bytes
        // are opaque, so they go to the client exactly as the target produced them.
        private void HandleTargetData(ISWPPeripheral source, byte[] data)
        {
            ForwardToClient(data);
        }

        // Sends raw bytes to the connected TCP client, exactly as the target produced them.
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
        private readonly bool forwardOnUnsolicitedData;
    }
}
