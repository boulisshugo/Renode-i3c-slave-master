//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SPI
{
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
            receiveInProgress = false;
            pollAttempts = 0;
            pollChipSelect = -1;
            lastReceivedBlock = new byte[0];
        }

        // Non-blocking. Performs the synchronous send phase (returned to the caller immediately),
        // then leaves chip select asserted and arms the poll timer. The polled response block is
        // delivered later via the BlockReceived event / LastReceivedBlockHex property once the SE
        // responds — driven by the virtual clock, so it completes only while the emulation is running.
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

            // Steps 2-4 run asynchronously on the clock-source thread.
            receiveInProgress = true;
            pollChipSelect = chipSelect;
            pollAttempts = 0;
            pollTimer.Enabled = true;

            return sent;
        }

        // Fired on the clock-source thread when a full response block has been received.
        public event Action<byte[]> BlockReceived;

        // True between a Transfer() call and delivery (or timeout) of its response block.
        public bool ReceiveInProgress => receiveInProgress;

        // Most recently received block, hex-encoded (monitor/robot readable).
        public string LastReceivedBlockHex => Misc.PrettyPrintCollectionHex(lastReceivedBlock);

        private void OnPollTick()
        {
            if(!TryGetTarget(pollChipSelect, out var target))
            {
                FinishReceive(new byte[0]);
                return;
            }

            pollAttempts++;
            var nad = target.Transmit(PollByte);
            if(nad == NotReadyByte)
            {
                if(pollAttempts >= MaxPollAttempts)
                {
                    this.Log(LogLevel.Warning, "SE did not respond after {0} poll attempts", pollAttempts);
                    FinishReceive(new byte[0]);
                }
                return; // Periodic mode auto-schedules the next tick.
            }

            var pcb = target.Transmit(PollByte);
            var len = target.Transmit(PollByte);
            var block = new byte[3 + len];
            block[0] = nad;
            block[1] = pcb;
            block[2] = len; // len = payload + CRC, per the Thales framing above
            for(var i = 0; i < len; i++)
            {
                block[3 + i] = target.Transmit(PollByte);
            }
            FinishReceive(block);
        }

        private void FinishReceive(byte[] block)
        {
            pollTimer.Enabled = false;
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

        private bool receiveInProgress;
        private int pollChipSelect = -1;
        private int pollAttempts;
        private byte[] lastReceivedBlock = new byte[0];
    }
}
