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
    // request/response protocol whose answer is delivered by an interrupt (SPI slaves cannot push a
    // response onto the command clocks, so the master must not busy-wait on the bus):
    //
    //   1. Command phase - the controller asserts chip select and clocks the command bytes; they land in
    //                      an RX FIFO. On deassert the target enters PROCESSING.
    //   2. Processing    - the firmware reads the command from RX, computes a response, writes it to the
    //                      TX FIFO, and commits. On commit the target asserts its data-ready interrupt
    //                      carrying the response bytes as the payload, then returns to idle.
    //
    // Delivering the response by interrupt (instead of having the master poll a status byte) keeps the
    // whole exchange inside the emulation's time domain: the command arrives on the time-domain thread,
    // the firmware runs on the CPU, and the commit-driven interrupt fires on that same thread - nothing
    // depends on host wall-clock polling, so the round-trip is deterministic. The SPITCPBridge forwards
    // the interrupt payload straight to its TCP client (forward-on-interrupt mode).
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
            byte[] response = null;
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
                        // Snapshot the response and return to idle under the lock; raise the interrupt
                        // outside it (the interrupt chain reaches the bridge and the host socket).
                        response = txFifo.ToArray();
                        txFifo.Clear();
                        state = State.Idle;
                    }
                }
                if(response != null)
                {
                    this.Log(LogLevel.Debug, "Firmware committed a {0}-byte response; asserting the data-ready interrupt", response.Length);
                    RequestInterrupt(response);
                }
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

        // Chip select framing: a command is exactly one CS-asserted transaction.
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
                }
            }
        }

        protected override byte OnTransfer(byte incoming)
        {
            lock(locker)
            {
                if(state == State.ReceivingCommand)
                {
                    rxFifo.Enqueue(incoming);
                }
                // The response never rides the command clocks - it is delivered by interrupt - so MISO is
                // always idle filler here.
                return 0;
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
        }

        private enum Registers : long
        {
            RxStatus = 0x00, // R: bit0 = command ready to read, bits[15:8] = byte count
            RxData = 0x04,   // R: pop one byte from the RX FIFO
            TxData = 0x08,   // W: push one response byte into the TX FIFO
            TxCommit = 0x0C, // W: finalise the response and assert the data-ready interrupt
            Control = 0x10,  // W: bit0 = clear FIFOs and return to idle
        }
    }
}
