//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
namespace Antmicro.Renode.Peripherals.SPI;

// Optional extension of ISPIPeripheral for targets that want to react to chip-select (NSS)
// assertion/deassertion - e.g. to frame a transaction (command vs. polled response phase) or reset
// per-transaction state. SimpleSPIController calls Select on registered targets that implement it.
public interface ISelectableSPIPeripheral: ISPIPeripheral
{
    public void Select(bool select);
}
