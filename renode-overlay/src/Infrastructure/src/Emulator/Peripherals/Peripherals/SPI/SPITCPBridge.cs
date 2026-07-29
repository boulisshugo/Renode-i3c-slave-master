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

namespace Antmicro.Renode.Peripherals.SPI
{
    public static class SPITCPBridgeExtensions
    {
        // Creates a raw TCP bridge to a target on a SimpleSPIController.
        //
        // Monitor usage:
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456          # full-duplex mode (default)
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456 true     # forward-on-interrupt mode
        //
        // The client speaks RAW bytes in both directions: whatever it sends is clocked to the slave, and
        // whatever the slave drives back is streamed to the client unmodified - no framing, no length
        // bytes, no idle-byte filtering added by the bridge.
        //
        // - full-duplex: the client is the master's brain. The bytes it sends are clocked out on MOSI and
        //   the MISO bytes shifted back in the SAME transfer are returned. The client decides how many
        //   bytes to clock (command + any read/dummy bytes), so it frames its own reads. This is the mode
        //   for synchronous slaves that drive their answer on the same clocks (e.g. EchoSPIDevice).
        // - forward-on-interrupt: for a slave whose answer is not ready on the command clocks (a
        //   firmware-managed slave). The client sends the command; the bridge clocks it in and returns
        //   nothing yet; when the slave later asserts its data-ready line, its raw payload is forwarded to
        //   the client. This is the deterministic SPI analog of an I3C In-Band Interrupt - it replaces
        //   host-thread polling, which could never share the CPU's simulation time.
        //
        // Determinism: every access the bridge makes to the controller and the slave is marshalled onto
        // the emulation's time-domain thread (see SPITCPBridge.HandleDataReceived). The controller drives
        // the slave in the SAME simulation time as the emulated CPU, never concurrently with it, so a run
        // is reproducible regardless of host socket timing.
        public static void CreateSPITCPBridge(this Emulation emulation, SimpleSPIController controller,
            int chipSelect, int port, bool forwardOnInterrupt = false, string name = "spiBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(
                new SPITCPBridge(controller, chipSelect, port, forwardOnInterrupt), name);
        }
    }

    // Bridges a single target on a SimpleSPIController to a raw TCP socket. See CreateSPITCPBridge for the
    // two response-delivery modes and the determinism guarantee.
    [Transient]
    public class SPITCPBridge : IExternal, IDisposable
    {
        public SPITCPBridge(SimpleSPIController controller, int chipSelect, int port,
            bool forwardOnInterrupt = false)
        {
            this.controller = controller;
            this.chipSelect = chipSelect;
            this.forwardOnInterrupt = forwardOnInterrupt;
            // The machine that owns the controller - used to run every transfer inside its time domain.
            machine = controller.GetMachine();

            server = new SocketServerProvider(telnetMode: false, serverName: "SPIBridge");
            // Read up to a full chunk per recv so a message is delivered as one block, not byte-by-byte.
            server.BufferSize = 4096;
            server.ConnectionAccepted += _ => this.Log(LogLevel.Info, "TCP client connected on the SPI bridge for chip select {0}", chipSelect);
            server.ConnectionClosed += () => this.Log(LogLevel.Info, "TCP client disconnected from the SPI bridge");
            server.DataBlockReceived += HandleDataReceived;

            // A SpiControllerSeHal does the send synchronously but delivers the polled response block
            // asynchronously via its BlockReceived event (not as the Transfer return value), so subscribe
            // to that and forward the raw block. This takes precedence over the target-interrupt path.
            seHalController = controller as SpiControllerSeHal;
            if(seHalController != null)
            {
                seHalController.BlockReceived += HandleBlockReceived;
            }
            else if(forwardOnInterrupt)
            {
                target = controller.GetTarget(chipSelect) as SimpleSPIPeripheral;
                if(target != null)
                {
                    target.InterruptRequested += HandleInterrupt;
                }
                else
                {
                    this.Log(LogLevel.Warning, "No SimpleSPIPeripheral target at chip select {0} to subscribe for interrupts", chipSelect);
                }
            }

            server.Start(port);
            var mode = seHalController != null ? "poll-and-forward-block"
                : (forwardOnInterrupt ? "forward-on-interrupt" : "full-duplex");
            this.Log(LogLevel.Info, "SPI TCP bridge for chip select {0} listening on port {1} ({2})",
                chipSelect, port, mode);
        }

        public void Dispose()
        {
            server.DataBlockReceived -= HandleDataReceived;
            if(target != null)
            {
                target.InterruptRequested -= HandleInterrupt;
            }
            if(seHalController != null)
            {
                seHalController.BlockReceived -= HandleBlockReceived;
            }
            server.Stop();
        }

        // Called on the host socket thread when the client sends raw bytes. We do NOT touch the controller
        // or the slave here: instead we hand the transaction to the machine's time domain so it runs on
        // the emulation thread, serialised with (never concurrent to) CPU execution. This is what makes
        // the bridge deterministic and puts the controller and slave on the same simulation clock.
        //
        // The whole transfer, and the reply back to the client, happen inside DriveTransfer, because the
        // marshalled call does not block this host thread waiting for a result.
        private void HandleDataReceived(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            this.Log(LogLevel.Debug, "Bridge received {0} bytes from TCP for chip select {1}: {2}",
                data.Length, chipSelect, Misc.PrettyPrintCollectionHex(data));

            machine.HandleTimeDomainEvent<byte[]>(DriveTransfer, data, timeDomainInternalEvent: false);
        }

        // Runs on the emulation's time-domain thread. Clocks the client's bytes to the slave through the
        // controller and, in full-duplex mode, streams the MISO bytes straight back.
        private void DriveTransfer(byte[] data)
        {
            var miso = controller.Transfer(chipSelect, data);
            if(seHalController != null)
            {
                // SpiControllerSeHal sent the command; the response block arrives later on the clock
                // thread via BlockReceived (see HandleBlockReceived). Nothing to forward now.
                return;
            }
            if(forwardOnInterrupt)
            {
                // The response is delivered later, when the slave asserts its data-ready line (see
                // HandleInterrupt). The MISO clocked back during the command is idle filler - ignore it.
                return;
            }
            ForwardToClient(miso);
        }

        // Fired from the emulation thread when a forward-on-interrupt slave asserts its data-ready line.
        // The payload is the raw response the slave wants to hand back.
        private void HandleInterrupt(ISPIPeripheral source, byte[] payload)
        {
            ForwardToClient(payload);
        }

        // Fired on the clock thread when a SpiControllerSeHal has polled a full response block out of the
        // SE. The block is the raw framed answer (NAD, PCB, LEN, payload+CRC) - forward it as-is.
        private void HandleBlockReceived(byte[] block)
        {
            ForwardToClient(block);
        }

        // Sends raw bytes to the connected TCP client, exactly as the slave drove them.
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
        private readonly SimpleSPIController controller;
        private readonly SpiControllerSeHal seHalController;
        private readonly SimpleSPIPeripheral target;
        private readonly int chipSelect;
        private readonly bool forwardOnInterrupt;
    }
}
