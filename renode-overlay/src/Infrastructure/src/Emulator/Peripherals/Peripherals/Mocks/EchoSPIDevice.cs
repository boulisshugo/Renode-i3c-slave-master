//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A mock SPI target that loops each MOSI byte straight back onto MISO. Because SPI is full-duplex,
    // a transfer of N bytes returns those same N bytes - handy for end-to-end data-integrity and
    // consistency testing of a controller or the TCP bridge.
    public class EchoSPIDevice : SimpleSPIPeripheral
    {
        protected override byte OnTransfer(byte incoming)
        {
            return incoming;
        }
    }
}
