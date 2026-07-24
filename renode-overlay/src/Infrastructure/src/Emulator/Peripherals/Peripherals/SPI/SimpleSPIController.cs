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

namespace Antmicro.Renode.Peripherals.SPI
{
    // A simple, agnostic SPI controller (master).
    //
    // Targets implementing ISPIPeripheral register on it by chip-select index, like SPI slaves register
    // on any SPI controller (repl: `slave: Mocks.DummySPITarget @ spi 0`). Transfers are driven at the
    // transaction level from a C# test-bench or from the monitor / robot tests:
    //   - Transfer / TransferHex perform a full-duplex exchange with the selected target,
    //   - a target's data-ready interrupt (for targets built on SimpleSPIPeripheral) is captured and
    //     drives the IRQ GPIO line.
    //
    // It is memory-mappable (with a no-op register window) only so it can sit on the sysbus like any
    // other controller; it is not meant to model a specific SoC's SPI register interface.
    public class SimpleSPIController : SimpleContainer<ISPIPeripheral>, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public SimpleSPIController(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            Connections = new Dictionary<int, IGPIO> { { 0, IRQ } };
            interruptHandlers = new Dictionary<SimpleSPIPeripheral, Action<ISPIPeripheral, byte[]>>();
        }

        public override void Register(ISPIPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            base.Register(peripheral, registrationPoint);
            if(peripheral is SimpleSPIPeripheral source)
            {
                Action<ISPIPeripheral, byte[]> handler = HandleInterrupt;
                interruptHandlers[source] = handler;
                source.InterruptRequested += handler;
            }
        }

        public override void Unregister(ISPIPeripheral peripheral)
        {
            if(peripheral is SimpleSPIPeripheral source && interruptHandlers.TryGetValue(source, out var handler))
            {
                source.InterruptRequested -= handler;
                interruptHandlers.Remove(source);
            }
            base.Unregister(peripheral);
        }

        public override void Reset()
        {
            IRQ.Unset();
            LastInterruptChipSelect = -1;
            lastInterruptPayload = new byte[0];
        }

        // No-op register window - see the class summary.
        public uint ReadDoubleWord(long offset)
        {
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
        }

        // Full-duplex SPI transaction with the target at the given chip select: clocks out each byte of
        // data and returns the MISO byte received for it, then deasserts chip select. Returns the
        // received bytes (one per byte sent).
        public byte[] Transfer(int chipSelect, byte[] data)
        {
            if(!TryGetTarget(chipSelect, out var target))
            {
                return new byte[0];
            }
            var result = new byte[data.Length];
            for(var i = 0; i < data.Length; i++)
            {
                result[i] = target.Transmit(data[i]);
            }
            target.FinishTransmission();
            return result;
        }

        // Monitor-friendly helper: full-duplex transfer of hex-encoded data, returning hex-encoded MISO.
        public string TransferHex(int chipSelect, string hexData)
        {
            return Misc.PrettyPrintCollectionHex(Transfer(chipSelect, Misc.HexStringToByteArray(hexData)));
        }

        // Clears the pending interrupt indication (drops the IRQ line).
        public void AcknowledgeInterrupt()
        {
            IRQ.Unset();
        }

        public long Size => 0x100;

        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Chip select of the target that raised the most recent interrupt, or -1 if none since reset.
        public int LastInterruptChipSelect { get; private set; } = -1;

        // Data carried by the most recent interrupt, hex-encoded (monitor-readable).
        public string LastInterruptPayloadHex => Misc.PrettyPrintCollectionHex(lastInterruptPayload);

        // Returns the target registered at the given chip select, or null if none is registered there.
        public ISPIPeripheral GetTarget(int chipSelect)
        {
            return TryGetByAddress(chipSelect, out var target) ? target : null;
        }

        private bool TryGetTarget(int chipSelect, out ISPIPeripheral target)
        {
            if(!TryGetByAddress(chipSelect, out target))
            {
                this.Log(LogLevel.Warning, "No SPI target registered at chip select {0}", chipSelect);
                return false;
            }
            return true;
        }

        private void HandleInterrupt(ISPIPeripheral target, byte[] payload)
        {
            var match = ChildCollection.Where(x => ReferenceEquals(x.Value, target)).Select(x => (int?)x.Key).FirstOrDefault();
            LastInterruptChipSelect = match ?? -1;
            lastInterruptPayload = payload;
            this.Log(LogLevel.Info, "Interrupt from target at chip select {0}, data {1}",
                LastInterruptChipSelect, Misc.PrettyPrintCollectionHex(payload));
            IRQ.Set();
        }

        private byte[] lastInterruptPayload = new byte[0];
        private readonly Dictionary<SimpleSPIPeripheral, Action<ISPIPeripheral, byte[]>> interruptHandlers;
    }
}
