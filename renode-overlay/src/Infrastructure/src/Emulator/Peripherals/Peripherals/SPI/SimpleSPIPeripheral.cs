//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SPI
{
    // A simple, agnostic SPI target (slave) implementation built on Renode's ISPIPeripheral.
    //
    // SPI is full-duplex: every clocked byte exchanges one byte in each direction. Each Transmit
    // delivers one MOSI byte to the target and returns one MISO byte to the controller. On its own
    // this class returns bytes queued with EnqueueResponseBytes (zeros once empty); subclass it and
    // override OnTransfer for proprietary behaviour, and call RequestInterrupt to raise the target's
    // data-ready / interrupt line towards the controller (the SPI side-band analog of an I3C IBI).
    public class SimpleSPIPeripheral : ISelectableSPIPeripheral
    {
        public SimpleSPIPeripheral()
        {
            Reset();
        }

        public virtual void Reset()
        {
            responseQueue.Clear();
        }

        public byte Transmit(byte data)
        {
            var outgoing = OnTransfer(data);
            this.Log(LogLevel.Noisy, "SPI transfer: MOSI 0x{0:X2} -> MISO 0x{1:X2}", data, outgoing);
            return outgoing;
        }

        public void FinishTransmission()
        {
            this.Log(LogLevel.Noisy, "Chip select deasserted (transmission finished)");
            OnFinishTransmission();
        }

        // Implements ISelectableSPIPeripheral: the controller calls this on chip-select assert/deassert.
        public void Select(bool select)
        {
            this.Log(LogLevel.Noisy, "Chip select {0}", select ? "asserted" : "deasserted");
            OnSelect(select);
        }

        // Queues a byte to be shifted out (MISO) on subsequent transfers.
        public void EnqueueResponseByte(byte value)
        {
            responseQueue.Enqueue(value);
        }

        // Queues bytes to be shifted out (MISO) on subsequent transfers.
        public void EnqueueResponseBytes(IEnumerable<byte> bytes)
        {
            foreach(var b in bytes)
            {
                responseQueue.Enqueue(b);
            }
        }

        // Monitor-friendly helper: queue MISO bytes from a hex string, e.g. "0102ab".
        public void EnqueueResponseBytesHex(string hexBytes)
        {
            EnqueueResponseBytes(Misc.HexStringToByteArray(hexBytes));
        }

        // Raised when the target asserts its data-ready / interrupt line. The payload carries the bytes
        // the target wants to hand back to the controller (may be empty for a bare signal).
        public event Action<ISPIPeripheral, byte[]> InterruptRequested;

        // Called for every clocked byte. Return the MISO byte for this transfer. Default: shift out the
        // next queued response byte, or 0 when the queue is empty.
        protected virtual byte OnTransfer(byte incoming)
        {
            return responseQueue.Count > 0 ? responseQueue.Dequeue() : (byte)0;
        }

        // Called when the controller deasserts chip select. Default: no-op.
        protected virtual void OnFinishTransmission()
        {
        }

        // Called on chip-select assert (true) / deassert (false). Default: no-op. Override to frame a
        // transaction (e.g. distinguish a command phase from a polled response phase).
        protected virtual void OnSelect(bool select)
        {
        }

        // Asserts the target's interrupt line, handing the given bytes to the controller.
        protected void RequestInterrupt(byte[] data = null)
        {
            var payload = data ?? new byte[0];
            this.Log(LogLevel.Noisy, "Requesting interrupt with {0} data byte(s)", payload.Length);
            InterruptRequested?.Invoke(this, payload);
        }

        private readonly Queue<byte> responseQueue = new Queue<byte>();
    }
}
