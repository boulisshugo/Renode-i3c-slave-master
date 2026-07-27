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
    // An "invented" memory-mapped SPI target for firmware-in-the-loop testing, driven by a real
    // request/response SPI protocol that the master POLLS (SPI slaves cannot push):
    //
    //   1. Command phase   - the controller asserts chip select and clocks the command bytes; they land
    //                        in an RX FIFO. On deassert the target enters PROCESSING.
    //   2. Processing      - the firmware reads the command from RX, computes a response, writes it to
    //                        the TX FIFO (a length byte followed by the data), and commits. Poll clocks
    //                        that arrive meanwhile read back 0x00 (busy) and are NOT buffered.
    //   3. Response phase  - once committed, poll clocks shift out the TX FIFO: the master reads the
    //                        length byte (non-zero => ready) and then that many response bytes.
    //
    // Registered on both the sysbus (MMIO, for the firmware) and the SPI bus (for the controller).
    public class InventedSPITarget : SimpleSPIPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public override void Reset()
        {
            base.Reset();
            lock(locker)
            {
                rxFifo.Clear();
                txFifo.Clear();
                state = State.Idle;
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.RxStatus:
                lock(locker)
                {
                    // The command is readable only once fully received (chip select deasserted).
                    var available = state == State.Processing && rxFifo.Count > 0;
                    return (available ? 1u : 0u) | ((uint)rxFifo.Count << 8);
                }
            case Registers.RxData:
                lock(locker)
                {
                    return rxFifo.Count > 0 ? rxFifo.Dequeue() : 0u;
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
                lock(locker)
                {
                    if(state == State.Processing)
                    {
                        state = State.SendingResponse;
                    }
                }
                this.Log(LogLevel.Debug, "Firmware committed a response ({0} bytes incl. length); ready to be polled", CountUnderLock());
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

        // Chip select framing: a command is exactly one CS-asserted transaction; the response is read
        // back in later (polled) transactions.
        protected override void OnSelect(bool select)
        {
            lock(locker)
            {
                if(select)
                {
                    if(state == State.Idle)
                    {
                        state = State.ReceivingCommand;
                        rxFifo.Clear();
                    }
                }
                else
                {
                    if(state == State.ReceivingCommand)
                    {
                        state = rxFifo.Count > 0 ? State.Processing : State.Idle;
                    }
                    else if(state == State.SendingResponse && txFifo.Count == 0)
                    {
                        state = State.Idle;
                    }
                }
            }
        }

        protected override byte OnTransfer(byte incoming)
        {
            lock(locker)
            {
                switch(state)
                {
                case State.ReceivingCommand:
                    rxFifo.Enqueue(incoming);
                    return 0;
                case State.SendingResponse:
                    return txFifo.Count > 0 ? txFifo.Dequeue() : (byte)0;
                default:
                    // Idle or Processing: busy - poll clocks read back 0 and are not buffered.
                    return 0;
                }
            }
        }

        private int CountUnderLock()
        {
            lock(locker)
            {
                return txFifo.Count;
            }
        }

        private readonly Queue<byte> rxFifo = new Queue<byte>();
        private readonly Queue<byte> txFifo = new Queue<byte>();
        private readonly object locker = new object();
        private State state = State.Idle;

        private enum State
        {
            Idle,
            ReceivingCommand,
            Processing,
            SendingResponse,
        }

        private enum Registers : long
        {
            RxStatus = 0x00, // R: bit0 = command ready to read, bits[15:8] = byte count
            RxData = 0x04,   // R: pop one byte from the RX FIFO
            TxData = 0x08,   // W: push one response byte into the TX FIFO (length byte first, then data)
            TxCommit = 0x0C, // W: mark the response ready to be polled
            Control = 0x10,  // W: bit0 = clear FIFOs and return to idle
        }
    }
}
