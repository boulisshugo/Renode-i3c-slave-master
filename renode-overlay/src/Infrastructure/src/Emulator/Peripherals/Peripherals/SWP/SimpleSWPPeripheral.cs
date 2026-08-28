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
    // A simple, agnostic SWP target (the UICC side of an ETSI TS 102 613 link).
    //
    // It is a transport endpoint and nothing more: it carries opaque bytes in both directions and
    // tracks whether the CLF has powered the interface. It implements no framing, no CRC, no ACT
    // activation sequence and no SHDLC - see ISWPPeripheral for why, and tools/swp-reference/ for a
    // standalone implementation of those layers if your test-bench wants one.
    //
    // Out of the box it answers each transfer with the next block queued by EnqueueResponse (or
    // nothing when the queue is empty). Subclass it and override OnTransfer for proprietary
    // behaviour, and call SendData to drive bytes on S2 without being polled.
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
                responseQueue.Clear();
                trace.Clear();
                lastReceived = Empty;
                lastSent = Empty;
                BytesReceived = 0;
                BytesSent = 0;
                Powered = false;
            }
        }

        // --------------------------------------------------------------------------------------
        // ISWPPeripheral
        // --------------------------------------------------------------------------------------

        public bool Powered { get; private set; }

        public virtual void SetPower(bool powered)
        {
            bool changed;
            lock(locker)
            {
                changed = powered != Powered;
                Powered = powered;
                if(!powered)
                {
                    // S1 low: the interface is unpowered and keeps no per-session state.
                    responseQueue.Clear();
                    lastReceived = Empty;
                    lastSent = Empty;
                }
            }
            if(!changed)
            {
                return;
            }
            this.Log(LogLevel.Debug, "Interface {0} by the CLF", powered ? "powered" : "unpowered");
            OnPowerChanged(powered);
        }

        public virtual byte[] Transfer(byte[] data)
        {
            data = data ?? Empty;
            lock(locker)
            {
                if(!Powered)
                {
                    this.Log(LogLevel.Warning, "Transfer of {0} byte(s) while the interface is unpowered - ignored",
                        data.Length);
                    return Empty;
                }

                if(data.Length > 0)
                {
                    BytesReceived += data.Length;
                    lastReceived = data;
                    Record(SWPDirection.Received, data);
                }

                var outgoing = OnTransfer(data) ?? Empty;
                if(outgoing.Length > 0)
                {
                    BytesSent += outgoing.Length;
                    lastSent = outgoing;
                    Record(SWPDirection.Sent, outgoing);
                }
                return outgoing;
            }
        }

        public event Action<ISWPPeripheral, byte[]> DataAvailable;

        // --------------------------------------------------------------------------------------
        // Observable state - monitor and robot friendly
        // --------------------------------------------------------------------------------------

        // Bytes of the last block received from / sent to the CLF, hex-encoded.
        public string LastReceivedHex => Misc.PrettyPrintCollectionHex(lastReceived);
        public string LastSentHex => Misc.PrettyPrintCollectionHex(lastSent);

        public int BytesReceived { get; private set; }
        public int BytesSent { get; private set; }

        // How many blocks the rolling trace keeps. 0 disables it; the Last* properties stay live.
        public int TraceDepth
        {
            get => traceDepth;
            set
            {
                lock(locker)
                {
                    traceDepth = Math.Max(0, value);
                    TrimTrace();
                }
            }
        }

        // The rolling trace of raw bytes crossing the wire, one block per line.
        public string TraceHex
        {
            get
            {
                lock(locker)
                {
                    return trace.Count == 0
                        ? "(nothing traced)"
                        : string.Join(Environment.NewLine, trace.Select(x => x.Item1 + "  " + PlainHex(x.Item2)));
                }
            }
        }

        public void ClearTrace()
        {
            lock(locker)
            {
                trace.Clear();
            }
        }

        // Queues one block to be driven on S2 in answer to the next transfer.
        public void EnqueueResponse(IEnumerable<byte> data)
        {
            lock(locker)
            {
                responseQueue.Enqueue(data.ToArray());
            }
        }

        // Monitor-friendly helper: queue one response block from a hex string, e.g. "0102ab".
        public void EnqueueResponseHex(string hexData)
        {
            EnqueueResponse(Misc.HexStringToByteArray(hexData));
        }

        // Monitor-friendly helper: push a raw block at the target as if the CLF had driven it, and
        // get back whatever the target drove on S2 in the same slot.
        public string TransferHex(string hexData)
        {
            return Misc.PrettyPrintCollectionHex(Transfer(Misc.HexStringToByteArray(hexData)));
        }

        // --------------------------------------------------------------------------------------
        // Hooks for proprietary targets
        // --------------------------------------------------------------------------------------

        // Called for every full-duplex slot. `incoming` is what the CLF drove on S1 (possibly
        // empty); return what this target drives on S2 in the same slot, or null for nothing.
        // The bytes are opaque - whatever protocol runs on the line is yours to implement here.
        //
        // Default: the next block queued with EnqueueResponse, else nothing.
        protected virtual byte[] OnTransfer(byte[] incoming)
        {
            return responseQueue.Count > 0 ? responseQueue.Dequeue() : null;
        }

        // Called when the CLF powers the interface up (true) or drives S1 low (false).
        protected virtual void OnPowerChanged(bool powered)
        {
        }

        // Drives bytes on S2 without being polled. Raises DataAvailable.
        protected void SendData(byte[] data)
        {
            data = data ?? Empty;
            lock(locker)
            {
                if(!Powered)
                {
                    this.Log(LogLevel.Warning, "Cannot drive S2: the interface is not powered");
                    return;
                }
                if(data.Length == 0)
                {
                    return;
                }
                BytesSent += data.Length;
                lastSent = data;
                Record(SWPDirection.Sent, data);
            }
            this.Log(LogLevel.Debug, "Driving {0} unsolicited byte(s) on S2", data.Length);
            DataAvailable?.Invoke(this, data);
        }

        // --------------------------------------------------------------------------------------

        private void Record(SWPDirection direction, byte[] data)
        {
            if(traceDepth <= 0)
            {
                return;
            }
            trace.Enqueue(Tuple.Create(direction == SWPDirection.Received ? "in " : "out", data));
            TrimTrace();
        }

        private void TrimTrace()
        {
            while(trace.Count > traceDepth)
            {
                trace.Dequeue();
            }
        }

        private static string PlainHex(byte[] data)
        {
            return string.Concat(data.Select(x => x.ToString("X2")));
        }

        private enum SWPDirection
        {
            Received,
            Sent,
        }

        private byte[] lastReceived = new byte[0];
        private byte[] lastSent = new byte[0];
        private int traceDepth = DefaultTraceDepth;

        private readonly Queue<Tuple<string, byte[]>> trace = new Queue<Tuple<string, byte[]>>();
        private readonly Queue<byte[]> responseQueue = new Queue<byte[]>();
        private readonly object locker = new object();

        private const int DefaultTraceDepth = 32;

        private static readonly byte[] Empty = new byte[0];
    }
}
