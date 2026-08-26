//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.SWP;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A mock SWP (UICC) target that answers every SHDLC I-frame with an I-frame carrying the same
    // payload back. Because the answer is piggybacked on the very frame that acknowledges the
    // request, one Send by the CLF returns the original bytes - handy for end-to-end data-integrity
    // and consistency testing of the framing, the CRC and the sequencing.
    public class EchoSWPDevice : SimpleSWPPeripheral
    {
        protected override byte[] OnInformation(byte[] payload)
        {
            return payload;
        }
    }
}
