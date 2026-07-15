# Agnostic I3C master & slave for Renode

Generic, reusable **I3C controller (master)** and **I3C target (slave)** models for
[Renode](https://github.com/renode/renode). They are deliberately *agnostic*: they do not model any
specific SoC's register map. Instead they provide a small, well-defined transaction-level interface
that proprietary I3C target implementations can plug into, and a controller that can drive them from a
C# test-bench, the Renode monitor, or robot tests.

## Why transaction-level (method calls) rather than raw SDA/SCL?

The I3C models communicate at the *transaction* level: the controller invokes `Write`, `Read`,
`HandleCommonCommandCode` and observes `InBandInterruptRequested` on the registered targets. This
mirrors Renode's existing `II2CPeripheral` idiom (`Write`/`Read`/`FinishTransmission`) and is how every
functional bus device in Renode is modelled.

Modelling the actual SDA/SCL wires at the bit level (open-drain/push-pull arbitration, T-bits, ENTDAA
address assignment on the wire, HDR framing) is possible but is a great deal of code and is not how
Renode peripherals are normally written. The transaction level captures the *behaviour* an integrator
cares about — private transfers, CCCs, dynamic addressing and IBIs — while staying simple to wire and
easy to test. If you specifically need bit-level SDA/SCL, that can be layered on top later.

## What's included

All C# lives in the `Antmicro.Renode.Peripherals.I3C` namespace (except the mock, which follows the
`Antmicro.Renode.Peripherals.Mocks` convention). Files are stored under `renode-overlay/` at exactly the
paths they occupy inside a Renode checkout:

| File | Role |
|------|------|
| `II3CPeripheral.cs` | The target (slave) contract. |
| `SimpleI3CPeripheral.cs` | Agnostic slave base class with `virtual` hooks — **subclass this to wire proprietary logic**. |
| `DummyI3CSlave.cs` | Ready-to-use mock target (buffers + events), the I3C analog of `DummyI2CSlave`. |
| `SimpleI3CController.cs` | Agnostic master; a `SimpleContainer<II3CPeripheral>` that drives transfers, CCCs, ENTDAA and captures IBIs. |
| `tests/peripherals/I3C.repl` | Example platform wiring a controller and two targets. |
| `tests/peripherals/I3C.robot` | Robot test covering private R/W, dynamic addressing, CCCs and IBI. |

## The `II3CPeripheral` contract

```csharp
public interface II3CPeripheral : IPeripheral
{
    ulong ProvisionedId { get; }         // 48-bit PID, arbitration value during ENTDAA
    byte  BusCharacteristics { get; }    // BCR
    byte  DeviceCharacteristics { get; } // DCR
    byte  StaticAddress { get; }         // legacy I2C static address, 0 = none
    byte  DynamicAddress { get; set; }   // assigned by the controller, 0 = unassigned

    void Write(byte[] data);             // SDR private write (controller -> target)
    byte[] Read(int count = 1);          // SDR private read  (target -> controller)
    void FinishTransmission();           // end of transfer (Stop / repeated Start)

    void HandleCommonCommandCode(byte code, byte[] payload); // broadcast or direct CCC

    event Action<II3CPeripheral, byte[]> InBandInterruptRequested; // IBI: payload = MDB + data
}
```

## Quick start

```bash
./setup.sh
```

This clones Renode next to this repo, overlays the I3C files, builds Renode headless and runs the robot
test. Override the checkout location or revision with `RENODE_DIR`, `RENODE_REMOTE`, `RENODE_REV`.

### Manual integration

If you already have a Renode checkout, just copy the overlay in and build:

```bash
cp -r renode-overlay/. /path/to/renode/
cd /path/to/renode
./build.sh --no-gui
./renode-test tests/peripherals/I3C.robot
```

The Infrastructure project globs its sources, so no `.csproj` edits are needed.

## Wiring in a platform (`.repl`)

```repl
i3c: I3C.SimpleI3CController @ sysbus 0x40010000

slave0: I3C.DummyI3CSlave @ i3c 0x08
    provisionedId: 0x1234567890AB
    busCharacteristics: 0x02
    deviceCharacteristics: 0xC5
```

## Driving it from the monitor

```
(machine) i3c AssignDynamicAddresses                 # simplified ENTDAA enumeration
(machine) i3c WritePrivateHex 0x08 "DEADBEEF"        # SDR private write
(machine) i3c.slave0 EnqueueResponseBytesHex "0102A0"
(machine) i3c ReadPrivateHex 0x08 3                  # -> [0x1, 0x2, 0xA0]
(machine) i3c SendBroadcastCommandCode 0x06          # broadcast CCC (e.g. RSTDAA)
(machine) i3c SendDirectCommandCode 0x80 0x08        # direct CCC to one target
(machine) i3c.slave0 RequestInBandInterrupt 0xAB     # target raises an IBI
(machine) i3c LastInBandInterruptAddress             # -> 8
(machine) i3c.IRQ                                     # IBI drives this GPIO line
```

## Wiring a proprietary target

Subclass `SimpleI3CPeripheral` and override the hooks you care about. The base class handles the
buffering, addressing and IBI plumbing.

```csharp
namespace Antmicro.Renode.Peripherals.I3C
{
    public class MyProprietaryTarget : SimpleI3CPeripheral
    {
        public MyProprietaryTarget()
            : base(provisionedId: 0x1122334455, busCharacteristics: 0x02, deviceCharacteristics: 0xC5)
        {
        }

        protected override void OnWritePrivate(byte[] data)
        {
            // interpret a register write, update internal state, ...
        }

        protected override byte[] OnReadPrivate(int count)
        {
            // return register contents, sensor samples, ...
            return new byte[count];
        }

        protected override void OnCommonCommandCode(byte code, byte[] payload)
        {
            // react to CCCs (ENEC/DISEC, SETMWL, custom vendor codes, ...)
        }

        private void SampleReady()
        {
            // notify the controller out-of-band
            RequestInBandInterrupt(mandatoryDataByte: 0x5A);
        }
    }
}
```

You can equally implement `II3CPeripheral` directly if you do not want the base behaviour. Either way,
register the target on a `SimpleI3CController` (in a `.repl` or from code) and it just works.

## Feature coverage and limitations

Covered: dynamic addressing (a simplified ENTDAA that assigns each registered target a dynamic address),
SDR private read/write, broadcast and direct Common Command Codes, and In-Band Interrupts (with an `IRQ`
GPIO line on the controller).

Out of scope (kept simple on purpose): bit-level SDA/SCL signalling and bus arbitration, HDR transfer
modes, hot-join, and the full CCC set. `SimpleContainer` keys targets by a single integer address, so
ENTDAA here assigns the registration address rather than arbitrating on Provisioned IDs.
