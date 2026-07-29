//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SPI
{
    // A SimpleSPIController variant that models the Thales secure-element HAL exchange and does the
    // polling AND the frame parsing itself, entirely on the machine clock source - never blocking the
    // simulation.
    //
    //   1. Send    - Transfer() clocks the command out synchronously and returns immediately, leaving
    //                chip select asserted and arming the poll timer. It does NOT wait for a response.
    //   2. Poll    - a LimitTimer on machine.ClockSource fires one tick per poll interval. Each tick
    //                clocks exactly ONE byte and advances a small receive state machine:
    //                   wait for NAD (non-0xFF) -> read PCB -> read LEN -> read LEN body bytes.
    //                Because only one byte is clocked per tick, virtual time advances between every read,
    //                so a firmware-driven SE gets CPU time to produce the next byte. Nothing spins or
    //                sleeps, so the CPU and the rest of the emulation keep running throughout.
    //   3. Deliver - once the whole [NAD, PCB, LEN, body] frame is assembled, it is published via the
    //                BlockReceived event (and LastReceivedBlockHex), and chip select is deasserted.
    public class SpiControllerSeHal : SimpleSPIController
    {
        public SpiControllerSeHal(IMachine machine, long pollingFrequency) : base(machine)
        {
            this.pollingFrequency = pollingFrequency;

            // frequency/limit = 1 tick per poll interval; Periodic self-reschedules each attempt.
            pollTimer = new LimitTimer(machine.ClockSource, (ulong)pollingFrequency, this, "sePollTimer",
                limit: 1, direction: Direction.Ascending, enabled: false,
                workMode: WorkMode.Periodic, eventEnabled: true);
            pollTimer.LimitReached += OnPollTick;
        }

        public override void Reset()
        {
            base.Reset();
            pollTimer.Reset();
            rxState = RxState.Idle;
            receiveInProgress = false;
            pollAttempts = 0;
            pollChipSelect = -1;
            body = null;
            bodyIndex = 0;
            lastReceivedBlock = new byte[0];
        }

        // Non-blocking. Performs the synchronous send phase (returned to the caller immediately), then
        // leaves chip select asserted and arms the poll timer. The response frame is polled, parsed, and
        // delivered later via the BlockReceived event / LastReceivedBlockHex property - driven by the
        // virtual clock, one byte per tick, so it completes only while the emulation is running and never
        // blocks it.
        public override byte[] Transfer(int chipSelect, byte[] data)
        {
            if(!TryGetTarget(chipSelect, out var target))
            {
                return new byte[0];
            }
            if(receiveInProgress)
            {
                this.Log(LogLevel.Warning, "Transfer requested while a receive is still in progress; ignoring");
                return new byte[0];
            }
            /* https://github.com/ThalesGroup/Thales_secure_element_hal/tree/master/secure_element/esehal/src */
            /* According to Thales HAL, the controller :    */
            /* 1. Send data                                 */
            /* 2. Poll the data until it receives the NAD   */
            /* 3. Parse the total length (payload + CRC)    */
            /* 4. Receives all the data and sends it        */

            // Step 1: send. Chip select stays asserted so the poll below is part of the same frame.
            Select(chipSelect);
            var sent = new byte[data.Length];
            for(var i = 0; i < data.Length; i++)
            {
                sent[i] = target.Transmit(data[i]);
            }

            // Steps 2-4 run one byte per tick on the clock-source thread.
            pollChipSelect = chipSelect;
            pollAttempts = 0;
            bodyIndex = 0;
            body = null;
            rxState = RxState.WaitingForNad;
            receiveInProgress = true;
            pollTimer.Enabled = true;

            return sent;
        }

        // Fired on the clock-source thread when a full response block has been received.
        public event Action<byte[]> BlockReceived;

        // True between a Transfer() call and delivery (or timeout) of its response block.
        public bool ReceiveInProgress => receiveInProgress;

        // Most recently received block, hex-encoded (monitor/robot readable).
        public string LastReceivedBlockHex => Misc.PrettyPrintCollectionHex(lastReceivedBlock);

        // One tick = one clocked byte = one step of the receive state machine. Never loops over the frame,
        // so the emulation advances between bytes and the SE gets time to produce the next one.
        private void OnPollTick()
        {
            if(!TryGetTarget(pollChipSelect, out var target))
            {
                FinishReceive(new byte[0]);
                return;
            }

            var incoming = target.Transmit(PollByte);
            switch(rxState)
            {
            case RxState.WaitingForNad:
                pollAttempts++;
                if(incoming == NotReadyByte)
                {
                    if(pollAttempts >= MaxPollAttempts)
                    {
                        this.Log(LogLevel.Warning, "SE did not respond after {0} poll attempts", pollAttempts);
                        FinishReceive(new byte[0]);
                    }
                    return; // still busy - try again next tick
                }
                nad = incoming;
                rxState = RxState.ReadingPcb;
                return;

            case RxState.ReadingPcb:
                pcb = incoming;
                rxState = RxState.ReadingLen;
                return;

            case RxState.ReadingLen:
                len = incoming; // len = payload + CRC, per the Thales framing above
                body = new byte[len];
                bodyIndex = 0;
                if(len == 0)
                {
                    FinishReceive(AssembleBlock());
                    return;
                }
                rxState = RxState.ReadingBody;
                return;

            case RxState.ReadingBody:
                body[bodyIndex++] = incoming;
                if(bodyIndex >= len)
                {
                    FinishReceive(AssembleBlock());
                }
                return;
            }
        }

        private byte[] AssembleBlock()
        {
            var block = new byte[3 + len];
            block[0] = nad;
            block[1] = pcb;
            block[2] = (byte)len;
            Array.Copy(body, 0, block, 3, len);
            return block;
        }

        private void FinishReceive(byte[] block)
        {
            pollTimer.Enabled = false;
            rxState = RxState.Idle;
            Deselect(pollChipSelect);
            lastReceivedBlock = block;
            receiveInProgress = false;
            this.Log(LogLevel.Debug, "Received block from SE: {0}", Misc.PrettyPrintCollectionHex(block));
            BlockReceived?.Invoke(block);
        }

        private const byte PollByte = 0x00;      // dummy byte clocked out while polling
        private const byte NotReadyByte = 0xFF;  // SE-not-ready sentinel on MISO — confirm against real HAL
        private const int MaxPollAttempts = 1000;

        private readonly LimitTimer pollTimer;
        private readonly long pollingFrequency;

        private RxState rxState = RxState.Idle;
        private bool receiveInProgress;
        private int pollChipSelect = -1;
        private int pollAttempts;
        private byte nad;
        private byte pcb;
        private int len;
        private byte[] body;
        private int bodyIndex;
        private byte[] lastReceivedBlock = new byte[0];

        private enum RxState
        {
            Idle,
            WaitingForNad,
            ReadingPcb,
            ReadingLen,
            ReadingBody,
        }
    }
}
