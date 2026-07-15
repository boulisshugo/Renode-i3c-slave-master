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

namespace Antmicro.Renode.Peripherals.I3C
{
    public static class I3CTCPBridgeExtensions
    {
        // Creates a raw TCP bridge to a target on a SimpleI3CController.
        //
        // Monitor usage:
        //   emulation CreateI3CTCPBridge sysbus.i3c 0x08 3456            # write-then-read mode
        //   emulation CreateI3CTCPBridge sysbus.i3c 0x08 3456 true       # forward-on-interrupt mode
        //
        // In forward-on-interrupt mode the bridge only writes the received bytes to the target and
        // returns nothing synchronously; the target's response is delivered later, when it raises an
        // In-Band Interrupt (e.g. a firmware-managed target). This suits an asynchronous, polled client.
        public static void CreateI3CTCPBridge(this Emulation emulation, SimpleI3CController controller,
            int address, int port, bool forwardOnInterrupt = false, string name = "i3cBridge")
        {
            if(port < 0 || port > 65535)
            {
                throw new RecoverableException("Port must be between 0 and 65535");
            }
            emulation.ExternalsManager.AddExternal(new I3CTCPBridge(controller, (byte)address, port, forwardOnInterrupt), name);
        }
    }

    // Bridges a single target on a SimpleI3CController to a raw TCP socket.
    //
    // Raw bytes received from the connected TCP client are transmitted to the target as an SDR
    // private write. The target's response is streamed back to the client either:
    //   - synchronously, by reading it back right after the write (the default), which realises the
    //     common I3C private-write-then-read exchange as a transparent, frameless byte pipe; or
    //   - asynchronously, when forwardOnInterrupt is set: the response arrives when the target raises
    //     an In-Band Interrupt (its payload, minus the Mandatory Data Byte, is forwarded to the client).
    //     This is the right mode for a firmware-managed target that produces its answer out-of-band.
    //
    // In write-then-read mode the number of bytes read back is controlled by ReadLength: 0 (the
    // default) mirrors the number of bytes just written, a positive value forces a fixed length.
    [Transient]
    public class I3CTCPBridge : IExternal, IDisposable
    {
        public I3CTCPBridge(SimpleI3CController controller, byte address, int port, bool forwardOnInterrupt = false)
        {
            this.controller = controller;
            this.address = address;
            this.forwardOnInterrupt = forwardOnInterrupt;

            server = new SocketServerProvider(telnetMode: false, serverName: "I3CBridge");
            // Read up to a full chunk per recv so a message is delivered as one block rather than
            // byte-by-byte (the default buffer size is 1).
            server.BufferSize = 4096;
            server.ConnectionAccepted += _ => this.Log(LogLevel.Info, "TCP client connected on the I3C bridge for target 0x{0:X2}", address);
            server.ConnectionClosed += () => this.Log(LogLevel.Info, "TCP client disconnected from the I3C bridge");
            server.DataBlockReceived += HandleDataReceived;

            if(forwardOnInterrupt)
            {
                target = controller.GetTarget(address);
                if(target != null)
                {
                    target.InBandInterruptRequested += HandleInBandInterrupt;
                }
                else
                {
                    this.Log(LogLevel.Warning, "No I3C target registered at 0x{0:X2} to subscribe for In-Band Interrupts", address);
                }
            }

            server.Start(port);
            this.Log(LogLevel.Info, "I3C TCP bridge for target 0x{0:X2} listening on port {1}{2}",
                address, port, forwardOnInterrupt ? " (forward-on-interrupt)" : "");
        }

        public void Dispose()
        {
            server.DataBlockReceived -= HandleDataReceived;
            if(target != null)
            {
                target.InBandInterruptRequested -= HandleInBandInterrupt;
            }
            server.Stop();
        }

        // Number of bytes to read back from the target after each write and forward to the client
        // (write-then-read mode only). 0 (default) mirrors the number of bytes just written.
        public int ReadLength { get; set; }

        private void HandleDataReceived(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            this.Log(LogLevel.Debug, "Bridge received {0} bytes from TCP, writing to target 0x{1:X2}: {2}",
                data.Length, address, Misc.PrettyPrintCollectionHex(data));
            controller.WritePrivate(address, data);

            if(forwardOnInterrupt)
            {
                // The response will arrive asynchronously via an In-Band Interrupt.
                return;
            }

            var count = ReadLength > 0 ? ReadLength : data.Length;
            if(count <= 0)
            {
                return;
            }

            var response = controller.ReadPrivate(address, count);
            if(response != null && response.Length > 0)
            {
                this.Log(LogLevel.Debug, "Bridge forwarding {0} bytes from target 0x{1:X2} to TCP: {2}",
                    response.Length, address, Misc.PrettyPrintCollectionHex(response));
                server.Send(response);
            }
        }

        private void HandleInBandInterrupt(II3CPeripheral source, byte[] payload)
        {
            // The payload is the Mandatory Data Byte followed by the response bytes; forward the
            // response bytes to the TCP client.
            if(payload == null || payload.Length <= 1)
            {
                return;
            }
            var response = new byte[payload.Length - 1];
            Array.Copy(payload, 1, response, 0, response.Length);
            this.Log(LogLevel.Debug, "Bridge forwarding {0} bytes from an IBI of target 0x{1:X2} to TCP: {2}",
                response.Length, address, Misc.PrettyPrintCollectionHex(response));
            server.Send(response);
        }

        private readonly SocketServerProvider server;
        private readonly SimpleI3CController controller;
        private readonly II3CPeripheral target;
        private readonly byte address;
        private readonly bool forwardOnInterrupt;
    }
}
