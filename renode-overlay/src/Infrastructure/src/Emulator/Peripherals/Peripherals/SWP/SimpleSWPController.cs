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
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // A simple, agnostic SWP master - the CLF (Contactless Front-end) side of an ETSI TS 102 613
    // link, modelled as a TRANSPORT.
    //
    // SWP is point to point: one CLF, one wire, one target. There is no addressing on the wire and
    // nothing to select, so this controller holds exactly one target and its API takes no line or
    // address argument. A CLF with two SWP interfaces is two controllers, each with its own target -
    // that is what the hardware is, and it keeps every model here honest about the fact that a
    // single wire connects exactly two endpoints.
    //
    //     swp:  SWP.SimpleSWPController @ sysbus
    //     uicc: SWP.SimpleSWPPeripheral @ swp
    //
    // It does the two things the wire does: it owns the power state, and it carries opaque bytes in
    // both directions. It implements no framing, no CRC, no ACT activation sequence and no SHDLC -
    // those layers belong to whatever is under test on either end. See ISWPPeripheral for the
    // reasoning, and tools/swp-reference/ for a standalone implementation of them.
    //
    // In particular, PowerUp does NOT run an activation sequence. It drives S1 and nothing more; the
    // ACT exchange, if the stack under test performs one, is just the first bytes to cross the wire.
    //
    // It registers on the sysbus WITHOUT an address. The CLF is a separate chip on the far end of
    // the SWP line, not a block inside the SoC: it has no register map, so claiming an address range
    // would be fiction and would make the bus lie about what is actually memory-mapped. The monitor
    // still reaches it as `sysbus.<name>`.
    public class SimpleSWPController :
        IPeripheral, IPeripheralContainer<ISWPPeripheral, NullRegistrationPoint>, INumberedGPIOOutput
    {
        public SimpleSWPController(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
            Connections = new Dictionary<int, IGPIO> { { 0, IRQ } };
        }

        // --------------------------------------------------------------------------------------
        // The single target on the wire
        // --------------------------------------------------------------------------------------

        public void Register(ISWPPeripheral peripheral, NullRegistrationPoint registrationPoint)
        {
            if(target != null)
            {
                throw new RegistrationException(
                    "SWP is point to point: this controller already has a target. Use a second controller for a second SWP interface.");
            }
            target = peripheral;
            peripheral.DataAvailable += HandleTargetData;
        }

        public void Unregister(ISWPPeripheral peripheral)
        {
            if(!ReferenceEquals(target, peripheral))
            {
                return;
            }
            peripheral.DataAvailable -= HandleTargetData;
            target = null;
        }

        public IEnumerable<NullRegistrationPoint> GetRegistrationPoints(ISWPPeripheral peripheral)
        {
            return ReferenceEquals(target, peripheral)
                ? new[] { NullRegistrationPoint.Instance }
                : Enumerable.Empty<NullRegistrationPoint>();
        }

        public IEnumerable<IRegistered<ISWPPeripheral, NullRegistrationPoint>> Children
        {
            get
            {
                return target == null
                    ? Enumerable.Empty<IRegistered<ISWPPeripheral, NullRegistrationPoint>>()
                    : new[] { Registered.Create(target, NullRegistrationPoint.Instance) };
            }
        }

        // The target on the other end of the wire, or null if none is registered.
        public ISWPPeripheral Target => target;

        public void Reset()
        {
            IRQ.Unset();
            lastReceived = Empty;
            BytesSent = 0;
            BytesReceived = 0;
        }

        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // --------------------------------------------------------------------------------------
        // Power (physical layer - the CLF owns it)
        // --------------------------------------------------------------------------------------

        // Drives S1. This is power only: no bytes are exchanged and no activation sequence is run -
        // if the stack under test performs one, it is simply the first traffic to cross the wire.
        public void PowerUp()
        {
            SetPower(true);
        }

        // Drives S1 low. The target drops whatever per-session state it holds.
        public void PowerDown()
        {
            SetPower(false);
        }

        public void SetPower(bool powered)
        {
            if(!TryGetTarget(out var swpTarget))
            {
                return;
            }
            swpTarget.SetPower(powered);
            this.Log(LogLevel.Info, "SWP line {0}", powered ? "powered" : "unpowered");
        }

        public bool Powered => target != null && target.Powered;

        // --------------------------------------------------------------------------------------
        // Data transfer
        // --------------------------------------------------------------------------------------

        // One full-duplex slot: drives `data` on S1 and returns whatever the target drove on S2 in
        // the same slot. The bytes are opaque - no framing is added or removed. Either direction may
        // be empty.
        public byte[] Transfer(byte[] data)
        {
            data = data ?? Empty;
            if(!TryGetTarget(out var swpTarget))
            {
                return Empty;
            }
            if(!swpTarget.Powered)
            {
                this.Log(LogLevel.Warning, "The SWP line is not powered - call PowerUp first");
                return Empty;
            }

            BytesSent += data.Length;
            this.Log(LogLevel.Noisy, "Driving {0} byte(s) on S1", data.Length);
            var answer = swpTarget.Transfer(data) ?? Empty;
            if(answer.Length > 0)
            {
                BytesReceived += answer.Length;
                lastReceived = answer;
            }
            return answer;
        }

        // Monitor-friendly helper: transfer hex-encoded bytes, get the hex-encoded answer back.
        public string TransferHex(string hexData)
        {
            return Misc.PrettyPrintCollectionHex(Transfer(Misc.HexStringToByteArray(hexData)));
        }

        // Gives the target a slot to drive S2 without the CLF sending anything. SWP is full duplex,
        // so an empty S1 slot is a legitimate way to let the far end talk.
        public byte[] Receive()
        {
            return Transfer(Empty);
        }

        public string ReceiveHex()
        {
            return Misc.PrettyPrintCollectionHex(Receive());
        }

        // --------------------------------------------------------------------------------------
        // Observable state
        // --------------------------------------------------------------------------------------

        // The most recent bytes received, hex-encoded (monitor-readable).
        public string LastReceivedHex => Misc.PrettyPrintCollectionHex(lastReceived);

        public int BytesSent { get; private set; }
        public int BytesReceived { get; private set; }

        // Clears the pending indication (drops the IRQ line).
        public void AcknowledgeInterrupt()
        {
            IRQ.Unset();
        }

        private bool TryGetTarget(out ISWPPeripheral swpTarget)
        {
            swpTarget = target;
            if(swpTarget == null)
            {
                this.Log(LogLevel.Warning, "No SWP target registered on this controller");
                return false;
            }
            return true;
        }

        // The target drove bytes on S2 on its own initiative. Record them and raise the IRQ line so
        // firmware or a test can react.
        private void HandleTargetData(ISWPPeripheral source, byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }
            BytesReceived += data.Length;
            lastReceived = data;
            this.Log(LogLevel.Info, "Unsolicited {0} byte(s) from the target", data.Length);
            IRQ.Set();
        }

        private ISWPPeripheral target;
        private byte[] lastReceived = new byte[0];

        private readonly IMachine machine;

        private static readonly byte[] Empty = new byte[0];
    }
}
