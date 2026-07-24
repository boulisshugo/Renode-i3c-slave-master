//
// Copyright (c) 2026 <your-org>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
// Template: a proprietary SPI slave built on this repo's SimpleSPIPeripheral.
//
// Place under:
//   renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SPI/MyProprietarySPISlave.cs
// so its .repl prefix is "SPI." (matching the namespace tail). The Infrastructure csproj globs sources.
//
using System.Collections.Generic;

using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class MyProprietarySPISlave : SimpleSPIPeripheral
    {
        public override void Reset()
        {
            base.Reset();
            registers.Clear();
            selectedRegister = 0;
            byteIndex = 0;
        }

        // Full-duplex: called once per clocked byte. This example models a register device where the
        // first MOSI byte selects a register and following bytes write it; MISO returns the register.
        protected override byte OnTransfer(byte incoming)
        {
            byte outgoing;
            if(byteIndex == 0)
            {
                selectedRegister = incoming;
                registers.TryGetValue(selectedRegister, out outgoing);
            }
            else
            {
                registers.TryGetValue((byte)(selectedRegister + byteIndex - 1), out outgoing);
                registers[(byte)(selectedRegister + byteIndex - 1)] = incoming;
            }
            byteIndex++;
            this.Log(LogLevel.Noisy, "SPI byte #{0}: MOSI 0x{1:X2} -> MISO 0x{2:X2}", byteIndex - 1, incoming, outgoing);
            return outgoing;
        }

        // Chip select deasserted: the transaction is over, reset the byte counter.
        protected override void OnFinishTransmission()
        {
            byteIndex = 0;
        }

        // Call this to assert the target's data-ready / interrupt line towards the controller, handing
        // it the given bytes (forwarded to a TCP client by the bridge in forward-on-interrupt mode).
        public void NotifyMaster(byte[] data = null)
        {
            RequestInterrupt(data);
        }

        // NOTE: field initializer, not constructor-body assignment - the base ctor calls Reset().
        private readonly Dictionary<byte, byte> registers = new Dictionary<byte, byte>();
        private byte selectedRegister;
        private int byteIndex;
    }
}
