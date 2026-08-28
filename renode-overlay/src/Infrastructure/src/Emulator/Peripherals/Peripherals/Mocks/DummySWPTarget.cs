//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Peripherals.SWP;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A ready-to-use mock SWP target for testing a CLF and for quick wiring from the monitor.
    //
    // It is a plain SimpleSWPPeripheral - a transparent transport endpoint - plus introspection of
    // what it received and a monitor helper to drive S2 unprompted. It is the SWP analog of
    // DummyI3CSlave / DummySPITarget.
    public class DummySWPTarget : SimpleSWPPeripheral
    {
        public override void Reset()
        {
            base.Reset();
            received.Clear();
        }

        // Monitor-friendly helper: drive hex-encoded bytes on S2 without being polled. SWP is full
        // duplex, so the target does not need to be addressed first.
        public void SendDataHex(string hexData)
        {
            SendData(Misc.HexStringToByteArray(hexData));
        }

        // Number of non-empty blocks received since reset.
        public int ReceivedCount => received.Count;

        // Every byte received since reset, concatenated and hex-encoded.
        public string AllReceivedHex
        {
            get
            {
                var all = new List<byte>();
                foreach(var block in received)
                {
                    all.AddRange(block);
                }
                return Misc.PrettyPrintCollectionHex(all.ToArray());
            }
        }

        // Raised for every non-empty block the CLF drives on S1.
        public event Action<byte[]> DataReceived;

        protected override byte[] OnTransfer(byte[] incoming)
        {
            if(incoming.Length > 0)
            {
                received.Add(incoming);
                DataReceived?.Invoke(incoming);
            }
            return base.OnTransfer(incoming);
        }

        private readonly List<byte[]> received = new List<byte[]>();
    }
}
