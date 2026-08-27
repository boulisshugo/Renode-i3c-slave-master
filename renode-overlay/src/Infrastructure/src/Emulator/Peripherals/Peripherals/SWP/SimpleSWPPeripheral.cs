//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // The SWP hardware block of a target (the UICC side of an ETSI TS 102 613 link).
    //
    // WHAT THIS MODELS, AND WHAT IT DELIBERATELY DOES NOT
    //
    // On real silicon the SWP contact is a transceiver: it drives and samples S1/S2, finds the frame
    // delimiters, undoes the bit stuffing and checks the CRC. That is the physical and data link
    // layer, clauses 7 and 8 - and that is ALL this class does.
    //
    // Everything above it is protocol: the ACT activation LLC (clause 11) and SHDLC (clause 10) -
    // ACT_SYNC, ACT_READY, RSET/UA, the modulo-8 N(S)/N(R) sequencing, RR and REJ. On the targets
    // this repository models, that layer lives in the TARGET'S FIRMWARE, not in the hardware, so
    // this peripheral must not answer the CLF on its own: an answer it invented would be an answer
    // the firmware never sent, and firmware bugs (a missing ACT_READY, a stale N(R), a late UA)
    // would be papered over by the model instead of showing up in the simulation.
    //
    // So: a frame arrives, its framing and CRC are checked, and the resulting LLC payload - control
    // field first, exactly as it came off the wire - is handed up through OnPayloadReceived. If
    // whoever owns the protocol has nothing to send, S2 stays silent. Nothing else happens.
    //
    // WHO OWNS THE PROTOCOL, THEN
    //
    //   - InventedSWPTarget: a memory-mapped register window on top of this class. The frame lands
    //     in an RX FIFO, firmware running on the emulated CPU reads it, builds the answer and
    //     commits it, and only then does a frame leave on S2. This is the firmware-in-the-loop case.
    //   - SoftwareSWPTarget: this class plus SWPTargetStack, a host-side implementation of ACT and
    //     SHDLC. For test benches, mocks and the consistency suites, where there is no firmware to
    //     run - the stack stands in for it, explicitly, rather than the hardware pretending.
    //
    // A proprietary target subclasses whichever of the two matches where its protocol really lives.
    //
    // SWP IS FULL DUPLEX
    //
    // The UICC drives S2 whenever it has something to say; it does not have to wait for a slot.
    // ExchangeFrame may therefore answer with nothing at all, and TransmitPayload may push a frame
    // out at any time (it raises FrameAvailable). A firmware-managed target uses the latter for
    // every answer it sends, because its firmware only runs after the receiving slot is over.
    //
    // TRACING
    //
    // Every frame crossing the wire is traced, at every layer, decoded or malformed: the OnFrameSent
    // / OnFrameReceived hooks, the FrameTraced event, and the LastFrameInHex / LastFrameOutHex /
    // FrameTraceHex properties for the monitor and robot tests. Recording happens at the two choke
    // points every frame must pass - Transmit on the way out, the decode in ExchangeFrame on the way
    // in - so no layer and no code path can slip past it.
    //
    // NOTE (same gotcha as the I3C/SPI models): the constructor calls the virtual Reset(), so every
    // field Reset() touches must be a field initializer, not a constructor-body assignment.
    public class SimpleSWPPeripheral : ISWPPeripheral
    {
        public SimpleSWPPeripheral()
        {
            Reset();
        }

        public virtual void Reset()
        {
            lock(locker)
            {
                ClearFrameTrace();
                FramesReceived = 0;
                FramesSent = 0;
                FrameErrors = 0;
                InterfaceState = SWPInterfaceState.Deactivated;
                PowerMode = SWPPowerMode.LowPower;
            }
        }

        // --------------------------------------------------------------------------------------
        // ISWPPeripheral - the electrical interface, driven by the CLF
        // --------------------------------------------------------------------------------------

        // The activation state of the interface. The transport itself only knows the two states it
        // can observe electrically - S1 low (Deactivated) and S1 driven (ActSync, i.e. powered and
        // waiting for the activation sequence to run). The intermediate ACT states and Activated
        // belong to whoever runs the ACT LLC, which reports them through the protected setter.
        public SWPInterfaceState InterfaceState { get; protected set; } = SWPInterfaceState.Deactivated;

        // The CLF starts driving S1: the contact is powered. Nothing is sent in reply here - on a
        // real target the firmware wakes up and sends ACT_SYNC when it is ready, which is what
        // OnActivated is for. A subclass that has an answer available immediately may return it.
        //
        // Idempotent: activating an already powered interface changes nothing and notifies nobody,
        // so a test bench that raises S1 itself (SetS1) and then lets the CLF activate the line does
        // not produce two activations.
        public virtual byte[] Activate()
        {
            lock(locker)
            {
                var payload = RaiseS1();
                return payload.Length > 0 ? Transmit(payload) : Nothing;
            }
        }

        // The CLF drives S1 low. The interface is unpowered and keeps no state.
        public virtual void Deactivate()
        {
            lock(locker)
            {
                if(InterfaceState == SWPInterfaceState.Deactivated)
                {
                    return;
                }
                InterfaceState = SWPInterfaceState.Deactivated;
                PowerMode = SWPPowerMode.LowPower;
            }
            this.Log(LogLevel.Debug, "S1 driven low by the CLF: the interface is deactivated");
            OnDeactivated();
        }

        // Drives S1 from outside the CLF model - for a test bench or an event sequencer that owns
        // the power-up order (VPS first, then S1, then the activation event) and wants to place each
        // edge in time itself.
        //
        // Unlike Activate(), which hands its answer back to the CLF as that slot's S2 traffic, this
        // puts any immediate answer on S2 asynchronously. That is what really happens: the UICC's
        // ACT_SYNC is not a reply to a frame, it is the UICC talking first.
        public void SetS1(bool driven)
        {
            if(!driven)
            {
                Deactivate();
                return;
            }
            byte[] payload;
            lock(locker)
            {
                payload = RaiseS1();
            }
            if(payload.Length > 0)
            {
                TransmitPayload(payload);
            }
        }

        // Powers the contact and asks the protocol layer whether it has anything to say straight
        // away. Returns that LLC payload - un-framed; the caller decides whether it leaves as this
        // slot's S2 traffic or asynchronously. Called with the lock held.
        private byte[] RaiseS1()
        {
            if(InterfaceState != SWPInterfaceState.Deactivated)
            {
                this.Log(LogLevel.Debug, "S1 is already driven; ignoring a repeated activation");
                return Nothing;
            }
            InterfaceState = SWPInterfaceState.ActSync;
            PowerMode = SWPPowerMode.LowPower;
            this.Log(LogLevel.Debug, "S1 driven by the CLF: the interface is powered");
            return OnActivated() ?? Nothing;
        }

        // One full-duplex frame slot: the CLF's frame arrives on S1, and whatever the protocol layer
        // has ready for this slot leaves on S2 - usually nothing, because the answer to this frame
        // cannot exist yet.
        public virtual byte[] ExchangeFrame(byte[] wireFrame)
        {
            lock(locker)
            {
                if(InterfaceState == SWPInterfaceState.Deactivated)
                {
                    this.Log(LogLevel.Warning, "Frame received while the interface is deactivated - ignored");
                    return Nothing;
                }
                if(!SWPFrame.TryDecode(wireFrame, out var payload, out var error))
                {
                    FrameErrors++;
                    // A corrupted frame is simply not acknowledged - it never reaches the protocol
                    // layer, exactly as a real transceiver would drop it. It is still traced: a
                    // trace that hides bad frames is no use for debugging.
                    Record(SWPFrameDirection.Received, wireFrame, null, "malformed: " + error);
                    this.Log(LogLevel.Warning, "Discarding a malformed frame: {0}", error);
                    return Nothing;
                }
                FramesReceived++;
                Record(SWPFrameDirection.Received, wireFrame, payload, SWPProtocol.Describe(payload));
                if(payload.Length == 0)
                {
                    this.Log(LogLevel.Warning, "Discarding an empty frame (no control field)");
                    return Nothing;
                }
                var answer = OnPayloadReceived(payload);
                return answer == null || answer.Length == 0 ? Nothing : Transmit(answer);
            }
        }

        public event Action<ISWPPeripheral, byte[]> FrameAvailable;

        // --------------------------------------------------------------------------------------
        // Observable state - monitor and robot friendly
        // --------------------------------------------------------------------------------------

        // Power mode in force on the interface. The transport cannot know it by itself: it is
        // selected by the CLF in ACT_POWER_MODE, so the protocol layer reports it here.
        public SWPPowerMode PowerMode { get; protected set; } = SWPPowerMode.LowPower;

        // True once S1 is driven and the contact is powered.
        public bool InterfacePowered => InterfaceState != SWPInterfaceState.Deactivated;

        public int FramesReceived { get; private set; }
        public int FramesSent { get; private set; }

        // Frames dropped because their CRC or framing was bad.
        public int FrameErrors { get; private set; }

        // --------------------------------------------------------------------------------------
        // Frame trace - the raw wire image of every frame, whichever layer it belongs to
        // --------------------------------------------------------------------------------------

        // Raw on-wire image of the last frame in / out, hex-encoded (monitor-readable).
        public string LastFrameInHex => Misc.PrettyPrintCollectionHex(lastFrameIn?.WireFrame ?? EmptyBytes);
        public string LastFrameOutHex => Misc.PrettyPrintCollectionHex(lastFrameOut?.WireFrame ?? EmptyBytes);

        // Decoded LLC payload of the last frame in / out - control field first - hex-encoded. This
        // is the whole payload as it came off the wire; a protocol layer that wants the information
        // field with the control byte stripped keeps that itself.
        public string LastPayloadInHex => Misc.PrettyPrintCollectionHex(lastFrameIn?.Payload ?? EmptyBytes);
        public string LastPayloadOutHex => Misc.PrettyPrintCollectionHex(lastFrameOut?.Payload ?? EmptyBytes);

        // Human-readable name of the last frame in / out, e.g. "ACT_READY" or "I   N(S)=0 N(R)=1 +2B".
        public string LastFrameIn => lastFrameIn?.Description ?? string.Empty;
        public string LastFrameOut => lastFrameOut?.Description ?? string.Empty;

        // How many frames the rolling trace keeps. 0 disables recording; the Last* properties above
        // are always maintained regardless. Settable from a .repl or the monitor.
        public int FrameTraceDepth
        {
            get => frameTraceDepth;
            set
            {
                lock(locker)
                {
                    frameTraceDepth = Math.Max(0, value);
                    TrimTrace();
                }
            }
        }

        // The rolling trace, one frame per line: direction, raw wire bytes, decoded name.
        public string FrameTraceHex
        {
            get
            {
                lock(locker)
                {
                    return frameTrace.Count == 0
                        ? "(no frames traced)"
                        : string.Join(Environment.NewLine, frameTrace.Select(x => x.ToString()));
                }
            }
        }

        // The traced frames, newest last. Snapshot - safe to enumerate.
        public IEnumerable<SWPFrameRecord> FrameTrace
        {
            get
            {
                lock(locker)
                {
                    return frameTrace.ToArray();
                }
            }
        }

        public void ClearFrameTrace()
        {
            lock(locker)
            {
                frameTrace.Clear();
                lastFrameIn = null;
                lastFrameOut = null;
            }
        }

        // Raised for every frame crossing the wire in either direction, at every layer - ACT frames,
        // SHDLC frames and malformed ones alike. Subscribe instead of subclassing when a bridge or a
        // test wants the raw bytes.
        //
        // Fired while the peripheral's lock is held (as OnPayloadReceived is): a handler must not
        // call back into this peripheral.
        public event Action<ISWPPeripheral, SWPFrameRecord> FrameTraced;

        // --------------------------------------------------------------------------------------
        // Monitor helpers
        // --------------------------------------------------------------------------------------

        // Push a raw wire frame straight at the target, as if the CLF had sent it, and get its
        // answer back hex-encoded. Useful for replaying a capture or feeding a deliberately corrupt
        // frame without writing a C# test-bench (ExchangeFrame itself takes a byte[], which the
        // monitor cannot bind).
        public string ExchangeFrameHex(string hexWireFrame)
        {
            return Misc.PrettyPrintCollectionHex(ExchangeFrame(Misc.HexStringToByteArray(hexWireFrame)));
        }

        // Transmit one complete LLC payload - control field first - on S2, hex-encoded. The framing
        // and the CRC are added here; the protocol content is entirely the caller's.
        public void TransmitPayloadHex(string hexPayload)
        {
            TransmitPayload(Misc.HexStringToByteArray(hexPayload));
        }

        // --------------------------------------------------------------------------------------
        // Hooks for the layer that owns the protocol
        // --------------------------------------------------------------------------------------

        // Called with the complete LLC payload of every well-formed frame received - control field
        // first, ACT and SHDLC alike. Return a payload to send back in the SAME slot, or null (the
        // default) to leave S2 silent and answer later with TransmitPayload.
        //
        // Runs with the peripheral's lock held; do not call back into this peripheral from it.
        protected virtual byte[] OnPayloadReceived(byte[] payload)
        {
            return null;
        }

        // Called when the CLF starts driving S1. Return a payload to send immediately, or null (the
        // default) to stay silent until the protocol layer has something to say - which is what real
        // firmware does, since it has to wake up first.
        protected virtual byte[] OnActivated()
        {
            return null;
        }

        // Called when the CLF deactivates the interface. Default: no-op.
        protected virtual void OnDeactivated()
        {
        }

        // Called for every frame received from the CLF, before it is acted on - ACT frames, SHDLC
        // frames, and frames that failed to decode (frame.IsMalformed). Default: no-op.
        //
        // Runs with the peripheral's lock held; do not call back into this peripheral from it.
        protected virtual void OnFrameReceived(SWPFrameRecord frame)
        {
        }

        // Called for every frame this peripheral transmits, at every layer. Default: no-op.
        //
        // Runs with the peripheral's lock held; do not call back into this peripheral from it.
        protected virtual void OnFrameSent(SWPFrameRecord frame)
        {
        }

        // Transmits one complete LLC payload on S2 on the target's own initiative - the normal way a
        // UICC answers, since SWP is full duplex and its answer is rarely ready inside the slot that
        // asked for it. The payload is framed and CRC'd here and nothing else: the control field and
        // any sequence numbers in it are the protocol layer's business.
        //
        // Raises FrameAvailable with the complete wire frame, outside the lock.
        public void TransmitPayload(byte[] payload)
        {
            byte[] wire;
            lock(locker)
            {
                if(InterfaceState == SWPInterfaceState.Deactivated)
                {
                    this.Log(LogLevel.Warning, "Cannot transmit: the interface is deactivated");
                    return;
                }
                if(payload == null || payload.Length == 0)
                {
                    this.Log(LogLevel.Warning, "Refusing to transmit an empty payload (no control field)");
                    return;
                }
                wire = Transmit(payload);
            }
            FrameAvailable?.Invoke(this, wire);
        }

        // --------------------------------------------------------------------------------------

        // Every frame this peripheral sends goes through here, so recording at this one point cannot
        // miss a layer. Called with the lock held.
        private byte[] Transmit(byte[] payload)
        {
            FramesSent++;
            var wire = SWPFrame.Encode(payload);
            Record(SWPFrameDirection.Sent, wire, payload, SWPProtocol.Describe(payload));
            return wire;
        }

        // Called with the lock held, from the two choke points (Transmit and the decode in
        // ExchangeFrame), so no frame reaches the wire without passing through it.
        private void Record(SWPFrameDirection direction, byte[] wireFrame, byte[] payload, string description)
        {
            var record = new SWPFrameRecord(direction, wireFrame, payload, description);
            if(direction == SWPFrameDirection.Received)
            {
                lastFrameIn = record;
            }
            else
            {
                lastFrameOut = record;
            }

            if(frameTraceDepth > 0)
            {
                frameTrace.Enqueue(record);
                TrimTrace();
            }

            this.Log(LogLevel.Noisy, "{0}", record);
            if(direction == SWPFrameDirection.Received)
            {
                OnFrameReceived(record);
            }
            else
            {
                OnFrameSent(record);
            }
            FrameTraced?.Invoke(this, record);
        }

        private void TrimTrace()
        {
            while(frameTrace.Count > frameTraceDepth)
            {
                frameTrace.Dequeue();
            }
        }

        protected readonly object locker = new object();

        private SWPFrameRecord lastFrameIn;
        private SWPFrameRecord lastFrameOut;
        private int frameTraceDepth = DefaultFrameTraceDepth;

        private readonly Queue<SWPFrameRecord> frameTrace = new Queue<SWPFrameRecord>();

        // Frames kept by default. Cheap - the trace stores references to arrays that already exist.
        private const int DefaultFrameTraceDepth = 32;

        private static readonly byte[] Nothing = new byte[0];
        private static readonly byte[] EmptyBytes = new byte[0];
    }
}
