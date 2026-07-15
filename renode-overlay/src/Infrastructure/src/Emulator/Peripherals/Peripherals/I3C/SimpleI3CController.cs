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
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.I3C
{
    // A simple, agnostic I3C controller (master).
    //
    // Targets implementing II3CPeripheral register on it by address, just like I2C slaves register
    // on an I2C controller (repl: `slave: I3C.DummyI3CSlave @ i3c 0x08`). Transfers are driven at
    // the transaction level, either from a C# test-bench or from the monitor / robot tests:
    //   - AssignDynamicAddresses performs a simplified ENTDAA enumeration,
    //   - WritePrivate / ReadPrivate perform SDR private transfers,
    //   - SendBroadcastCommandCode / SendDirectCommandCode issue CCCs,
    //   - In-Band Interrupts raised by targets are captured and drive the IRQ GPIO line.
    //
    // It is memory-mappable (with a no-op register window) only so it can sit on the sysbus like any
    // other controller; it is not meant to model a specific SoC's I3C register interface.
    public class SimpleI3CController : SimpleContainer<II3CPeripheral>, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public SimpleI3CController(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            Connections = new Dictionary<int, IGPIO> { { 0, IRQ } };
            interruptHandlers = new Dictionary<II3CPeripheral, Action<II3CPeripheral, byte[]>>();
        }

        public override void Register(II3CPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            base.Register(peripheral, registrationPoint);
            Action<II3CPeripheral, byte[]> handler = HandleInBandInterrupt;
            interruptHandlers[peripheral] = handler;
            peripheral.InBandInterruptRequested += handler;
        }

        public override void Unregister(II3CPeripheral peripheral)
        {
            if(interruptHandlers.TryGetValue(peripheral, out var handler))
            {
                peripheral.InBandInterruptRequested -= handler;
                interruptHandlers.Remove(peripheral);
            }
            base.Unregister(peripheral);
        }

        public override void Reset()
        {
            IRQ.Unset();
            LastInBandInterruptAddress = -1;
            lastInBandInterruptPayload = new byte[0];
        }

        // No-op register window - see the class summary.
        public uint ReadDoubleWord(long offset)
        {
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
        }

        // Simplified ENTDAA: assign each registered target a dynamic address equal to its
        // registration address. A real bus would arbitrate on the Provisioned IDs; here the mapping
        // is fixed, which is enough for a functional model.
        public void AssignDynamicAddresses()
        {
            foreach(var entry in ChildCollection)
            {
                var address = (byte)entry.Key;
                entry.Value.DynamicAddress = address;
                this.Log(LogLevel.Info,
                    "ENTDAA: assigned dynamic address 0x{0:X2} (PID 0x{1:X12}, BCR 0x{2:X2}, DCR 0x{3:X2})",
                    address, entry.Value.ProvisionedId, entry.Value.BusCharacteristics, entry.Value.DeviceCharacteristics);
            }
        }

        // SDR private write to a single target.
        public void WritePrivate(int address, byte[] data)
        {
            if(!TryGetTarget(address, out var target))
            {
                return;
            }
            target.Write(data);
            target.FinishTransmission();
        }

        // SDR private read from a single target.
        public byte[] ReadPrivate(int address, int count)
        {
            if(!TryGetTarget(address, out var target))
            {
                return new byte[0];
            }
            var result = target.Read(count);
            target.FinishTransmission();
            return result;
        }

        // Monitor-friendly helper: private write of hex-encoded data (e.g. "0102ab").
        public void WritePrivateHex(int address, string hexData)
        {
            WritePrivate(address, Misc.HexStringToByteArray(hexData));
        }

        // Monitor-friendly helper: private read returning hex-encoded data.
        public string ReadPrivateHex(int address, int count)
        {
            return Misc.PrettyPrintCollectionHex(ReadPrivate(address, count));
        }

        // Broadcast CCC delivered to every registered target.
        public void SendBroadcastCommandCode(int code, byte[] payload = null)
        {
            this.Log(LogLevel.Info, "Broadcast CCC 0x{0:X2}", code);
            foreach(var target in ChildCollection.Values)
            {
                target.HandleCommonCommandCode((byte)code, payload);
            }
        }

        // Monitor-friendly helper: broadcast CCC with hex-encoded payload.
        public void SendBroadcastCommandCodeHex(int code, string hexPayload)
        {
            SendBroadcastCommandCode(code, Misc.HexStringToByteArray(hexPayload));
        }

        // Direct CCC delivered to a single target.
        public void SendDirectCommandCode(int code, int address, byte[] payload = null)
        {
            if(!TryGetTarget(address, out var target))
            {
                return;
            }
            this.Log(LogLevel.Info, "Direct CCC 0x{0:X2} to 0x{1:X2}", code, address);
            target.HandleCommonCommandCode((byte)code, payload);
        }

        // Monitor-friendly helper: direct CCC with hex-encoded payload.
        public void SendDirectCommandCodeHex(int code, int address, string hexPayload)
        {
            SendDirectCommandCode(code, address, Misc.HexStringToByteArray(hexPayload));
        }

        // Clears the pending IBI indication (drops the IRQ line).
        public void AcknowledgeInBandInterrupt()
        {
            IRQ.Unset();
        }

        public long Size => 0x100;

        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Address of the target that raised the most recent IBI, or -1 if none since reset.
        public int LastInBandInterruptAddress { get; private set; } = -1;

        // Payload (MDB + data) of the most recent IBI, hex-encoded (monitor-readable).
        public string LastInBandInterruptPayloadHex => Misc.PrettyPrintCollectionHex(lastInBandInterruptPayload);

        private bool TryGetTarget(int address, out II3CPeripheral target)
        {
            if(!TryGetByAddress(address, out target))
            {
                this.Log(LogLevel.Warning, "No I3C target registered at address 0x{0:X2}", address);
                return false;
            }
            return true;
        }

        private void HandleInBandInterrupt(II3CPeripheral target, byte[] payload)
        {
            var match = ChildCollection.Where(x => ReferenceEquals(x.Value, target)).Select(x => (int?)x.Key).FirstOrDefault();
            LastInBandInterruptAddress = match ?? -1;
            lastInBandInterruptPayload = payload;
            this.Log(LogLevel.Info, "IBI from target 0x{0:X2}, payload {1}",
                LastInBandInterruptAddress, Misc.PrettyPrintCollectionHex(payload));
            IRQ.Set();
        }

        private byte[] lastInBandInterruptPayload = new byte[0];
        private readonly Dictionary<II3CPeripheral, Action<II3CPeripheral, byte[]>> interruptHandlers;
    }
}
