//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // An "invented" memory-mapped SWP contact for firmware-in-the-loop testing: the UICC/eSE side of
    // an ETSI TS 102 613 link whose ACT and SHDLC layers run as FIRMWARE ON THE EMULATED CPU.
    //
    // This is the model that matches how real silicon is built. The hardware block does the wire and
    // nothing else - it raises an interrupt when the CLF activates the interface, hands received
    // frames over as raw LLC payloads, and transmits the payloads firmware gives it. Every ACT_SYNC,
    // every ACT_READY, every UA and every N(R) in the simulation is one the firmware actually built.
    // If the firmware forgets to answer, the CLF sees silence, exactly as it would on a bench.
    //
    // It mirrors the shape of a typical SWP MAC/LLC block, so a firmware LLC layer ports onto it
    // directly:
    //
    //   1. Activation  - the CLF drives S1. ACT_EVT latches in STATUS and the IRQ line is asserted.
    //                    The firmware's interrupt handler opens its LLC and pushes an ACT_SYNC
    //                    payload of its own making (SyncId, bit duration, whatever its profile says
    //                    - the hardware neither knows nor cares).
    //   2. Reception   - a frame arrives, its framing and CRC are checked, and its complete LLC
    //                    payload - control field first - is queued. RX_FRAME goes up in STATUS with
    //                    the byte count of the current frame, and the IRQ line follows.
    //   3. Transmission- the firmware writes the answer byte by byte into TX_DATA and writes
    //                    TX_COMMIT. Only then is a frame framed, CRC'd and driven onto S2.
    //   4. Deactivation- the CLF drives S1 low. DEACT_EVT latches in STATUS and the IRQ line is
    //                    asserted, so the firmware can close its LLC and MAC layers.
    //
    // Frames go out on S2 asynchronously (SWP is full duplex), which is what makes this work at all:
    // the firmware only runs after the receiving slot is over, so an answer can never ride the frame
    // that asked for it. The CLF - SimpleSWPController - is written to expect exactly that.
    //
    // Registered on both the sysbus (MMIO, for the firmware) and the SWP interface (for the controller):
    //
    //     uicc: SWP.InventedSWPTarget @ { sysbus 0x90000000; swp 0 }
    //
    // The IRQ line is exposed as GPIO 0 and can be wired to an interrupt controller in the .repl.
    // Firmware that would rather poll can read STATUS instead - the same bits drive both.
    public class InventedSWPTarget : SimpleSWPPeripheral, IDoubleWordPeripheral, IKnownSize,
        INumberedGPIOOutput
    {
        public InventedSWPTarget()
        {
            IRQ = new GPIO();
            Connections = new Dictionary<int, IGPIO> { { 0, IRQ } };
        }

        public override void Reset()
        {
            base.Reset();
            lock(locker)
            {
                // Called from the base constructor, before IRQ exists - see the note in
                // SimpleSWPPeripheral about the virtual Reset().
                rxFrames?.Clear();
                currentRxFrame = null;
                currentRxOffset = 0;
                txBuffer?.Clear();
                statusFlags = 0;
                interruptEnable = DefaultInterruptEnable;
                LlcState = SWPLlcState.Closed;
                IRQ?.Unset();
            }
        }

        // --------------------------------------------------------------------------------------
        // Firmware-facing register window
        // --------------------------------------------------------------------------------------

        public uint ReadDoubleWord(long offset)
        {
            lock(locker)
            {
                switch((Registers)offset)
                {
                case Registers.Status:
                    return BuildStatus();
                case Registers.InterruptEnable:
                    return interruptEnable;
                case Registers.RxData:
                    return PopRxByte();
                case Registers.LlcState:
                    return (uint)LlcState;
                default:
                    this.Log(LogLevel.Warning, "Read from an unhandled register 0x{0:X}", offset);
                    return 0;
                }
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            byte[] payload = null;
            lock(locker)
            {
                switch((Registers)offset)
                {
                case Registers.StatusClear:
                    // Write 1 to clear, for the latched event bits only; the level bits follow the
                    // hardware state and cannot be cleared by firmware.
                    statusFlags &= ~(value & (StatusActivationEvent | StatusDeactivationEvent));
                    break;
                case Registers.InterruptEnable:
                    interruptEnable = value;
                    break;
                case Registers.RxNext:
                    // Drop whatever is left of the current frame and move on to the next one. A
                    // firmware that does not understand a frame uses this instead of draining it.
                    DropCurrentRxFrame();
                    break;
                case Registers.TxData:
                    txBuffer.Add((byte)value);
                    break;
                case Registers.TxCommit:
                    if(txBuffer.Count == 0)
                    {
                        this.Log(LogLevel.Warning, "TX_COMMIT with an empty TX buffer - nothing to transmit");
                        break;
                    }
                    payload = txBuffer.ToArray();
                    txBuffer.Clear();
                    break;
                case Registers.Control:
                    if((value & ControlFlush) != 0)
                    {
                        rxFrames.Clear();
                        currentRxFrame = null;
                        currentRxOffset = 0;
                        txBuffer.Clear();
                    }
                    break;
                case Registers.LlcState:
                    // Introspection only: the firmware publishes the LLC state it is in, so the
                    // monitor, the robot suites and the CLF-side logs can see it. The hardware does
                    // not act on it - it has no opinion about the protocol.
                    PublishLlcState(value);
                    break;
                default:
                    this.Log(LogLevel.Warning, "Write 0x{0:X} to an unhandled register 0x{1:X}", value, offset);
                    break;
                }
                UpdateInterrupt();
            }

            if(payload != null)
            {
                // Outside the lock: TransmitPayload frames the payload and raises FrameAvailable,
                // which runs the CLF's receive path.
                this.Log(LogLevel.Debug, "Firmware committed a {0}-byte LLC payload ({1})",
                    payload.Length, SWPProtocol.Describe(payload));
                TransmitPayload(payload);
            }
        }

        public long Size => 0x100;

        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // --------------------------------------------------------------------------------------
        // Observable state - monitor and robot friendly
        // --------------------------------------------------------------------------------------

        // The LLC state the firmware last published through the LLC_STATE register. The hardware
        // cannot know it by itself; this is the firmware telling us.
        public SWPLlcState LlcState { get; private set; } = SWPLlcState.Closed;

        // Complete LLC payloads received and not yet drained by the firmware.
        public int PendingRxFrames
        {
            get
            {
                lock(locker)
                {
                    return rxFrames.Count + (currentRxFrame != null ? 1 : 0);
                }
            }
        }

        // Bytes the firmware has pushed into the TX buffer but not yet committed.
        public int UncommittedTxBytes
        {
            get
            {
                lock(locker)
                {
                    return txBuffer.Count;
                }
            }
        }

        // --------------------------------------------------------------------------------------
        // Activation events
        //
        // On silicon, S1 coming up and the ACT_EVT interrupt reaching the CPU are two different
        // things separated by real time, and a bench that models the power-up order (VPS, then S1,
        // then the event) needs to place them separately. Leave AutoActivationEvent set and the
        // event follows the S1 edge immediately; clear it and the bench raises the event itself with
        // TriggerActEvent / TriggerDeactEvent, after whatever delay it wants to model.
        // --------------------------------------------------------------------------------------

        // Whether the ACT_EVT / DEACT_EVT flags follow the S1 edges automatically. Settable from a
        // .repl or the monitor.
        public bool AutoActivationEvent { get; set; } = true;

        // Latches ACT_EVT and interrupts the CPU: "the CLF has activated the interface". The
        // firmware's handler is what opens the LLC and sends ACT_SYNC - nothing is sent from here.
        public void TriggerActEvent()
        {
            lock(locker)
            {
                if(InterfaceState == SWPInterfaceState.Deactivated)
                {
                    this.Log(LogLevel.Warning, "Activation event raised while S1 is low - raise S1 first");
                }
                statusFlags |= StatusActivationEvent;
                statusFlags &= ~StatusDeactivationEvent;
                UpdateInterrupt();
            }
            this.Log(LogLevel.Debug, "ACT_EVT latched; waiting for the firmware to answer");
        }

        // Latches DEACT_EVT and interrupts the CPU: "the CLF has deactivated the interface".
        public void TriggerDeactEvent()
        {
            lock(locker)
            {
                statusFlags |= StatusDeactivationEvent;
                statusFlags &= ~StatusActivationEvent;
                UpdateInterrupt();
            }
            this.Log(LogLevel.Debug, "DEACT_EVT latched");
        }

        // --------------------------------------------------------------------------------------
        // Transport hooks - queue and interrupt, never answer
        // --------------------------------------------------------------------------------------

        // S1 has come up. Latch the activation event and interrupt the CPU; the answer, if any, is
        // the firmware's to send. Nothing goes out on S2 from here.
        protected override byte[] OnActivated()
        {
            if(AutoActivationEvent)
            {
                TriggerActEvent();
            }
            return null;
        }

        // S1 has gone low. Drop every buffered frame - the contact is unpowered and keeps no state -
        // and latch the deactivation event so the firmware can close its LLC and MAC layers.
        protected override void OnDeactivated()
        {
            lock(locker)
            {
                rxFrames.Clear();
                currentRxFrame = null;
                currentRxOffset = 0;
                txBuffer.Clear();
                LlcState = SWPLlcState.Closed;
                UpdateInterrupt();
            }
            if(AutoActivationEvent)
            {
                TriggerDeactEvent();
            }
        }

        // A well-formed frame arrived. Queue its complete LLC payload for the firmware and raise the
        // interrupt. S2 stays silent in this slot: the firmware has not even run yet.
        protected override byte[] OnPayloadReceived(byte[] payload)
        {
            if(rxFrames.Count + (currentRxFrame != null ? 1 : 0) >= MaxPendingRxFrames)
            {
                // A real block has a bounded frame buffer too, and firmware that stops draining it
                // must lose frames rather than have the model grow without limit.
                this.Log(LogLevel.Warning,
                    "RX frame buffer full ({0} frames); dropping a {1}-byte payload the firmware never read",
                    MaxPendingRxFrames, payload.Length);
                return null;
            }
            rxFrames.Enqueue(payload);
            AdvanceRxFrame();
            UpdateInterrupt();
            return null;
        }

        // --------------------------------------------------------------------------------------

        private uint BuildStatus()
        {
            var status = statusFlags;
            if(currentRxFrame != null)
            {
                status |= StatusRxFrame;
            }
            if(InterfaceState != SWPInterfaceState.Deactivated)
            {
                status |= StatusInterfacePowered;
            }
            var remaining = currentRxFrame != null ? currentRxFrame.Length - currentRxOffset : 0;
            return status | ((uint)remaining << RxCountShift);
        }

        private uint PopRxByte()
        {
            if(currentRxFrame == null)
            {
                this.Log(LogLevel.Warning, "RX_DATA read with no frame pending");
                return 0;
            }
            var value = currentRxFrame[currentRxOffset++];
            if(currentRxOffset >= currentRxFrame.Length)
            {
                currentRxFrame = null;
                currentRxOffset = 0;
                AdvanceRxFrame();
            }
            UpdateInterrupt();
            return value;
        }

        private void DropCurrentRxFrame()
        {
            currentRxFrame = null;
            currentRxOffset = 0;
            AdvanceRxFrame();
        }

        private void AdvanceRxFrame()
        {
            if(currentRxFrame == null && rxFrames.Count > 0)
            {
                currentRxFrame = rxFrames.Dequeue();
                currentRxOffset = 0;
            }
        }

        private void PublishLlcState(uint value)
        {
            if(!Enum.IsDefined(typeof(SWPLlcState), (int)value))
            {
                this.Log(LogLevel.Warning, "Firmware published an unknown LLC state {0}", value);
                return;
            }
            LlcState = (SWPLlcState)value;
            // Keep the interface state the CLF-side tooling reads in step with what the firmware
            // says it is doing. The transport still owns Deactivated: S1 is not the firmware's call.
            if(InterfaceState != SWPInterfaceState.Deactivated)
            {
                InterfaceState = ToInterfaceState(LlcState);
            }
            this.Log(LogLevel.Debug, "Firmware LLC state is now {0}", LlcState);
        }

        private static SWPInterfaceState ToInterfaceState(SWPLlcState state)
        {
            switch(state)
            {
            case SWPLlcState.ActReadySent:
                return SWPInterfaceState.ActReady;
            case SWPLlcState.Established:
                return SWPInterfaceState.Activated;
            default:
                return SWPInterfaceState.ActSync;
            }
        }

        private void UpdateInterrupt()
        {
            if(IRQ == null)
            {
                return;
            }
            var pending = (BuildStatus() & interruptEnable & InterruptSources) != 0;
            if(pending != IRQ.IsSet)
            {
                IRQ.Set(pending);
            }
        }

        private uint statusFlags;
        private uint interruptEnable = DefaultInterruptEnable;
        private byte[] currentRxFrame;
        private int currentRxOffset;

        private readonly Queue<byte[]> rxFrames = new Queue<byte[]>();
        private readonly List<byte> txBuffer = new List<byte>();

        // STATUS bits.
        private const uint StatusActivationEvent = 1u << 0;   // latched: the CLF activated the interface
        private const uint StatusDeactivationEvent = 1u << 1; // latched: the CLF deactivated it
        private const uint StatusRxFrame = 1u << 2;           // level: an LLC payload is waiting
        private const uint StatusInterfacePowered = 1u << 3;  // level: S1 is driven
        private const uint InterruptSources =
            StatusActivationEvent | StatusDeactivationEvent | StatusRxFrame;
        private const uint DefaultInterruptEnable = InterruptSources;
        private const int RxCountShift = 8;

        // CONTROL bits.
        private const uint ControlFlush = 1u << 0;

        // How many complete frames the RX buffer holds before it starts dropping.
        private const int MaxPendingRxFrames = 16;

        private enum Registers : long
        {
            Status = 0x00,          // R:  see the Status* bits; bits[23:8] = bytes left in the current frame
            StatusClear = 0x04,     // W:  write 1 to clear ACT_EVT / DEACT_EVT
            InterruptEnable = 0x08, // RW: which STATUS bits assert the IRQ line
            RxData = 0x0C,          // R:  pop one byte of the current LLC payload (control field first)
            RxNext = 0x10,          // W:  discard the rest of the current frame, move to the next
            TxData = 0x14,          // W:  push one byte of the outgoing LLC payload
            TxCommit = 0x18,        // W:  frame it, CRC it and drive it onto S2
            Control = 0x1C,         // W:  bit0 = flush the RX and TX buffers
            LlcState = 0x20,        // RW: the firmware publishes its LLC state (introspection only)
        }
    }

    // The LLC state a firmware publishes through the LLC_STATE register. These mirror the states a
    // real SWP LLC layer goes through (closed, opened, ACT_SYNC sent, ACT_READY sent, SHDLC up), and
    // are for introspection only - the hardware model never acts on them.
    public enum SWPLlcState
    {
        Closed = 0,
        Opened = 1,
        ActSyncSent = 2,
        ActReadySent = 3,
        Established = 4,
    }
}
