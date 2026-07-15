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

namespace Antmicro.Renode.Peripherals.I3C
{
    // A simple, agnostic I3C target (slave) implementation.
    //
    // On its own it behaves as a buffer-backed device: bytes queued with EnqueueResponseBytes are
    // returned on private reads, private writes are ignored (but logged), and CCCs are only logged.
    //
    // It is meant to be subclassed to wire proprietary target logic: override OnWritePrivate,
    // OnReadPrivate, OnCommonCommandCode and OnFinishTransmission, and call RequestInBandInterrupt
    // to raise an IBI towards the controller.
    public class SimpleI3CPeripheral : II3CPeripheral
    {
        public SimpleI3CPeripheral(ulong provisionedId = 0, byte busCharacteristics = 0,
            byte deviceCharacteristics = 0, byte staticAddress = 0)
        {
            ProvisionedId = provisionedId;
            BusCharacteristics = busCharacteristics;
            DeviceCharacteristics = deviceCharacteristics;
            StaticAddress = staticAddress;
            responseQueue = new Queue<byte>();
            Reset();
        }

        public virtual void Reset()
        {
            responseQueue.Clear();
            DynamicAddress = 0;
        }

        public void Write(byte[] data)
        {
            this.Log(LogLevel.Debug, "Private write of {0} bytes: {1}", data.Length, Misc.PrettyPrintCollectionHex(data));
            OnWritePrivate(data);
        }

        public byte[] Read(int count = 1)
        {
            var result = OnReadPrivate(count);
            this.Log(LogLevel.Debug, "Private read of {0} bytes: {1}", result.Length, Misc.PrettyPrintCollectionHex(result));
            return result;
        }

        public void FinishTransmission()
        {
            this.Log(LogLevel.Noisy, "Transmission finished");
            OnFinishTransmission();
        }

        public void HandleCommonCommandCode(byte code, byte[] payload)
        {
            this.Log(LogLevel.Debug, "Received CCC 0x{0:X2} with {1} payload byte(s)", code, payload?.Length ?? 0);
            OnCommonCommandCode(code, payload ?? new byte[0]);
        }

        // Queues a single byte to be returned on subsequent private reads.
        public void EnqueueResponseByte(byte value)
        {
            responseQueue.Enqueue(value);
        }

        // Queues bytes to be returned on subsequent private reads.
        public void EnqueueResponseBytes(IEnumerable<byte> bytes)
        {
            foreach(var b in bytes)
            {
                responseQueue.Enqueue(b);
            }
        }

        // Monitor-friendly helper: queues bytes given as a hex string, e.g. "0102ab".
        public void EnqueueResponseBytesHex(string hexBytes)
        {
            EnqueueResponseBytes(Misc.HexStringToByteArray(hexBytes));
        }

        public ulong ProvisionedId { get; }
        public byte BusCharacteristics { get; }
        public byte DeviceCharacteristics { get; }
        public byte StaticAddress { get; }
        public byte DynamicAddress { get; set; }

        public event Action<II3CPeripheral, byte[]> InBandInterruptRequested;

        // Called on a private write from the controller. Default: no-op.
        protected virtual void OnWritePrivate(byte[] data)
        {
        }

        // Called on a private read from the controller. Default: dequeue from the response buffer,
        // padding with zeros when the buffer runs dry (mirrors DummyI2CSlave behaviour).
        protected virtual byte[] OnReadPrivate(int count)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                result[i] = responseQueue.Count > 0 ? responseQueue.Dequeue() : (byte)0;
            }
            return result;
        }

        // Called when the target receives a CCC. Default: no-op.
        protected virtual void OnCommonCommandCode(byte code, byte[] payload)
        {
        }

        // Called at the end of a transfer. Default: no-op.
        protected virtual void OnFinishTransmission()
        {
        }

        // Raises an In-Band Interrupt towards the controller. The MDB is the Mandatory Data Byte,
        // data holds any optional trailing IBI bytes.
        protected void RequestInBandInterrupt(byte mandatoryDataByte, byte[] data = null)
        {
            var payload = new byte[1 + (data?.Length ?? 0)];
            payload[0] = mandatoryDataByte;
            data?.CopyTo(payload, 1);
            this.Log(LogLevel.Noisy, "Requesting IBI, MDB 0x{0:X2}", mandatoryDataByte);
            InBandInterruptRequested?.Invoke(this, payload);
        }

        private readonly Queue<byte> responseQueue;
    }
}
