//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;
using System.Threading;

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
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456                       # full-duplex mode
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456 true                  # forward-on-interrupt mode
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456 false true            # poll-for-response mode
        //   emulation CreateSPITCPBridge sysbus.spi 0 3456 false false true      # full-duplex, strip 0xFF idle
        //
        // - full-duplex: the MISO bytes returned by the same transfer are streamed back (synchronous
        //   slaves).
        // - forward-on-interrupt: the response arrives when the target asserts its interrupt line
        //   (a slave with a side-band IRQ pin).
        // - poll-for-response: after clocking the command, the master POLLS the slave (SPI slaves cannot
        //   push) - clocking a status byte until it becomes non-zero (the length), then clocking out
        //   that many response bytes. This is the right mode for the firmware-managed InventedSPITarget.
        //
        // stripIdleBytes: when true, the bridge drops the slave's idle/busy filler bytes (0xFF by
        // default, see SPITCPBridge.IdleByte) from what it forwards to the client, so the client sees
        // only the bytes the slave actually drove. Leave it off (the default) for binary-clean transfers
        // where 0xFF is legitimate data.
        public static void CreateSPITCPBridge(this Emulation emulation, SimpleSPIController controller,
            int chipSelect, int port, bool forwardOnInterrupt = false, bool pollForResponse = false,
            bool stripIdleBytes = false, string name = "spiBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(
                new SPITCPBridge(controller, chipSelect, port, forwardOnInterrupt, pollForResponse, stripIdleBytes), name);
        }
    }

    // Bridges a single target on a SimpleSPIController to a raw TCP socket. See CreateSPITCPBridge for
    // the response-delivery modes and the idle-byte stripping option.
    [Transient]
    public class SPITCPBridge : IExternal, IDisposable
    {
        public SPITCPBridge(SimpleSPIController controller, int chipSelect, int port,
            bool forwardOnInterrupt = false, bool pollForResponse = false, bool stripIdleBytes = false)
        {
            this.controller = controller;
            this.chipSelect = chipSelect;
            this.forwardOnInterrupt = forwardOnInterrupt;
            this.pollForResponse = pollForResponse;
            StripIdleBytes = stripIdleBytes;

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
            this.Log(LogLevel.Info, "SPI TCP bridge for chip select {0} listening on port {1} ({2}{3})",
                chipSelect, port, forwardOnInterrupt ? "forward-on-interrupt" : (pollForResponse ? "poll-for-response" : "full-duplex"),
                StripIdleBytes ? ", stripping idle bytes" : "");
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

        // Max time to poll a firmware-managed slave for its response.
        public int PollTimeoutMilliseconds { get; set; } = 5000;

        // When true, IdleByte values are removed from the bytes forwarded to the TCP client.
        public bool StripIdleBytes { get; set; }

        // The slave's idle/busy MISO value to drop when StripIdleBytes is set. SPI MISO idles high, so
        // this is 0xFF by default.
        public byte IdleByte { get; set; } = 0xFF;

        private void HandleDataReceived(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            this.Log(LogLevel.Debug, "Bridge received {0} bytes from TCP, sending to chip select {1}: {2}",
                data.Length, chipSelect, Misc.PrettyPrintCollectionHex(data));

            if(pollForResponse)
            {
                PollForResponse(data);
                return;
            }

            var response = controller.Transfer(chipSelect, data);
            if(forwardOnInterrupt)
            {
                // The response arrives asynchronously via the target's interrupt line.
                return;
            }
            ForwardToClient(response);
        }

        private void PollForResponse(byte[] command)
        {
            // 1. Clock the command to the slave (one chip-select transaction).
            controller.Transfer(chipSelect, command);

            // 2. Poll: hold chip select, clock a status byte until it is non-zero (the response length),
            //    then clock out that many response bytes.
            controller.Select(chipSelect);
            try
            {
                var length = 0;
                var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMilliseconds);
                while(DateTime.UtcNow < deadline)
                {
                    var status = controller.Transmit(chipSelect, 0x00);
                    if(status != 0)
                    {
                        length = status;
                        break;
                    }
                    Thread.Sleep(1); // let the emulated firmware run
                }

                if(length == 0)
                {
                    this.Log(LogLevel.Warning, "Timed out polling chip select {0} for a response", chipSelect);
                    return;
                }

                var response = new byte[length];
                for(var i = 0; i < length; i++)
                {
                    response[i] = controller.Transmit(chipSelect, 0x00);
                }
                ForwardToClient(response);
            }
            finally
            {
                controller.Deselect(chipSelect);
            }
        }

        private void HandleInterrupt(ISPIPeripheral source, byte[] payload)
        {
            ForwardToClient(payload);
        }

        // Sends bytes to the connected TCP client, optionally dropping the slave's idle/busy filler.
        private void ForwardToClient(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }
            if(StripIdleBytes)
            {
                data = data.Where(b => b != IdleByte).ToArray();
                if(data.Length == 0)
                {
                    return;
                }
            }
            this.Log(LogLevel.Debug, "Bridge forwarding {0} bytes to TCP: {1}",
                data.Length, Misc.PrettyPrintCollectionHex(data));
            server.Send(data);
        }

        private readonly SocketServerProvider server;
        private readonly SimpleSPIController controller;
        private readonly SimpleSPIPeripheral target;
        private readonly int chipSelect;
        private readonly bool forwardOnInterrupt;
        private readonly bool pollForResponse;
    }
}
