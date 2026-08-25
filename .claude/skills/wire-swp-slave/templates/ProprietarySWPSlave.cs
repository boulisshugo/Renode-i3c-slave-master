//
// Copy-paste starting point for a proprietary SWP (ETSI TS 102 613) UICC model.
//
// Drop it in renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/ and refer to
// it from a .repl as `SWP.MyProprietarySWPSlave @ swp 0`.
//
using System.Collections.Generic;

using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SWP
{
    public class MyProprietarySWPSlave : SimpleSWPPeripheral
    {
        public MyProprietarySWPSlave()
        {
            // The ACT_INFORMATION the UICC advertises in its ACT_SYNC frame. Set the maximum frame
            // payload to what the real silicon accepts: the CLF reads it and refuses to send more.
            MaxFramePayloadSize = 254;
            MaxWindowSize = 4;
        }

        // Anything Reset() touches must be a field initializer - the base constructor calls the
        // virtual Reset() before this class's constructor body runs.
        public override void Reset()
        {
            base.Reset();
            registers.Clear();
        }

        // The SHDLC link is up (RSET/UA done). Load whatever the application layer needs.
        protected override void OnLinkEstablished()
        {
            this.Log(LogLevel.Info, "SHDLC link established with the CLF");
        }

        // The CLF drove S1 low - the interface is unpowered and keeps no state.
        protected override void OnDeactivated()
        {
            registers.Clear();
        }

        // One well-sequenced I-frame arrived. Return the payload to answer with (it rides an I-frame
        // that also carries the acknowledgement), or null to answer with a bare RR.
        protected override byte[] OnInformation(byte[] payload)
        {
            if(payload.Length < 2)
            {
                return null;
            }

            // Toy example: [0x01, reg] reads a register, [0x02, reg, value] writes one.
            switch(payload[0])
            {
            case 0x01:
                return new byte[] { registers.TryGetValue(payload[1], out var value) ? value : (byte)0 };
            case 0x02 when payload.Length >= 3:
                registers[payload[1]] = payload[2];
                return null;
            default:
                this.Log(LogLevel.Warning, "Unknown command 0x{0:X2}", payload[0]);
                return null;
            }
        }

        // Call this from anywhere (a timer, a register write from firmware, a sensor model) to push
        // data to the CLF without being polled. SWP is full duplex, so the UICC may transmit on S2 at
        // any time; the controller decodes the frame and raises its IRQ line.
        public void NotifyEvent(byte code)
        {
            SendInformation(new byte[] { 0xF0, code });
        }

        private readonly Dictionary<byte, byte> registers = new Dictionary<byte, byte>();
    }
}
