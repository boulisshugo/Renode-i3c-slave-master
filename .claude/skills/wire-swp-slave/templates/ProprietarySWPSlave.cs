//
// Copy-paste starting point for a proprietary SWP (ETSI TS 102 613) UICC model.
//
// Drop it in renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/ and refer to
// it from a .repl as `SWP.MyProprietarySWPSlave @ swp 0`.
//
// WHICH BASE CLASS: this template extends SoftwareSWPTarget, which is SimpleSWPPeripheral (the SWP
// transceiver: framing, CRC, S1/S2) plus SWPTargetStack, a HOST-SIDE implementation of the ACT and
// SHDLC layers. Use it when there is no firmware in the simulation to run those layers.
//
// If your UICC is driven by firmware on a simulated CPU, this is the wrong base: use
// InventedSWPTarget instead and write the protocol in the firmware, as firmware-swp/main.c does. On
// real silicon ACT and SHDLC are firmware, and a model that answers the CLF by itself hides exactly
// the firmware bugs you are simulating to find. SimpleSWPPeripheral on its own therefore answers
// nothing at all - which base you pick is the decision about who does.
//
using System.Collections.Generic;

using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SWP
{
    public class MyProprietarySWPSlave : SoftwareSWPTarget
    {
        public MyProprietarySWPSlave()
        {
            // The ACT_INFORMATION the UICC advertises in its ACT_SYNC frame. Set the maximum frame
            // payload to what the real silicon accepts: the CLF reads it and refuses to send more.
            MaxFramePayloadSize = 254;
            MaxWindowSize = 4;
        }

        // Anything Reset() touches must be a field initializer - the base constructor calls the
        // virtual Reset(), which runs before this class's constructor body.
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

        // One well-sequenced I-frame arrived. `information` is the application bytes only - the SHDLC
        // control field, the CRC and the flags have already been taken off. Return the payload to
        // answer with (it rides an I-frame that also carries the acknowledgement), or null to answer
        // with a bare RR.
        protected override byte[] OnInformation(byte[] information)
        {
            if(information.Length < 2)
            {
                return null;
            }

            // Toy example: [0x01, reg] reads a register, [0x02, reg, value] writes one.
            switch(information[0])
            {
            case 0x01:
                return new byte[] { registers.TryGetValue(information[1], out var value) ? value : (byte)0 };
            case 0x02 when information.Length >= 3:
                registers[information[1]] = information[2];
                return null;
            default:
                this.Log(LogLevel.Warning, "Unknown command 0x{0:X2}", information[0]);
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
