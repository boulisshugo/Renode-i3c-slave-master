//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.SWP;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A mock SWP target that drives back on S2 exactly what the CLF drove on S1 in the same
    // full-duplex slot. Because the transport is transparent, one Transfer by the CLF returns the
    // original bytes - handy for end-to-end data-integrity testing of the wire and the TCP bridge.
    public class EchoSWPDevice : SimpleSWPPeripheral
    {
        protected override byte[] OnTransfer(byte[] incoming)
        {
            return incoming;
        }
    }
}
