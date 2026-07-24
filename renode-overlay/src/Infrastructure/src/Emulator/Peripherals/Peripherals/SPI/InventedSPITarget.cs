//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    // An "invented" memory-mapped SPI target for firmware-in-the-loop testing.
    //
    // On the SPI bus it is an ISPIPeripheral (via SimpleSPIPeripheral); on the sysbus it exposes a
    // small register interface that a CPU firmware polls:
    //   - MOSI bytes clocked in by the controller land in an RX FIFO the firmware reads out,
    //   - the firmware pushes a response into a TX FIFO and commits it,
    //   - on commit the target asserts its interrupt line carrying the response, so the controller
    //     (and, through it, the TCP bridge) receives the firmware's answer.
    //
    // This makes the "slave" genuinely firmware-managed: the controller clocks data to it, the emulated
    // firmware processes that data, and its response travels back over the same path.
    public class InventedSPITarget : SimpleSPIPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public override void Reset()
        {
            base.Reset();
            lock(locker)
            {
                rxFifo.Clear();
                txFifo.Clear();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.RxStatus:
                lock(locker)
                {
                    var count = (uint)rxFifo.Count;
                    return (count > 0 ? 1u : 0u) | (count << 8);
                }
            case Registers.RxData:
                lock(locker)
                {
                    return rxFifo.Count > 0 ? rxFifo.Dequeue() : 0u;
                }
            case Registers.TxStatus:
                lock(locker)
                {
                    return (uint)txFifo.Count << 8;
                }
            default:
                this.Log(LogLevel.Warning, "Read from an unhandled register 0x{0:X}", offset);
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch((Registers)offset)
            {
            case Registers.TxData:
                lock(locker)
                {
                    txFifo.Enqueue((byte)value);
                }
                break;
            case Registers.TxCommit:
                CommitResponse();
                break;
            case Registers.Control:
                if((value & 0x1) != 0)
                {
                    Reset();
                }
                break;
            default:
                this.Log(LogLevel.Warning, "Write 0x{0:X} to an unhandled register 0x{1:X}", value, offset);
                break;
            }
        }

        public long Size => 0x100;

        // The incoming MOSI byte goes to the RX FIFO for the firmware. MISO stays 0 during the command
        // phase: the firmware's response is delivered out-of-band via the interrupt on commit, NOT
        // shifted out here. (Shifting the TX FIFO out here would let the controller's ongoing RX clocks
        // consume the firmware's response bytes as discarded MISO when the two overlap.)
        protected override byte OnTransfer(byte incoming)
        {
            lock(locker)
            {
                rxFifo.Enqueue(incoming);
            }
            return 0;
        }

        private void CommitResponse()
        {
            byte[] response;
            lock(locker)
            {
                response = txFifo.ToArray();
                txFifo.Clear();
            }
            this.Log(LogLevel.Debug, "Firmware committed a {0}-byte response, asserting the interrupt line", response.Length);
            RequestInterrupt(response);
        }

        // Field initializers (run before the base constructor, which calls the virtual Reset()).
        private readonly Queue<byte> rxFifo = new Queue<byte>();
        private readonly Queue<byte> txFifo = new Queue<byte>();
        private readonly object locker = new object();

        private enum Registers : long
        {
            RxStatus = 0x00, // R: bit0 = RX data available, bits[15:8] = byte count
            RxData = 0x04,   // R: pop one byte from the RX FIFO
            TxData = 0x08,   // W: push one byte into the TX FIFO
            TxCommit = 0x0C, // W: finalise the response (asserts the interrupt line with the TX bytes)
            Control = 0x10,  // W: bit0 = clear both FIFOs
            TxStatus = 0x14, // R: bits[15:8] = pending TX byte count
        }
    }
}
