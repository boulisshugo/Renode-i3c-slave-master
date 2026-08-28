//
// Copy-paste starting point for a proprietary SWP (ETSI TS 102 613) target.
//
// Drop it in renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/ and refer to
// it from a .repl as `SWP.MyProprietarySWPSlave @ swp` - no line index, SWP is point to point.
//
// Remember what the model is: a TRANSPORT. The bytes handed to OnTransfer are exactly what the peer
// drove on the wire - no framing, CRC or protocol byte has been removed - and whatever you return is
// driven back untouched. Your protocol stack lives here. tools/swp-reference/ has the ETSI framing,
// ACT and SHDLC as a standalone library if you want to borrow one.
//
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SWP
{
    public class MyProprietarySWPSlave : SimpleSWPPeripheral
    {
        // Anything Reset() touches must be a field initializer - the base constructor calls the
        // virtual Reset() before this class's constructor body runs.
        public override void Reset()
        {
            base.Reset();
            rxBuffer.Clear();
            txBuffer.Clear();
        }

        // The CLF powered the line up (true) or drove S1 low (false). Reset your stack here: an
        // unpowered SWP interface keeps no state.
        protected override void OnPowerChanged(bool powered)
        {
            if(!powered)
            {
                rxBuffer.Clear();
                txBuffer.Clear();
                this.Log(LogLevel.Debug, "Line unpowered - protocol state dropped");
            }
        }

        // One full-duplex slot. `incoming` is the raw bytes the CLF drove on S1 (possibly empty);
        // return the raw bytes this target drives on S2 in the same slot, or null for nothing.
        //
        // SWP is a bit-serial wire: do NOT assume the peer's frames align with the blocks you get
        // here. Buffer and re-frame, exactly as you would on real hardware.
        protected override byte[] OnTransfer(byte[] incoming)
        {
            rxBuffer.AddRange(incoming);

            // Toy example: a frame is a length byte followed by that many payload bytes.
            while(rxBuffer.Count >= 1 && rxBuffer.Count >= rxBuffer[0] + 1)
            {
                var length = rxBuffer[0];
                var frame = rxBuffer.Skip(1).Take(length).ToArray();
                rxBuffer.RemoveRange(0, length + 1);
                HandleFrame(frame);
            }

            if(txBuffer.Count == 0)
            {
                return null;
            }
            var outgoing = txBuffer.ToArray();
            txBuffer.Clear();
            return outgoing;
        }

        private void HandleFrame(byte[] frame)
        {
            // ... your application logic; queue the answer for the next slot.
            txBuffer.Add((byte)2);
            txBuffer.Add(0x90);
            txBuffer.Add(0x00);
        }

        // Call this from anywhere (a timer, a register write from firmware, a sensor model) to drive
        // bytes on S2 without being polled. SWP is full duplex, so the target may transmit at any
        // time; the controller raises its IRQ line.
        public void NotifyEvent(byte code)
        {
            SendData(new byte[] { 2, 0xF0, code });
        }

        private readonly List<byte> rxBuffer = new List<byte>();
        private readonly List<byte> txBuffer = new List<byte>();
    }
}
