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
    // SWP is a point-to-point, full-duplex link over one wire: the CLF (master) drives S1 in the
    // voltage domain, the UICC (slave) answers on S2 in the current domain, both at the same time.
    // The CLF is the only side that can power the interface up or down; once the interface is up the
    // UICC may transmit on S2 whenever it has something to say.
    //
    // This contract sits at the DATA LINK LAYER (clause 8): every method exchanges a complete SWP
    // *wire frame* - SOF, bit-stuffed payload and CRC, EOF, bit-packed MSB first, exactly as
    // SWPFrame.Encode produces it. The S1/S2 bit modulation below that is abstracted away; everything
    // above it (framing, CRC, the ACT activation LLC and SHDLC) is real. See SWPFrame and
    // SWPProtocol for the encodings.
    //
    // WHERE THE PROTOCOL LIVES
    //
    // This interface is the wire, not the protocol. SimpleSWPPeripheral implements it as a
    // transceiver and answers nothing on its own, because on the targets modelled here the ACT and
    // SHDLC layers are the chip's FIRMWARE. Subclass one of the two models rather than implementing
    // this interface directly, then register on a SimpleSWPController:
    //
    //   - InventedSWPTarget - firmware in the loop: received payloads go to the emulated CPU through
    //     a register window and every answer is one the firmware built;
    //   - SoftwareSWPTarget - SimpleSWPPeripheral plus SWPTargetStack, a host-side implementation of
    //     ACT and SHDLC, for mocks and benches where no firmware runs.
    public interface ISWPPeripheral : IPeripheral
    {
        // Current interface state as seen by the target (clause 6 activation state machine).
        SWPInterfaceState InterfaceState { get; }

        // The CLF starts driving S1: the contact is powered. A target that already has something to
        // say returns it here as this slot's S2 traffic; an empty array means it stayed silent,
        // which is the normal case for a firmware-managed target - its firmware has not run yet, and
        // its ACT_SYNC will arrive later through FrameAvailable. Silence here is therefore not a
        // failed activation.
        byte[] Activate();

        // The CLF drives S1 low: the interface returns to DEACTIVATED and all link state is dropped.
        void Deactivate();

        // One full-duplex frame slot on the wire: the CLF transmits wireFrame on S1 and the UICC
        // transmits whatever it has ready on S2. Returns the target's wire frame, or an empty array
        // if it had nothing to send in this slot - which is the usual answer, since the reply to
        // this frame generally does not exist yet. It arrives later through FrameAvailable.
        byte[] ExchangeFrame(byte[] wireFrame);

        // Raised when the target transmits a frame on S2 on its own initiative - an ACT frame its
        // firmware has just built, an answer to something sent earlier, or an unsolicited SHDLC
        // I-frame (the SWP equivalent of a device-initiated interrupt). SWP is full duplex, so this
        // is the main way a target talks, not an exception. The argument is a complete wire frame.
        event Action<ISWPPeripheral, byte[]> FrameAvailable;
    }

    // SWP interface states of the activation / deactivation sequence (ETSI TS 102 613 clause 6).
    public enum SWPInterfaceState
    {
        // S1 is low: the interface is unpowered and no state is retained.
        Deactivated,
        // S1 is being driven. On the target side this is also the state a bare transport reports for
        // a powered contact, since it has no protocol layer to refine it any further.
        ActSync,
        // The CLF has answered with ACT_POWER_MODE, selecting low or full power mode.
        ActPowerMode,
        // The UICC has acknowledged the power mode with ACT_READY.
        ActReady,
        // Activation is complete; SHDLC (or CLT) frames may be exchanged.
        Activated,
    }

    // Power mode selected by the CLF in the ACT_POWER_MODE frame (clause 6).
    public enum SWPPowerMode
    {
        LowPower = 0,
        FullPower = 1,
    }
}
