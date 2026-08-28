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
    // link, modelled as a TRANSPORT.
    //
    // It does two things, which are the two things the wire does: it owns the power state of each
    // line, and it carries opaque bytes in both directions. It implements no framing, no CRC, no
    // ACT activation sequence and no SHDLC - those layers belong to whatever is under test on
    // either end. See ISWPPeripheral for the reasoning, and tools/swp-reference/ for a standalone
    // implementation of them.
    //
    // In particular, note that PowerUp does NOT run an activation sequence. It drives S1 and
    // nothing more; the ACT exchange, if the stack under test performs one, is just the first bytes
    // to cross the wire afterwards.
    //
    // SWP is point to point, but a CLF commonly has more than one SWP line (one to the UICC, one to
    // an embedded SE). Targets therefore register by SWP *line number*, like any Renode bus child:
    //
    //     swp:  SWP.SimpleSWPController @ sysbus
    //     uicc: SWP.SimpleSWPPeripheral @ swp 0
    //
    // It registers on the sysbus WITHOUT an address. The CLF is a separate chip on the far end of
    // the SWP line, not a block inside the SoC: it has no register map, so claiming an address range
    // would be fiction and would make the bus lie about what is actually memory-mapped. The monitor
    // still reaches it as `sysbus.<name>`.
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
            Action<ISWPPeripheral, byte[]> handler = HandleTargetData;
            dataHandlers[peripheral] = handler;
            peripheral.DataAvailable += handler;
        }

        public override void Unregister(ISWPPeripheral peripheral)
        {
            if(dataHandlers.TryGetValue(peripheral, out var handler))
            {
                peripheral.DataAvailable -= handler;
                dataHandlers.Remove(peripheral);
            }
            base.Unregister(peripheral);
        }

        public override void Reset()
        {
            IRQ.Unset();
            LastReceivedLine = -1;
            lastReceived = Empty;
            BytesSent = 0;
            BytesReceived = 0;
        }

        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // --------------------------------------------------------------------------------------
        // Power (physical layer - the CLF owns it)
        // --------------------------------------------------------------------------------------

        // Drives S1 on the given line. This is power only: no bytes are exchanged, and no
        // activation sequence is run - if the stack under test performs one, it is simply the first
        // traffic to cross the wire afterwards.
        public void PowerUp(int line)
        {
            SetPower(line, true);
        }

        // Drives S1 low. The target drops whatever per-session state it holds.
        public void PowerDown(int line)
        {
            SetPower(line, false);
        }

        public void SetPower(int line, bool powered)
        {
            if(!TryGetTarget(line, out var target))
            {
                return;
            }
            target.SetPower(powered);
            this.Log(LogLevel.Info, "SWP line {0} {1}", line, powered ? "powered" : "unpowered");
        }

        // Powers every registered line. Convenience for the monitor.
        public void PowerUpAll()
        {
            foreach(var line in ChildCollection.Keys.OrderBy(x => x).ToArray())
            {
                PowerUp(line);
            }
        }

        public bool IsPowered(int line)
        {
            return TryGetByAddress(line, out var target) && target.Powered;
        }

        // Power state of SWP line 0 - the common single-UICC case, readable from the monitor.
        public bool Powered => IsPowered(0);

        // --------------------------------------------------------------------------------------
        // Data transfer
        // --------------------------------------------------------------------------------------

        // One full-duplex slot on the given line: drives `data` on S1 and returns whatever the
        // target drove on S2 in the same slot. The bytes are opaque - no framing is added or
        // removed. Either direction may be empty.
        public byte[] Transfer(int line, byte[] data)
        {
            data = data ?? Empty;
            if(!TryGetTarget(line, out var target))
            {
                return Empty;
            }
            if(!target.Powered)
            {
                this.Log(LogLevel.Warning, "SWP line {0} is not powered - call PowerUp first", line);
                return Empty;
            }

            BytesSent += data.Length;
            this.Log(LogLevel.Noisy, "SWP line {0}: driving {1} byte(s) on S1", line, data.Length);
            var answer = target.Transfer(data) ?? Empty;
            if(answer.Length > 0)
            {
                BytesReceived += answer.Length;
                lastReceived = answer;
                LastReceivedLine = line;
            }
            return answer;
        }

        // Monitor-friendly helper: transfer hex-encoded bytes, get the hex-encoded answer back.
        public string TransferHex(int line, string hexData)
        {
            return Misc.PrettyPrintCollectionHex(Transfer(line, Misc.HexStringToByteArray(hexData)));
        }

        // Gives the target a slot to drive S2 without the CLF sending anything. SWP is full duplex,
        // so an empty S1 slot is a legitimate way to let the far end talk.
        public byte[] Receive(int line)
        {
            return Transfer(line, Empty);
        }

        public string ReceiveHex(int line)
        {
            return Misc.PrettyPrintCollectionHex(Receive(line));
        }

        // --------------------------------------------------------------------------------------
        // Observable state
        // --------------------------------------------------------------------------------------

        // SWP line the most recent bytes came from, or -1 if none since reset.
        public int LastReceivedLine { get; private set; } = -1;

        // The most recent bytes received, hex-encoded (monitor-readable).
        public string LastReceivedHex => Misc.PrettyPrintCollectionHex(lastReceived);

        public int BytesSent { get; private set; }
        public int BytesReceived { get; private set; }

        // Clears the pending indication (drops the IRQ line).
        public void AcknowledgeInterrupt()
        {
            IRQ.Unset();
        }

        // Returns the target registered on the given SWP line, or null if there is none.
        public ISWPPeripheral GetTarget(int line)
        {
            return TryGetByAddress(line, out var target) ? target : null;
        }

        protected bool TryGetTarget(int line, out ISWPPeripheral target)
        {
            if(!TryGetByAddress(line, out target))
            {
                this.Log(LogLevel.Warning, "No SWP target registered on line {0}", line);
                return false;
            }
            return true;
        }

        // A target drove bytes on S2 on its own initiative. Record them and raise the IRQ line so
        // firmware or a test can react.
        private void HandleTargetData(ISWPPeripheral target, byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }
            var line = ChildCollection.Where(x => ReferenceEquals(x.Value, target))
                .Select(x => (int?)x.Key).FirstOrDefault() ?? -1;

            BytesReceived += data.Length;
            lastReceived = data;
            LastReceivedLine = line;
            this.Log(LogLevel.Info, "Unsolicited {0} byte(s) from the target on SWP line {1}", data.Length, line);
            IRQ.Set();
        }

        private byte[] lastReceived = new byte[0];

        // Field initializer, not a constructor-body assignment: Register may run before Reset.
        private readonly Dictionary<ISWPPeripheral, Action<ISWPPeripheral, byte[]>> dataHandlers =
            new Dictionary<ISWPPeripheral, Action<ISWPPeripheral, byte[]>>();

        private static readonly byte[] Empty = new byte[0];
    }
}
