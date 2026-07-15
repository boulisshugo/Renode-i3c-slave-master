//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.I3C
{
    // An "invented" memory-mapped I3C target for firmware-in-the-loop testing.
    //
    // On the I3C bus it is an II3CPeripheral (via SimpleI3CPeripheral); on the sysbus it exposes a
    // small register interface that a CPU firmware polls:
    //   - bytes written by the I3C master land in an RX FIFO the firmware reads out,
    //   - the firmware pushes a response into a TX FIFO and commits it,
    //   - on commit the target raises an In-Band Interrupt carrying the response, so the controller
    //     (and, through it, the TCP bridge) receives the firmware's answer.
    //
    // This makes the "slave" genuinely firmware-managed: the Renode master transmits data to it, the
    // emulated firmware processes that data, and its response travels back over the same path.
    public class InventedI3CTarget : SimpleI3CPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public InventedI3CTarget(ulong provisionedId = 0, byte busCharacteristics = 0x02,
            byte deviceCharacteristics = 0, byte staticAddress = 0, byte interruptDataByte = 0x01)
            : base(provisionedId, busCharacteristics, deviceCharacteristics, staticAddress)
        {
            this.interruptDataByte = interruptDataByte;
        }

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

        protected override void OnWritePrivate(byte[] data)
        {
            lock(locker)
            {
                foreach(var b in data)
                {
                    rxFifo.Enqueue(b);
                }
            }
            this.Log(LogLevel.Debug, "Received {0} bytes from the I3C master into the RX FIFO", data.Length);
        }

        private void CommitResponse()
        {
            byte[] response;
            lock(locker)
            {
                response = txFifo.ToArray();
                txFifo.Clear();
            }
            this.Log(LogLevel.Debug, "Firmware committed a {0}-byte response, raising an IBI", response.Length);
            RequestInBandInterrupt(interruptDataByte, response);
        }

        // These are initialised as field initializers (which run before the base constructor) because
        // the base SimpleI3CPeripheral constructor calls the virtual Reset(), which touches them.
        private readonly Queue<byte> rxFifo = new Queue<byte>();
        private readonly Queue<byte> txFifo = new Queue<byte>();
        private readonly object locker = new object();
        private readonly byte interruptDataByte;

        private enum Registers : long
        {
            RxStatus = 0x00, // R: bit0 = RX data available, bits[15:8] = byte count
            RxData = 0x04,   // R: pop one byte from the RX FIFO
            TxData = 0x08,   // W: push one byte into the TX FIFO
            TxCommit = 0x0C, // W: finalise the response (raises an IBI carrying the TX bytes)
            Control = 0x10,  // W: bit0 = clear both FIFOs
            TxStatus = 0x14, // R: bits[15:8] = pending TX byte count
        }
    }
}
