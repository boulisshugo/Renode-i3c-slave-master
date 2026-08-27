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
    // A ready-to-use mock SWP (UICC) target for testing a CLF and for quick wiring from the monitor.
    //
    // It is a plain SoftwareSWPTarget - so the ACT activation sequence and SHDLC come from the
    // host-side stack, there being no firmware in a mock to run them - plus introspection of what it
    // received and monitor-friendly helpers to answer and to transmit on its own initiative. It is
    // the SWP analog of DummyI3CSlave / DummySPITarget.
    public class DummySWPTarget : SoftwareSWPTarget
    {
        public override void Reset()
        {
            base.Reset();
            received.Clear();
        }

        // Monitor-friendly helper: transmit an unsolicited I-frame carrying hex-encoded data. SWP is
        // full duplex, so the UICC does not need to be polled first - this is the SWP equivalent of
        // an I3C In-Band Interrupt or an SPI data-ready line.
        public void RequestServiceWithData(string hexData)
        {
            SendInformation(Misc.HexStringToByteArray(hexData));
        }

        // Number of I-frame payloads received since reset.
        public int ReceivedCount => received.Count;

        // All I-frame payloads received since reset, concatenated and hex-encoded.
        public string AllReceivedHex
        {
            get
            {
                var all = new List<byte>();
                foreach(var item in received)
                {
                    all.AddRange(item);
                }
                return Misc.PrettyPrintCollectionHex(all.ToArray());
            }
        }

        // Raised for every I-frame payload delivered to the target.
        public event Action<byte[]> InformationReceived;

        protected override byte[] OnInformation(byte[] information)
        {
            received.Add(information);
            InformationReceived?.Invoke(information);
            return base.OnInformation(information);
        }

        private readonly List<byte[]> received = new List<byte[]>();
    }
}
