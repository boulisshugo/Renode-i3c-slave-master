//
// Copyright (c) 2026 <your-org>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
// Template: a proprietary I3C slave built on this repo's SimpleI3CPeripheral.
//
// Place under:
//   renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/I3C/MyProprietaryI3CSlave.cs
// so its .repl prefix is "I3C." (matching the namespace tail). The Infrastructure csproj globs sources,
// so no project edits are needed.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.I3C
{
    public class MyProprietaryI3CSlave : SimpleI3CPeripheral
    {
        public MyProprietaryI3CSlave(ulong provisionedId = 0, byte busCharacteristics = 0x02,
            byte deviceCharacteristics = 0, byte staticAddress = 0)
            : base(provisionedId, busCharacteristics, deviceCharacteristics, staticAddress)
        {
            // Only assign fields here that Reset() does NOT touch. Anything Reset() uses must be a
            // field initializer (see registers below), because the base constructor calls Reset().
        }

        // NOTE: field initializer, not constructor-body assignment - the base ctor calls Reset().
        private readonly Dictionary<byte, byte> registers = new Dictionary<byte, byte>();

        public override void Reset()
        {
            base.Reset();          // clears the response buffer and the dynamic address
            registers.Clear();
            selectedRegister = 0;
        }

        // Master -> slave (SDR private write). Here: [regAddr, value, value, ...] register writes.
        protected override void OnWritePrivate(byte[] data)
        {
            this.Log(LogLevel.Debug, "Private write: {0}", Misc.PrettyPrintCollectionHex(data));
            if(data.Length == 0)
            {
                return;
            }
            selectedRegister = data[0];
            for(var i = 1; i < data.Length; i++)
            {
                registers[(byte)(selectedRegister + i - 1)] = data[i];
            }
        }

        // slave -> master (SDR private read). Return your device's data; short arrays are fine.
        protected override byte[] OnReadPrivate(int count)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                registers.TryGetValue((byte)(selectedRegister + i), out result[i]);
            }
            return result;
        }

        // Broadcast or direct Common Command Code.
        protected override void OnCommonCommandCode(byte code, byte[] payload)
        {
            this.Log(LogLevel.Debug, "CCC 0x{0:X2} ({1} payload bytes)", code, payload.Length);
            // Handle the CCCs your device cares about (ENEC/DISEC, SETMWL, vendor codes, ...).
        }

        // Call this to raise an In-Band Interrupt towards the master (e.g. "data ready").
        // The payload delivered to the master is [mdb, data...]; the TCP bridge in forward-on-interrupt
        // mode strips the MDB and forwards the rest.
        public void NotifyMaster(byte mandatoryDataByte, byte[] data = null)
        {
            RequestInBandInterrupt(mandatoryDataByte, data);
        }

        private byte selectedRegister;
    }
}
