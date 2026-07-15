//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

namespace Antmicro.Renode.Peripherals.I3C
{
    // Protocol-agnostic I3C target (slave) contract.
    //
    // It mirrors the transaction-level shape of II2CPeripheral (Write/Read/FinishTransmission)
    // and adds the features that make a device an I3C target rather than a plain I2C one:
    //   - a dynamic address assigned by the controller (ENTDAA), plus the identifiers used
    //     during enumeration (Provisioned ID, Bus/Device Characteristics registers),
    //   - Common Command Code handling (broadcast and direct CCCs),
    //   - In-Band Interrupt (IBI) requests raised by the target towards the controller.
    //
    // Proprietary target models should implement this interface (or subclass SimpleI3CPeripheral)
    // and can then be registered on any II3CPeripheral container, e.g. SimpleI3CController.
    public interface II3CPeripheral : IPeripheral
    {
        // 48-bit Provisioned ID, used as the arbitration value during ENTDAA enumeration.
        ulong ProvisionedId { get; }

        // Bus Characteristics Register - advertises target capabilities (e.g. IBI support).
        byte BusCharacteristics { get; }

        // Device Characteristics Register - advertises the device type.
        byte DeviceCharacteristics { get; }

        // Legacy I2C static address, or 0 when the target has no static address.
        byte StaticAddress { get; }

        // Dynamic address assigned by the controller during ENTDAA; 0 means unassigned.
        byte DynamicAddress { get; set; }

        // SDR private write: data sent from the controller to this target.
        void Write(byte[] data);

        // SDR private read: the controller requests up to count bytes from this target.
        byte[] Read(int count = 1);

        // Marks the end of a transfer (a Stop or repeated Start on the bus).
        void FinishTransmission();

        // Handles a Common Command Code. payload carries any defining/sub-command bytes
        // that follow the code (may be null or empty for simple CCCs).
        void HandleCommonCommandCode(byte code, byte[] payload);

        // Raised by the target to request an In-Band Interrupt. The payload starts with the
        // Mandatory Data Byte (MDB) followed by any optional IBI data bytes.
        event Action<II3CPeripheral, byte[]> InBandInterruptRequested;
    }
}
