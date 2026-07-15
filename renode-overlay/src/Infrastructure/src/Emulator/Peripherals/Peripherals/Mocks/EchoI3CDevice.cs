//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.I3C;

namespace Antmicro.Renode.Peripherals.Mocks
{
    // A mock I3C target that echoes back the bytes of the most recent private write on the next
    // private read. Useful for end-to-end data-integrity/consistency testing of a controller or the
    // TCP bridge (write N bytes, read N bytes, compare).
    public class EchoI3CDevice : SimpleI3CPeripheral
    {
        public EchoI3CDevice(ulong provisionedId = 0, byte busCharacteristics = 0,
            byte deviceCharacteristics = 0, byte staticAddress = 0)
            : base(provisionedId, busCharacteristics, deviceCharacteristics, staticAddress)
        {
        }

        public override void Reset()
        {
            base.Reset();
            buffer = new byte[0];
        }

        protected override void OnWritePrivate(byte[] data)
        {
            buffer = data;
        }

        protected override byte[] OnReadPrivate(int count)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                result[i] = i < buffer.Length ? buffer[i] : (byte)0;
            }
            return result;
        }

        private byte[] buffer = new byte[0];
    }
}
