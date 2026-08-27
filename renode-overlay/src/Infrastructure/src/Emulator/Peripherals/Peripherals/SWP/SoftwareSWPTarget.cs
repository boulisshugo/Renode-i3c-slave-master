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
    // A self-contained SWP target: the SimpleSWPPeripheral transport with an SWPTargetStack bolted
    // on top of it, so ACT and SHDLC are answered by the model itself.
    //
    // USE THIS ONLY WHEN THERE IS NO FIRMWARE. On a real target the ACT and SHDLC layers are the
    // chip's firmware, and the model for that is InventedSWPTarget, which hands raw payloads to the
    // emulated CPU. This class is the stand-in for a firmware that does not exist in the simulation:
    // a well-behaved UICC to point a CLF at in mocks, benches and the consistency suites.
    //
    // The distinction is deliberate and is the whole reason the two are separate classes: nothing a
    // SimpleSWPPeripheral does can be mistaken for something firmware did, and everything this class
    // answers is visibly the host-side stack answering.
    //
    // Out of the box it answers each I-frame with the next payload queued by EnqueueResponsePayload
    // (or a bare RR acknowledgement when the queue is empty). Override OnInformation for proprietary
    // behaviour, and call SendInformation to transmit on the UICC's own initiative - SWP is full
    // duplex, so the UICC does not have to wait to be polled.
    public class SoftwareSWPTarget : SimpleSWPPeripheral
    {
        public SoftwareSWPTarget()
        {
            // The stack is a plain object with no Renode plumbing of its own; give it ours.
            stack.DebugLog = message => this.Log(LogLevel.Debug, "{0}", message);
            stack.WarningLog = message => this.Log(LogLevel.Warning, "{0}", message);
            stack.InformationHandler = OnInformation;
            stack.LinkEstablishedHandler = OnLinkEstablished;
        }

        public override void Reset()
        {
            base.Reset();
            lock(locker)
            {
                // Reset() is called from the base constructor. Our field initializers have already
                // run by then (C# runs the derived ones before the base constructor), but only
                // because stack and responseQueue are initializers rather than constructor-body
                // assignments - the same gotcha the base class documents.
                responseQueue.Clear();
                stack.Reset();
            }
        }

        // --------------------------------------------------------------------------------------
        // Capabilities advertised in ACT_INFORMATION - settable from a .repl
        // --------------------------------------------------------------------------------------

        public byte ProtocolVersion
        {
            get => stack.ProtocolVersion;
            set => stack.ProtocolVersion = value;
        }

        public SWPProtocol.SupportedLlc SupportedLlcs
        {
            get => stack.SupportedLlcs;
            set => stack.SupportedLlcs = value;
        }

        // Largest LLC payload the UICC can receive in one frame, advertised in ACT_INFORMATION.
        public int MaxFramePayloadSize
        {
            get => stack.MaxFramePayloadSize;
            set => stack.MaxFramePayloadSize = value;
        }

        // Power modes the UICC supports: bit0 low power, bit1 full power.
        public byte SupportedPowerModes
        {
            get => stack.SupportedPowerModes;
            set => stack.SupportedPowerModes = value;
        }

        // Largest SHDLC window the UICC will accept in the RSET handshake.
        public int MaxWindowSize
        {
            get => stack.MaxWindowSize;
            set => stack.MaxWindowSize = value;
        }

        // Whether the UICC offers selective reject in the RSET handshake.
        public bool SelectiveRejectSupport
        {
            get => stack.SelectiveRejectSupport;
            set => stack.SelectiveRejectSupport = value;
        }

        // --------------------------------------------------------------------------------------
        // Observable state
        // --------------------------------------------------------------------------------------

        // True once the SHDLC RSET/UA handshake has completed.
        public bool LinkEstablished => stack.LinkEstablished;

        // SHDLC window size agreed with the CLF (the smaller of the two proposals).
        public int WindowSize => stack.WindowSize;

        // Number of REJ frames this target has sent (an out-of-sequence I-frame arrived).
        public int RejectsSent => stack.RejectsSent;

        // Information field of the last I-frame accepted - the control byte stripped - hex-encoded.
        // (The whole payload of the last frame received, control field included, is LastPayloadInHex.)
        public string LastReceivedPayloadHex =>
            Misc.PrettyPrintCollectionHex(stack.LastReceivedInformation);

        // Queues one payload to be returned in an I-frame in answer to the next I-frame received.
        public void EnqueueResponsePayload(IEnumerable<byte> payload)
        {
            lock(locker)
            {
                responseQueue.Enqueue(payload.ToArray());
            }
        }

        // Monitor-friendly helper: queue one response payload from a hex string, e.g. "0102ab".
        public void EnqueueResponsePayloadHex(string hexPayload)
        {
            EnqueueResponsePayload(Misc.HexStringToByteArray(hexPayload));
        }

        // --------------------------------------------------------------------------------------
        // Hooks for proprietary targets
        // --------------------------------------------------------------------------------------

        // Called with the information field of a well-sequenced SHDLC I-frame. Return a payload to
        // answer with an I-frame of our own (the acknowledgement rides along in its N(R)); return
        // null or an empty array to answer with a bare RR acknowledgement.
        //
        // Default: the next payload queued with EnqueueResponsePayload, else null.
        //
        // Runs with the peripheral's lock held; do not call back into this peripheral from it.
        protected virtual byte[] OnInformation(byte[] information)
        {
            return responseQueue.Count > 0 ? responseQueue.Dequeue() : null;
        }

        // Called once the SHDLC RSET/UA handshake has completed. Default: no-op.
        protected virtual void OnLinkEstablished()
        {
        }

        // Transmits an I-frame on the UICC's own initiative (SWP is full duplex, so the UICC may
        // transmit on S2 without being polled). Raises FrameAvailable with the complete wire frame.
        protected void SendInformation(byte[] information)
        {
            byte[] payload;
            lock(locker)
            {
                payload = stack.BuildInformation(information);
                if(payload == null)
                {
                    return;
                }
            }
            this.Log(LogLevel.Debug, "Transmitting an unsolicited {0}-byte I-frame", information?.Length ?? 0);
            TransmitPayload(payload);
        }

        // --------------------------------------------------------------------------------------
        // Transport hooks: everything the stack decides, and nothing the transport invented
        // --------------------------------------------------------------------------------------

        protected override byte[] OnActivated()
        {
            var payload = stack.Activate();
            PublishStackState();
            return payload;
        }

        protected override void OnDeactivated()
        {
            lock(locker)
            {
                stack.Deactivate();
                PublishStackState();
            }
        }

        protected override byte[] OnPayloadReceived(byte[] payload)
        {
            var answer = stack.HandlePayload(payload);
            PublishStackState();
            return answer;
        }

        // The transport can only see S1; the ACT state and the power mode are the stack's to report.
        private void PublishStackState()
        {
            InterfaceState = stack.InterfaceState;
            PowerMode = stack.PowerMode;
        }

        private readonly SWPTargetStack stack = new SWPTargetStack();
        private readonly Queue<byte[]> responseQueue = new Queue<byte[]>();
    }
}
