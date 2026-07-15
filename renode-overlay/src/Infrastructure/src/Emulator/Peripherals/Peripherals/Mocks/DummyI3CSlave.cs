//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Peripherals.I3C;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A ready-to-use mock I3C target for testing controllers and for quick wiring from the monitor.
    //
    // It is the I3C analog of DummyI2CSlave: it records the last private write and the last CCC,
    // exposes events for more elaborate mocking, and offers monitor-friendly helpers to enqueue
    // read responses and to trigger In-Band Interrupts.
    public class DummyI3CSlave : SimpleI3CPeripheral
    {
        public DummyI3CSlave(ulong provisionedId = 0, byte busCharacteristics = 0,
            byte deviceCharacteristics = 0, byte staticAddress = 0)
            : base(provisionedId, busCharacteristics, deviceCharacteristics, staticAddress)
        {
        }

        public override void Reset()
        {
            base.Reset();
            LastCommandCode = -1;
            lastReceived = new byte[0];
        }

        // Monitor-friendly helper: raises an IBI carrying just the Mandatory Data Byte.
        public void RequestInBandInterrupt(int mandatoryDataByte)
        {
            RequestInBandInterrupt((byte)mandatoryDataByte, null);
        }

        // Monitor-friendly helper: raises an IBI with the MDB followed by hex-encoded data bytes.
        // Named distinctly from the single-argument overload so the monitor never binds a bare
        // `RequestInBandInterrupt 0xAB` to this method with a null hex string.
        public void RequestInBandInterruptWithData(int mandatoryDataByte, string hexData)
        {
            RequestInBandInterrupt((byte)mandatoryDataByte, Misc.HexStringToByteArray(hexData));
        }

        // Last code received via a CCC, or -1 if none since the last reset.
        public int LastCommandCode { get; private set; } = -1;

        // Last bytes received via a private write, hex-encoded (monitor-readable).
        public string LastReceivedHex => Misc.PrettyPrintCollectionHex(lastReceived);

        // Raised on every private write, carrying the received bytes.
        public event Action<byte[]> DataReceived;

        // Raised on every private read, carrying the number of bytes requested (fired before the
        // response buffer is consumed, so a handler may enqueue data on demand).
        public event Action<int> ReadRequested;

        // Raised on every received CCC.
        public event Action<byte, byte[]> CommandCodeReceived;

        protected override void OnWritePrivate(byte[] data)
        {
            lastReceived = data;
            DataReceived?.Invoke(data);
        }

        protected override byte[] OnReadPrivate(int count)
        {
            ReadRequested?.Invoke(count);
            return base.OnReadPrivate(count);
        }

        protected override void OnCommonCommandCode(byte code, byte[] payload)
        {
            LastCommandCode = code;
            CommandCodeReceived?.Invoke(code, payload);
        }

        private byte[] lastReceived = new byte[0];
    }
}
