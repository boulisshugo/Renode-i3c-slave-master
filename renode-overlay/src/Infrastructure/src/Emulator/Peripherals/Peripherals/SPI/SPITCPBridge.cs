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
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SPI
{
    public static class SPITCPBridgeExtensions
    {
        // Creates a raw TCP bridge to a target on a SimpleSPIController.
        //
        // Monitor usage:
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456           # full-duplex mode
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456 true      # forward-on-interrupt mode
        //
        // In forward-on-interrupt mode the bridge clocks the received bytes into the target but returns
        // nothing synchronously; the target's response is delivered later, when it asserts its interrupt
        // line (e.g. a firmware-managed target). This suits an asynchronous, polled client.
        public static void CreateSPITCPBridge(this Emulation emulation, SimpleSPIController controller,
            int chipSelect, int port, bool forwardOnInterrupt = false, string name = "spiBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(new SPITCPBridge(controller, chipSelect, port, forwardOnInterrupt), name);
        }
    }

    // Bridges a single target on a SimpleSPIController to a raw TCP socket.
    //
    // Bytes from the TCP client are clocked to the target as a full-duplex SPI transfer. The target's
    // response reaches the client either:
    //   - synchronously, as the MISO bytes returned by the same transfer (the default) - because SPI is
    //     full-duplex, N bytes in yields N bytes out, a transparent frameless byte pipe; or
    //   - asynchronously, when forwardOnInterrupt is set: the response is whatever the target hands back
    //     on its interrupt line (right for a firmware-managed target that answers out-of-band).
    [Transient]
    public class SPITCPBridge : IExternal, IDisposable
    {
        public SPITCPBridge(SimpleSPIController controller, int chipSelect, int port, bool forwardOnInterrupt = false)
        {
            this.controller = controller;
            this.chipSelect = chipSelect;
            this.forwardOnInterrupt = forwardOnInterrupt;

            server = new SocketServerProvider(telnetMode: false, serverName: "SPIBridge");
            // Read up to a full chunk per recv so a message is delivered as one block, not byte-by-byte.
            server.BufferSize = 4096;
            server.ConnectionAccepted += _ => this.Log(LogLevel.Info, "TCP client connected on the SPI bridge for chip select {0}", chipSelect);
            server.ConnectionClosed += () => this.Log(LogLevel.Info, "TCP client disconnected from the SPI bridge");
            server.DataBlockReceived += HandleDataReceived;

            if(forwardOnInterrupt)
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
            this.Log(LogLevel.Info, "SPI TCP bridge for chip select {0} listening on port {1}{2}",
                chipSelect, port, forwardOnInterrupt ? " (forward-on-interrupt)" : "");
        }

        public void Dispose()
        {
            server.DataBlockReceived -= HandleDataReceived;
            if(target != null)
            {
                target.InterruptRequested -= HandleInterrupt;
            }
            server.Stop();
        }

        private void HandleDataReceived(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            this.Log(LogLevel.Debug, "Bridge received {0} bytes from TCP, transferring to chip select {1}: {2}",
                data.Length, chipSelect, Misc.PrettyPrintCollectionHex(data));
            var response = controller.Transfer(chipSelect, data);

            if(forwardOnInterrupt)
            {
                // The response will arrive asynchronously via the target's interrupt line.
                return;
            }

            if(response != null && response.Length > 0)
            {
                this.Log(LogLevel.Debug, "Bridge forwarding {0} MISO bytes to TCP: {1}",
                    response.Length, Misc.PrettyPrintCollectionHex(response));
                server.Send(response);
            }
        }

        private void HandleInterrupt(ISPIPeripheral source, byte[] payload)
        {
            if(payload == null || payload.Length == 0)
            {
                return;
            }
            this.Log(LogLevel.Debug, "Bridge forwarding {0} bytes from an interrupt of chip select {1} to TCP: {2}",
                payload.Length, chipSelect, Misc.PrettyPrintCollectionHex(payload));
            server.Send(payload);
        }

        private readonly SocketServerProvider server;
        private readonly SimpleSPIController controller;
        private readonly SimpleSPIPeripheral target;
        private readonly int chipSelect;
        private readonly bool forwardOnInterrupt;
    }
}
