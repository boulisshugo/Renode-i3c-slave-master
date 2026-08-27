//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // A simple, agnostic SWP master - the CLF (Contactless Front-end) side of an ETSI TS 102 613
    // link. It is the counterpart of the target models and speaks the same three layers:
    //
    //   - data link layer (clause 8): every frame it sends is built by SWPFrame (SOF, bit-stuffed
    //     payload and CRC, EOF) and every frame it receives is decoded and CRC-checked;
    //   - ACT LLC (clause 11): Activate() powers S1 and then runs the activation sequence as the
    //     target's frames arrive - ACT_SYNC in, ACT_POWER_MODE out, ACT_READY in;
    //   - SHDLC LLC (clause 10): after activation it establishes the link with RSET/UA (negotiating
    //     the window size and SREJ support), then carries data in modulo-8 sequenced I-frames,
    //     acknowledging with RR and recovering a lost frame with REJ.
    //
    // THE TARGET ANSWERS WHEN IT IS READY, NOT WHEN IT IS ASKED
    //
    // SWP is full duplex and point to point: the UICC drives S2 whenever it has something to say. On
    // a target whose ACT and SHDLC layers are firmware (InventedSWPTarget), that is not a nicety but
    // the only possibility - the firmware does not even run until the receiving slot is over, so no
    // answer can ride the frame that asked for it.
    //
    // This controller is therefore EVENT-DRIVEN. Whether a frame comes back in the same slot
    // (ExchangeFrame) or later on the target's own initiative (FrameAvailable), it goes through the
    // same path and advances the same state machine. Which means:
    //
    //   - Activate(line) returns true only if the link came up within the call, which happens with a
    //     host-side stack (SoftwareSWPTarget) that answers in-slot. With a firmware-managed target it
    //     returns false and activation continues as the firmware answers: poll IsLinkEstablished(line)
    //     while the emulation runs, rather than treating false as a failure.
    //   - Send(line, ...) returns the answer only when it arrives in-slot. Otherwise it arrives later
    //     and is published through the PayloadReceived event, LastReceivedPayloadHex and the IRQ.
    //
    // Nothing here ever spins or sleeps: an exchange is a call into the target and a return, so the
    // CPU keeps running and simulation time keeps advancing between a question and its answer.
    //
    // ABOUT THE INTERFACE INDEX
    //
    // SWP is a single wire, point to point: nothing on the wire, in ACT or in SHDLC is addressed, and
    // the specification has no notion of a numbered line. The index below is Renode plumbing - this is
    // a SimpleContainer, and Renode registers bus children by NumberRegistrationPoint<int>, so a
    // number has to go in the `@ swp 0` slot of a .repl.
    //
    // It is loosely backed by hardware: a CLF chip commonly has more than one SWP contact (one to the
    // UICC, one to an embedded SE), and each is its own independent point-to-point interface. So the
    // index selects WHICH interface of this CLF, and means nothing beyond that.
    //
    //     swp:  SWP.SimpleSWPController @ sysbus
    //     uicc: SWP.SoftwareSWPTarget @ swp 0
    //     ese:  SWP.SoftwareSWPTarget @ swp 1
    //
    // The controller registers on the sysbus WITHOUT an address. The CLF is a separate chip on the far
    // end of the wire, not a block inside the SoC: it has no register map, so claiming an address range
    // would be fiction and would make the bus lie about what is actually memory-mapped. The monitor
    // still reaches it as `sysbus.<name>`.
    // Who runs the ACT and SHDLC layers on the CLF side of the link.
    public enum SWPProtocolOwner
    {
        // SimpleSWPController runs them: Activate() drives the whole sequence, Send() sequences
        // I-frames. Self-contained, and what a C# test bench wants.
        Controller,
        // Something outside the model runs them - a host stack, a driver, a TCP client - and this
        // controller is only the CLF's transceiver: it frames what it is given and hands back what
        // it receives, interpreting nothing.
        External,
    }

    public class SimpleSWPController : SimpleContainer<ISWPPeripheral>, INumberedGPIOOutput
    {
        public SimpleSWPController(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            Connections = new Dictionary<int, IGPIO> { { 0, IRQ } };
        }

        public override void Register(ISWPPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            base.Register(peripheral, registrationPoint);
            Action<ISWPPeripheral, byte[]> handler = HandleTargetFrame;
            frameHandlers[peripheral] = handler;
            peripheral.FrameAvailable += handler;
            links[registrationPoint.Address] = new Link();
        }

        public override void Unregister(ISWPPeripheral peripheral)
        {
            if(frameHandlers.TryGetValue(peripheral, out var handler))
            {
                peripheral.FrameAvailable -= handler;
                frameHandlers.Remove(peripheral);
            }
            foreach(var iface in ChildCollection.Where(x => ReferenceEquals(x.Value, peripheral)).Select(x => x.Key).ToArray())
            {
                links.Remove(iface);
            }
            base.Unregister(peripheral);
        }

        public override void Reset()
        {
            IRQ.Unset();
            LastReceivedInterface = -1;
            lastReceivedPayload = new byte[0];
            lastLpduIn = new byte[0];
            FramesSent = 0;
            FramesReceived = 0;
            CrcErrors = 0;
            RejectsReceived = 0;
            Retransmissions = 0;
            foreach(var iface in links.Keys.ToArray())
            {
                links[iface] = new Link();
            }
        }

        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // --------------------------------------------------------------------------------------
        // Activation and deactivation (ACT LLC)
        // --------------------------------------------------------------------------------------

        // Power mode the CLF requests in ACT_POWER_MODE. Settable from a .repl or the monitor.
        public SWPPowerMode PowerMode { get; set; } = SWPPowerMode.FullPower;

        // How many times the CLF re-sends ACT_POWER_MODE with FR = 1 before giving up on activation.
        // Only the in-slot answers of a host-side stack can be retried inside Activate(); with a
        // firmware-managed target, use RetryActivation(line) when the firmware has gone quiet.
        public int ActivationRetries { get; set; } = 3;

        // SHDLC window size the CLF proposes in RSET.
        public int WindowSize { get; set; } = SWPProtocol.DefaultWindowSize;

        // Whether the CLF offers selective reject in RSET.
        public bool SelectiveRejectSupport { get; set; } = SWPProtocol.DefaultSelectiveRejectSupport;

        // Powers S1 on one SWP interface and starts the activation sequence. Returns true if the link came
        // all the way up inside this call - which needs a target that answers in-slot. A
        // firmware-managed target returns false here and finishes the sequence as its firmware runs;
        // watch IsLinkEstablished(line).
        public bool Activate(int iface)
        {
            if(!TryGetTarget(iface, out var target) || !TryGetLink(iface, out var link))
            {
                return false;
            }

            link.Reset();
            link.State = SWPInterfaceState.Deactivated;
            link.ActivationPending = true;
            link.ActivationAttempts = 0;

            // Drive S1. A target with a host-side stack hands back its ACT_SYNC right away; a
            // firmware-managed one hands back nothing and answers on S2 once its firmware has run.
            var opening = target.Activate();
            if(opening != null && opening.Length > 0)
            {
                link.AnswersInSlot = true;
            }
            Deliver(iface, link, opening);

            // A target that has answered in-slot is one that can: if the sequence has stalled, the
            // frame was lost rather than merely late, and the specification's recovery is to ask for
            // it again with FR = 1. A target that has said nothing at all may simply not have run
            // its firmware yet, and retrying at it would be shouting at a chip that is still booting.
            while(!link.Established && link.ActivationPending && link.AnswersInSlot
                && RetryActivation(iface))
            {
            }

            if(link.Established)
            {
                return true;
            }
            if(link.ActivationPending)
            {
                this.Log(LogLevel.Info,
                    "SWP interface {0}: S1 is up, waiting for the target's ACT_SYNC (it answers when its firmware is ready)",
                    iface);
            }
            return false;
        }

        // Activates every registered SWP interface. Convenience for the monitor and for platforms with a
        // single UICC.
        public void ActivateAll()
        {
            foreach(var iface in ChildCollection.Keys.OrderBy(x => x).ToArray())
            {
                Activate(iface);
            }
        }

        // Asks the target to repeat its last ACT frame by re-sending ACT_POWER_MODE with FR = 1 -
        // the recovery the specification prescribes when the CLF did not get an ACT frame intact.
        // Returns false when the line is not mid-activation or the retry budget is spent.
        public bool RetryActivation(int iface)
        {
            if(!TryGetTarget(iface, out var target) || !TryGetLink(iface, out var link))
            {
                return false;
            }
            if(!link.ActivationPending || link.State == SWPInterfaceState.Deactivated)
            {
                this.Log(LogLevel.Warning, "SWP interface {0}: no activation in progress to retry", iface);
                return false;
            }
            if(link.ActivationAttempts >= Math.Max(0, ActivationRetries))
            {
                this.Log(LogLevel.Warning, "SWP interface {0}: activation retries exhausted", iface);
                return false;
            }
            link.ActivationAttempts++;
            this.Log(LogLevel.Warning,
                "SWP interface {0}: no ACT_READY (attempt {1}); re-sending ACT_POWER_MODE with FR = 1",
                iface, link.ActivationAttempts);
            SendPayload(iface, link, target, SWPProtocol.BuildActPowerMode(PowerMode, true));
            return true;
        }

        // Drives S1 low: the interface returns to DEACTIVATED and all link state is dropped.
        public void Deactivate(int iface)
        {
            if(!TryGetTarget(iface, out var target))
            {
                return;
            }
            target.Deactivate();
            if(TryGetLink(iface, out var link))
            {
                link.Reset();
                link.State = SWPInterfaceState.Deactivated;
            }
            this.Log(LogLevel.Info, "SWP interface {0} deactivated", iface);
        }

        // --------------------------------------------------------------------------------------
        // Data transfer (SHDLC LLC)
        // --------------------------------------------------------------------------------------

        // Sends one LLC payload in an SHDLC I-frame. Returns the payload the target answered with in
        // the same slot, or an empty array - which means either a bare acknowledgement or, with a
        // firmware-managed target, that the answer is still being built. A REJ is honoured by
        // retransmitting once.
        public byte[] Send(int iface, byte[] payload)
        {
            payload = payload ?? new byte[0];
            if(!TryGetTarget(iface, out var target) || !TryGetLink(iface, out var link))
            {
                return Nothing;
            }
            if(!link.Established)
            {
                this.Log(LogLevel.Warning, "SWP interface {0}: the SHDLC link is not established - call Activate first", iface);
                return Nothing;
            }
            if(payload.Length > link.TargetMaxFramePayloadSize)
            {
                this.Log(LogLevel.Warning,
                    "SWP interface {0}: payload of {1} bytes exceeds the {2}-byte maximum the UICC advertised in ACT_INFORMATION",
                    iface, payload.Length, link.TargetMaxFramePayloadSize);
                return Nothing;
            }

            var sequence = link.SendSequence;
            link.SendSequence = (sequence + 1) % SWPProtocol.SequenceModulo;
            link.PendingReject = -1;
            var delivered = SendPayload(iface, link, target,
                SWPProtocol.BuildInformation(sequence, link.ReceiveSequence, payload));

            if(link.PendingReject < 0)
            {
                return delivered;
            }

            // A REJ asks for retransmission starting at its N(R). Resynchronise our send sequence to
            // it and send the frame again - blindly reusing the refused N(S) would just be rejected.
            var rejectSequence = link.PendingReject;
            link.PendingReject = -1;
            Retransmissions++;
            this.Log(LogLevel.Debug, "SWP interface {0}: REJ received, retransmitting I-frame with N(S) = {1}",
                iface, rejectSequence);
            link.SendSequence = (rejectSequence + 1) % SWPProtocol.SequenceModulo;
            return SendPayload(iface, link, target,
                SWPProtocol.BuildInformation(rejectSequence, link.ReceiveSequence, payload));
        }

        // Monitor-friendly helper: send hex-encoded data, get the hex-encoded answer back.
        public string SendHex(int iface, string hexPayload)
        {
            return Misc.PrettyPrintCollectionHex(Send(iface, Misc.HexStringToByteArray(hexPayload)));
        }

        // Sends a bare RR - a poll that acknowledges what we have received and gives the target a
        // slot in which to answer. Returns any payload it sends back in that slot.
        public byte[] Poll(int iface)
        {
            if(!TryGetTarget(iface, out var target) || !TryGetLink(iface, out var link) || !link.Established)
            {
                return Nothing;
            }
            return SendPayload(iface, link, target,
                SWPProtocol.BuildSupervisory(SWPProtocol.SupervisoryFrameType.ReceiveReady, link.ReceiveSequence));
        }

        public string PollHex(int iface)
        {
            return Misc.PrettyPrintCollectionHex(Poll(iface));
        }

        // Raised whenever an I-frame payload from a target has been accepted, whichever way it
        // arrived. The arguments are the SWP interface and the application payload, control field
        // stripped. Fired on the emulation thread that ran the exchange.
        public event Action<int, byte[]> PayloadReceived;

        // --------------------------------------------------------------------------------------
        // Who owns the CLF's protocol layers
        //
        // By default this controller runs ACT and SHDLC itself, which is what a self-contained test
        // bench wants. But on a real CLF those layers are software too - a host stack, a driver, a
        // Java application - and a simulation is more useful when that software is the thing under
        // test rather than a stand-in for it.
        //
        // Set ProtocolOwner to External and this model stops interpreting anything: it powers S1,
        // frames and CRCs the LPDUs it is given, and hands back every LPDU it receives. It becomes
        // the CLF's transceiver, exactly as SimpleSWPPeripheral is the target's - the same split,
        // applied to the other end of the wire. CreateSWPLpduBridge puts a TCP client in that seat.
        // --------------------------------------------------------------------------------------

        // Who runs the ACT and SHDLC layers on the CLF side. Settable from a .repl or the monitor.
        public SWPProtocolOwner ProtocolOwner { get; set; } = SWPProtocolOwner.Controller;

        // Powers S1 without running any protocol - the electrical half of Activate. Use it when the
        // protocol is External: the target's opening ACT_SYNC arrives through LpduReceived, and the
        // answer to it is the owner's to send.
        public void PowerUp(int iface)
        {
            if(!TryGetTarget(iface, out var target) || !TryGetLink(iface, out var link))
            {
                return;
            }
            link.Reset();
            link.State = SWPInterfaceState.ActSync;
            link.ActivationPending = true;
            this.Log(LogLevel.Info, "SWP interface {0}: S1 driven up", iface);
            // Anything the target has ready in this slot goes through the same path as everything
            // else, so an external owner sees it too.
            Deliver(iface, link, target.Activate());
        }

        // Drives S1 low. Same as Deactivate - named for symmetry with PowerUp.
        public void PowerDown(int iface)
        {
            Deactivate(iface);
        }

        // Sends one complete LPDU - the LLC payload, control field first - on the given interface.
        // The framing, bit stuffing and CRC are added here; everything above them is the caller's.
        // Returns any LPDU the target put in the same slot (usually none: it answers when ready).
        public byte[] SendLpdu(int iface, byte[] lpdu)
        {
            if(lpdu == null || lpdu.Length == 0)
            {
                this.Log(LogLevel.Warning, "Refusing to send an empty LPDU (no control field)");
                return Nothing;
            }
            if(!TryGetTarget(iface, out var target) || !TryGetLink(iface, out var link))
            {
                return Nothing;
            }
            if(link.State == SWPInterfaceState.Deactivated)
            {
                this.Log(LogLevel.Warning,
                    "SWP interface {0}: cannot send an LPDU while S1 is low - call PowerUp first", iface);
                return Nothing;
            }
            FramesSent++;
            this.Log(LogLevel.Debug, "SWP interface {0}: sending LPDU {1}",
                iface, SWPProtocol.Describe(lpdu));
            var answer = target.ExchangeFrame(SWPFrame.Encode(lpdu));
            Deliver(iface, link, answer);
            return answer != null && answer.Length > 0 && SWPFrame.TryDecode(answer, out var decoded, out _)
                ? decoded
                : Nothing;
        }

        // Monitor-friendly helper: send one hex-encoded LPDU, get the hex-encoded in-slot answer.
        public string SendLpduHex(int iface, string hexLpdu)
        {
            return Misc.PrettyPrintCollectionHex(SendLpdu(iface, Misc.HexStringToByteArray(hexLpdu)));
        }

        // Raised for every well-formed LPDU received from a target, at every layer - ACT frames,
        // SHDLC frames, whatever the target sent - control field first, before anything interprets
        // it. Fires in both protocol modes.
        public event Action<int, byte[]> LpduReceived;

        // The last LPDU received, hex-encoded (monitor-readable).
        public string LastLpduInHex => Misc.PrettyPrintCollectionHex(lastLpduIn);

        // --------------------------------------------------------------------------------------
        // Observable state
        // --------------------------------------------------------------------------------------

        // Interface state of one SWP interface.
        public SWPInterfaceState GetInterfaceState(int iface)
        {
            return TryGetLink(iface, out var link) ? link.State : SWPInterfaceState.Deactivated;
        }

        // Interface state of SWP interface 0 - the common single-UICC case, readable straight from the monitor.
        public SWPInterfaceState InterfaceState => GetInterfaceState(0);

        // True once the SHDLC RSET/UA handshake on the line has completed.
        public bool IsLinkEstablished(int iface)
        {
            return TryGetLink(iface, out var link) && link.Established;
        }

        public bool LinkEstablished => IsLinkEstablished(0);

        // True between Activate(line) and the link coming up - the window in which the CLF is
        // waiting on the target's firmware.
        public bool IsActivationPending(int iface)
        {
            return TryGetLink(iface, out var link) && link.ActivationPending;
        }

        // SHDLC window size agreed on the line.
        public int GetWindowSize(int iface)
        {
            return TryGetLink(iface, out var link) ? link.WindowSize : 0;
        }

        // Maximum frame payload the UICC on the line advertised in ACT_INFORMATION.
        public int GetTargetMaxFramePayloadSize(int iface)
        {
            return TryGetLink(iface, out var link) ? link.TargetMaxFramePayloadSize : 0;
        }

        // SWP interface the most recent frame came from, or -1 if none since reset.
        public int LastReceivedInterface { get; private set; } = -1;

        // Payload of the most recent I-frame received, hex-encoded (monitor-readable).
        public string LastReceivedPayloadHex => Misc.PrettyPrintCollectionHex(lastReceivedPayload);

        public int FramesSent { get; private set; }
        public int FramesReceived { get; private set; }

        // Frames dropped because their CRC or framing was bad.
        public int CrcErrors { get; private set; }

        // REJ frames received from a target (it asked for a retransmission).
        public int RejectsReceived { get; private set; }

        // I-frames the controller re-sent after a REJ.
        public int Retransmissions { get; private set; }

        // Clears the pending indication (drops the IRQ line).
        public void AcknowledgeInterrupt()
        {
            IRQ.Unset();
        }

        // --------------------------------------------------------------------------------------
        // Data link layer helpers, exposed so the framing itself can be exercised and inspected
        // --------------------------------------------------------------------------------------

        // Encodes an LLC payload into a complete SWP wire frame, hex-encoded.
        public string EncodeFrameHex(string hexPayload)
        {
            return Misc.PrettyPrintCollectionHex(SWPFrame.Encode(Misc.HexStringToByteArray(hexPayload)));
        }

        // Decodes a hex-encoded SWP wire frame back to its LLC payload, or reports why it is invalid.
        public string DecodeFrameHex(string hexFrame)
        {
            return SWPFrame.TryDecode(Misc.HexStringToByteArray(hexFrame), out var payload, out var error)
                ? Misc.PrettyPrintCollectionHex(payload)
                : $"invalid frame: {error}";
        }

        // CRC-16 of a hex-encoded payload, as it would be appended to the frame.
        public string ComputeFrameCrc(string hexPayload)
        {
            return $"0x{SWPFrame.ComputeCrc(Misc.HexStringToByteArray(hexPayload)):X4}";
        }

        // Returns the target registered on the given SWP interface, or null if there is none.
        public ISWPPeripheral GetTarget(int iface)
        {
            return TryGetByAddress(iface, out var target) ? target : null;
        }

        protected bool TryGetTarget(int iface, out ISWPPeripheral target)
        {
            if(!TryGetByAddress(iface, out target))
            {
                this.Log(LogLevel.Warning, "No SWP target registered on interface {0}", iface);
                return false;
            }
            return true;
        }

        // --------------------------------------------------------------------------------------
        // The one path every frame from a target takes
        // --------------------------------------------------------------------------------------

        // Sends one LLC payload as a wire frame and feeds whatever comes back in the same slot into
        // the state machine. Returns the application payload delivered by that answer, if any.
        private byte[] SendPayload(int iface, Link link, ISWPPeripheral target, byte[] payload)
        {
            FramesSent++;
            this.Log(LogLevel.Noisy, "SWP interface {0}: sending control 0x{1:X2} with {2} payload byte(s)",
                iface, payload.Length > 0 ? payload[0] : 0, Math.Max(0, payload.Length - 1));
            var answer = target.ExchangeFrame(SWPFrame.Encode(payload));
            if(answer != null && answer.Length > 0)
            {
                link.AnswersInSlot = true;
            }
            return Deliver(iface, link, answer);
        }

        // Decodes a wire frame from a target and dispatches it. Counts CRC/framing errors. Returns
        // the application payload it carried, or an empty array.
        private byte[] Deliver(int iface, Link link, byte[] wire)
        {
            if(wire == null || wire.Length == 0)
            {
                return Nothing;
            }
            if(!SWPFrame.TryDecode(wire, out var payload, out var error))
            {
                CrcErrors++;
                this.Log(LogLevel.Warning, "SWP interface {0}: discarding a malformed frame: {1}", iface, error);
                return Nothing;
            }
            FramesReceived++;
            if(payload.Length == 0)
            {
                return Nothing;
            }

            // Every LPDU that survives the frame check is published raw, control field first, before
            // anything interprets it. That is what an external protocol owner consumes, and it is
            // also a complete tap on the link for a trace or a test.
            lastLpduIn = payload;
            LastReceivedInterface = iface;
            LpduReceived?.Invoke(iface, payload);

            if(ProtocolOwner == SWPProtocolOwner.External)
            {
                // The ACT and SHDLC layers live outside this model, so there is nothing to advance
                // and nothing to answer: whoever owns them has just been handed the LPDU and will
                // send the next one itself with SendLpdu.
                return Nothing;
            }
            return Dispatch(iface, link, payload);
        }

        // Advances the CLF state machine by one received payload, whichever layer it belongs to and
        // whichever way it arrived. May send the next frame of a handshake, which recurses back
        // here through SendPayload - bounded by the length of the activation sequence.
        private byte[] Dispatch(int iface, Link link, byte[] payload)
        {
            if(!TryGetTarget(iface, out var target))
            {
                return Nothing;
            }

            switch(payload[0])
            {
            case SWPProtocol.ActSync:
                // The target has come up and announced itself. It may do so at any time - after S1
                // rises, or again because we asked it to repeat with FR = 1 - so this is accepted
                // whenever it arrives rather than only in the state that expects it.
                ParseActInformation(link, payload);
                link.State = SWPInterfaceState.ActSync;
                link.ActivationPending = true;
                this.Log(LogLevel.Info,
                    "SWP interface {0}: ACT_SYNC received (version {1}, LLCs {2}, max frame {3} bytes); answering ACT_POWER_MODE",
                    iface, link.TargetProtocolVersion, link.TargetSupportedLlcs, link.TargetMaxFramePayloadSize);
                link.State = SWPInterfaceState.ActPowerMode;
                return SendPayload(iface, link, target, SWPProtocol.BuildActPowerMode(PowerMode, false));

            case SWPProtocol.ActReady:
                if(!link.ActivationPending)
                {
                    this.Log(LogLevel.Warning, "SWP interface {0}: unexpected ACT_READY - no activation in progress", iface);
                    return Nothing;
                }
                // ACT_READY completes the sequence; the interface is available for data transfer.
                link.PowerMode = PowerMode;
                link.State = SWPInterfaceState.Activated;
                this.Log(LogLevel.Info, "SWP interface {0} activated in {1} mode; establishing the SHDLC link", iface, PowerMode);
                return SendPayload(iface, link, target, SWPProtocol.BuildUnnumbered(
                    SWPProtocol.UnnumberedFrameModifier.Reset,
                    SWPProtocol.BuildResetParameters((byte)WindowSize, SelectiveRejectSupport)));

            case SWPProtocol.ActPowerMode:
                this.Log(LogLevel.Warning, "SWP interface {0}: a target sent ACT_POWER_MODE, which is the CLF's frame", iface);
                return Nothing;
            }

            switch(SWPProtocol.GetFrameKind(payload[0]))
            {
            case SWPProtocol.ShdlcFrameKind.Unnumbered:
                return HandleUnnumbered(iface, link, payload);
            case SWPProtocol.ShdlcFrameKind.Supervisory:
                return HandleSupervisory(iface, link, payload[0]);
            default:
                return HandleInformation(iface, link, payload);
            }
        }

        private byte[] HandleUnnumbered(int iface, Link link, byte[] payload)
        {
            if(SWPProtocol.GetModifier(payload[0]) != SWPProtocol.UnnumberedFrameModifier.UnnumberedAcknowledgement)
            {
                this.Log(LogLevel.Debug, "SWP interface {0}: U-frame 0x{1:X2} received outside link establishment",
                    iface, payload[0]);
                return Nothing;
            }

            // UA: the target accepted our RSET and echoed the parameters it will use.
            link.WindowSize = payload.Length > 1 ? Math.Max(1, Math.Min(payload[1], WindowSize)) : WindowSize;
            link.SendSequence = 0;
            link.ReceiveSequence = 0;
            link.Established = true;
            link.ActivationPending = false;
            link.State = SWPInterfaceState.Activated;
            this.Log(LogLevel.Info, "SWP interface {0}: SHDLC link established (window {1})", iface, link.WindowSize);
            return Nothing;
        }

        private byte[] HandleSupervisory(int iface, Link link, byte control)
        {
            var type = SWPProtocol.GetSupervisoryType(control);
            if(type == SWPProtocol.SupervisoryFrameType.Reject)
            {
                RejectsReceived++;
                link.PendingReject = SWPProtocol.GetReceiveSequence(control);
            }
            else if(type == SWPProtocol.SupervisoryFrameType.ReceiveNotReady)
            {
                this.Log(LogLevel.Warning, "SWP interface {0}: the UICC signalled RNR (busy)", iface);
            }
            return Nothing;
        }

        private byte[] HandleInformation(int iface, Link link, byte[] payload)
        {
            var sendSequence = SWPProtocol.GetSendSequence(payload[0]);
            if(sendSequence != link.ReceiveSequence)
            {
                this.Log(LogLevel.Warning, "SWP interface {0}: out-of-sequence I-frame N(S) = {1}, expected {2}",
                    iface, sendSequence, link.ReceiveSequence);
                return Nothing;
            }
            link.ReceiveSequence = (link.ReceiveSequence + 1) % SWPProtocol.SequenceModulo;

            var information = new byte[payload.Length - 1];
            Array.Copy(payload, 1, information, 0, information.Length);
            lastReceivedPayload = information;
            PayloadReceived?.Invoke(iface, information);
            return information;
        }

        private void ParseActInformation(Link link, byte[] actSync)
        {
            // ACT_SYNC = control byte + ACT_INFORMATION (version, LLC bitmap, max frame size, power modes).
            if(actSync.Length > 1)
            {
                link.TargetProtocolVersion = actSync[1];
            }
            if(actSync.Length > 2)
            {
                link.TargetSupportedLlcs = (SWPProtocol.SupportedLlc)actSync[2];
            }
            if(actSync.Length > 4)
            {
                var size = (actSync[3] << 8) | actSync[4];
                link.TargetMaxFramePayloadSize = size > 0 ? size : DefaultMaxFramePayloadSize;
            }
            if(actSync.Length > 5)
            {
                link.TargetPowerModes = actSync[5];
            }
        }

        // A target transmitted on S2 on its own initiative - an ACT frame its firmware has just
        // built, an answer to something we sent earlier, or an unsolicited I-frame. It goes through
        // exactly the same state machine as an in-slot answer.
        private void HandleTargetFrame(ISWPPeripheral target, byte[] wire)
        {
            var iface = ChildCollection.Where(x => ReferenceEquals(x.Value, target))
                .Select(x => (int?)x.Key).FirstOrDefault() ?? -1;
            if(iface < 0 || !TryGetLink(iface, out var link))
            {
                return;
            }

            var payload = Deliver(iface, link, wire);
            if(payload.Length == 0)
            {
                return;
            }
            this.Log(LogLevel.Info, "Unsolicited frame from the UICC on SWP interface {0}, payload {1}",
                iface, Misc.PrettyPrintCollectionHex(payload));
            IRQ.Set();
        }

        private bool TryGetLink(int iface, out Link link)
        {
            return links.TryGetValue(iface, out link);
        }

        private const int DefaultMaxFramePayloadSize = 4096;

        private byte[] lastReceivedPayload = new byte[0];
        private byte[] lastLpduIn = new byte[0];
        // Field initializers, not constructor-body assignments: Reset() touches them.
        private readonly Dictionary<ISWPPeripheral, Action<ISWPPeripheral, byte[]>> frameHandlers =
            new Dictionary<ISWPPeripheral, Action<ISWPPeripheral, byte[]>>();
        private readonly Dictionary<int, Link> links = new Dictionary<int, Link>();

        private static readonly byte[] Nothing = new byte[0];

        // Per-line interface and SHDLC state held by the CLF.
        private class Link
        {
            public void Reset()
            {
                Established = false;
                ActivationPending = false;
                ActivationAttempts = 0;
                AnswersInSlot = false;
                PendingReject = -1;
                SendSequence = 0;
                ReceiveSequence = 0;
                WindowSize = SWPProtocol.DefaultWindowSize;
                PowerMode = SWPPowerMode.LowPower;
                TargetProtocolVersion = 0;
                TargetSupportedLlcs = SWPProtocol.SupportedLlc.None;
                TargetMaxFramePayloadSize = DefaultMaxFramePayloadSize;
                TargetPowerModes = 0;
            }

            public SWPInterfaceState State = SWPInterfaceState.Deactivated;
            public SWPPowerMode PowerMode = SWPPowerMode.LowPower;
            public bool Established;

            // Set between Activate() and the link coming up: the window in which an ACT frame from
            // the target is expected and the FR retry applies.
            public bool ActivationPending;
            public int ActivationAttempts;

            // Set once this target has answered inside a slot. It tells a lost frame (retry now)
            // apart from a target whose firmware has not answered yet (wait for it).
            public bool AnswersInSlot;

            // N(R) of a REJ seen while handling the current Send, or -1. Collected here because the
            // REJ may arrive from a nested dispatch rather than as Send's own return value.
            public int PendingReject = -1;

            public int SendSequence;
            public int ReceiveSequence;
            public int WindowSize = SWPProtocol.DefaultWindowSize;
            public byte TargetProtocolVersion;
            public SWPProtocol.SupportedLlc TargetSupportedLlcs = SWPProtocol.SupportedLlc.None;
            public int TargetMaxFramePayloadSize = DefaultMaxFramePayloadSize;
            public byte TargetPowerModes;
        }
    }
}
