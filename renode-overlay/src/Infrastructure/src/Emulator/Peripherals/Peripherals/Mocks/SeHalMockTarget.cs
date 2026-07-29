//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A mock secure-element SPI target that models the Thales SE HAL framing exercised by
    // SpiControllerSeHal: within one held-chip-select frame it (1) absorbs a fixed-length command
    // (returning the 0xFF not-ready filler), (2) returns 0xFF for a configurable number of poll clocks
    // (SE busy), then (3) shifts out a preloaded response block [NAD, PCB, LEN, payload+CRC...] one byte
    // per subsequent clock. This lets a test drive the controller's send -> poll-until-NAD -> read-length
    // -> read-block logic without real firmware.
    public class SeHalMockTarget : SimpleSPIPeripheral
    {
        public override void Reset()
        {
            base.Reset();
            transfersSinceSelect = 0;
            responseCursor = 0;
            emitting = false;
            receivedCommand.Clear();
        }

        // Number of leading clocks in a frame that carry the command (the SE is receiving; MISO = 0xFF).
        public int CommandLength { get; set; }

        // Number of poll clocks answered with 0xFF (SE busy) before the response block starts.
        public int NotReadyPolls { get; set; }

        // Monitor helper: preload the response block the SE shifts out, e.g. "2100039000AB"
        // (NAD=21, PCB=00, LEN=03, then LEN bytes of payload+CRC).
        public void SetResponseBlockHex(string hex)
        {
            responseBlock = Misc.HexStringToByteArray(hex);
        }

        // The command bytes captured during the send phase of the last frame (monitor-readable).
        public string ReceivedCommandHex => Misc.PrettyPrintCollectionHex(receivedCommand.ToArray());

        protected override void OnSelect(bool select)
        {
            if(select)
            {
                transfersSinceSelect = 0;
                responseCursor = 0;
                emitting = false;
                receivedCommand.Clear();
            }
        }

        protected override byte OnTransfer(byte incoming)
        {
            var index = transfersSinceSelect++;

            // Command phase: absorb the command, answer with the not-ready filler.
            if(index < CommandLength)
            {
                receivedCommand.Add(incoming);
                return NotReadyByte;
            }

            // Poll phase.
            var pollIndex = index - CommandLength;
            if(!emitting)
            {
                if(pollIndex < NotReadyPolls)
                {
                    return NotReadyByte; // SE still busy
                }
                emitting = true; // first ready clock -> start shifting the block out below
            }

            if(responseCursor < responseBlock.Length)
            {
                return responseBlock[responseCursor++];
            }
            return NotReadyByte;
        }

        private const byte NotReadyByte = 0xFF;

        private int transfersSinceSelect;
        private int responseCursor;
        private bool emitting;
        private byte[] responseBlock = new byte[0];
        private readonly List<byte> receivedCommand = new List<byte>();
    }
}
