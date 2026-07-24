//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A ready-to-use mock SPI target for testing controllers and quick wiring from the monitor.
    //
    // It records the bytes received during the last chip-select transaction, exposes an event, and
    // offers monitor-friendly helpers to queue MISO responses and to assert the interrupt line. It is
    // the SPI analog of DummyI3CSlave. (Renode also ships a minimal Mocks.DummySPISlave; this one adds
    // received-data introspection and the interrupt helpers used by the tests here.)
    public class DummySPITarget : SimpleSPIPeripheral
    {
        public override void Reset()
        {
            base.Reset();
            current.Clear();
            lastReceived = new byte[0];
        }

        // Monitor-friendly helper: assert the interrupt line with no data.
        public void RequestInterrupt()
        {
            RequestInterrupt((byte[])null);
        }

        // Monitor-friendly helper: assert the interrupt line carrying hex-encoded data.
        public void RequestInterruptWithData(string hexData)
        {
            RequestInterrupt(Misc.HexStringToByteArray(hexData));
        }

        // Bytes received (MOSI) during the last completed transaction, hex-encoded (monitor-readable).
        public string LastReceivedHex => Misc.PrettyPrintCollectionHex(lastReceived);

        // Raised for every clocked byte, carrying the received MOSI byte.
        public event Action<byte> DataReceived;

        protected override byte OnTransfer(byte incoming)
        {
            current.Add(incoming);
            DataReceived?.Invoke(incoming);
            return base.OnTransfer(incoming);
        }

        protected override void OnFinishTransmission()
        {
            lastReceived = current.ToArray();
            current.Clear();
        }

        private readonly List<byte> current = new List<byte>();
        private byte[] lastReceived = new byte[0];
    }
}
