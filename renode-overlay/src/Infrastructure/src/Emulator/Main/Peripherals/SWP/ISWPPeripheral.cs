//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

namespace Antmicro.Renode.Peripherals.SWP
{
    // Single Wire Protocol (ETSI TS 102 613) target contract - the UICC side of a CLF <-> UICC link.
    //
    // This is a TRANSPORT, not a protocol stack. SWP is a point-to-point, full-duplex link over one
    // wire: the CLF (master) drives S1 in the voltage domain, the UICC (slave) answers on S2 in the
    // current domain, both at the same time, and only the CLF can power the interface up or down.
    // Those are the properties modelled here - carrying bytes in both directions, and the power
    // state that gates them.
    //
    // Everything above the wire - the frame delimiting and CRC of clause 8, the ACT activation
    // sequence of clause 11, SHDLC of clause 10 - is deliberately NOT implemented. Those layers
    // belong to whatever is under test: a proprietary UICC model, CPU firmware, or an external
    // client on the far end of the TCP bridge. A model that ran its own ACT/SHDLC would be talking
    // to that stack instead of carrying it, which is exactly what a transport must not do.
    // tools/swp-reference/ has a standalone implementation of those layers if a test-bench or client
    // wants one.
    //
    // Proprietary UICC models should subclass SimpleSWPPeripheral and override OnTransfer, then
    // register on a SimpleSWPController.
    public interface ISWPPeripheral : IPeripheral
    {
        // True while the CLF is driving S1. Nothing crosses the wire when it is false.
        bool Powered { get; }

        // The CLF powers the interface up or drives S1 low. Physical layer only: no bytes are
        // exchanged here, and the target drops whatever per-session state it holds when unpowered.
        void SetPower(bool powered);

        // One full-duplex slot on the wire: the bytes the CLF drives on S1, returning the bytes the
        // UICC drove on S2 in the same slot. Either side may be empty - the link is full duplex, so
        // the two directions are independent. The bytes are opaque; no framing is added or removed.
        byte[] Transfer(byte[] data);

        // Raised when the target drives bytes on S2 on its own initiative, without the CLF having
        // sent anything. SWP is full duplex, so a UICC does not have to wait to be polled.
        event Action<ISWPPeripheral, byte[]> DataAvailable;
    }
}
