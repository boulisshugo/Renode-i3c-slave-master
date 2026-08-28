# Agnostic I3C, SPI & SWP master/slave for Renode

Generic, reusable **controller (master)** and **target (slave)** models for
[Renode](https://github.com/renode/renode), for **I3C**, **SPI** and **SWP** (Single Wire Protocol,
ETSI TS 102 613). They are deliberately *agnostic*: they do not model any specific SoC's register map.
Instead they provide a small, well-defined interface that proprietary target implementations can plug
into, and a controller that can drive them from a C# test-bench, the Renode monitor, or robot tests.

Most of this README describes the I3C models; the **SPI** counterpart mirrors them one-to-one — see the
[SPI counterpart](#spi-counterpart) section and the `wire-spi-slave` skill. The **SWP** models are a
third instance of the same idea — see [SWP counterpart](#swp-counterpart) and the `wire-swp-slave`
skill. All three are transports: the protocol running over them belongs to whatever is under test.

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
| `I3CTCPBridge.cs` | Raw TCP bridge to a target: TCP bytes → private write to the slave; the slave's response → TCP. Supports a synchronous write-then-read mode and an asynchronous forward-on-interrupt mode. |
| `EchoI3CDevice.cs` | Mock target that echoes the last write on the next read (for consistency testing). |
| `InventedI3CTarget.cs` | A memory-mapped I3C target driven by CPU firmware (RX/TX FIFOs + IBI-on-commit). |
| `firmware/` | A tiny bare-metal RISC-V "OS" (C) that manages the `InventedI3CTarget` slave. |
| `java/` | A Java I3C bridge client (`sendData`/`isDataAvailable`/`receiveData`) + a reliability harness. |
| `tests/peripherals/I3C*.robot` | Robot suites: per-feature, data consistency, firmware-in-the-loop, and Java-driven. |

## Testing (summary)

Everything below is covered by automated tests (`./setup.sh` builds Renode + firmware + Java and runs them):

- **Per-feature** (`I3C.robot`): each I3C feature one-by-one — identifiers, dynamic addressing, private
  read/write, missing-target warning, broadcast/direct CCC isolation, and IBI raise/carry-data/acknowledge.
- **Consistency** (`I3C-consistency.robot`): large payloads sent at once and many sequential exchanges,
  checked byte-for-byte over both the direct API and the TCP bridge.
- **Firmware-in-the-loop** (`I3C-firmware.robot`): a RISC-V firmware manages the slave; messages are
  driven over TCP through the master to the firmware and back, including a 200-round-trip reliability run.
- **Java-driven** (`I3C-java.robot` + `java/run-integration.sh`): the Java bridge drives the firmware
  slave through the whole chain; measured **100% reliability** over 1000+ round-trips (avg latency ~1.3 ms).

The SPI suites (`SPI*.robot`) mirror these one-to-one. The SWP suites cover the transport: `SWP.robot`
checks power gating, full-duplex byte carriage both ways, line isolation, unsolicited data raising the
IRQ, the raw byte trace, and that bytes a framed link would have to escape cross unchanged;
`SWP-consistency.robot` checks byte-for-byte integrity for large blocks over both the direct API and the
TCP bridge. `tools/swp-selftest/run.sh` drives the transport in seconds without a Renode checkout, and
`tools/swp-reference/selftest.sh` does the same for the standalone protocol reference.

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
i3c: I3C.SimpleI3CController @ sysbus

slave0: I3C.DummyI3CSlave @ i3c 0x08
    provisionedId: 0x1234567890AB
    busCharacteristics: 0x02
    deviceCharacteristics: 0xC5
```

**The controller takes no sysbus address.** A controller here models a bus master, not a block inside
the SoC — it has no register map, so it is neither `IDoubleWordPeripheral` nor `IKnownSize` and claims
no address space. The monitor still reaches it as `sysbus.i3c`. The only models in this repo that do
take an address are `InventedI3CTarget` and `InventedSPITarget`, which really are memory-mapped and
driven by CPU firmware. The same holds for the SPI and SWP controllers.

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

## TCP bridge

`I3CTCPBridge` exposes a target on the controller over a raw TCP socket, so an external program can
drive a proprietary I3C target through Renode. Bytes received from the TCP client are transmitted to
the target as an SDR **private write**, and the target's **read** response is streamed straight back to
the client — a transparent, frameless byte pipe realising the common I3C write-then-read exchange.

Create it from the monitor (it starts listening immediately):

```
(machine) emulation CreateI3CTCPBridge sysbus.i3c 0x08 3456
```

Then, from any TCP client:

```python
import socket
s = socket.create_connection(("127.0.0.1", 3456))
s.sendall(bytes.fromhex("DEADBEEF"))  # transmitted to the target as a private write
print(s.recv(4).hex())                # the bytes the target returned on the read side
```

By default the bridge reads back as many bytes as it just wrote (mirroring). For fixed-length responses
set `ReadLength` on the bridge to a positive value. Because TCP is a byte stream with no message
boundaries, each chunk delivered by the socket becomes one write-then-read exchange; the read buffer is
sized so a small message normally arrives whole.

## Firmware-managed slave + Java bridge (end-to-end)

The full stack below shows an external Java program driving a firmware-managed I3C slave through Renode:

```
Java (I3CBridge: sendData / isDataAvailable / receiveData)
   │  TCP
   ▼
I3CTCPBridge (forward-on-interrupt mode)
   │  private write
   ▼
SimpleI3CController  ──I3C──▶  InventedI3CTarget ──MMIO──▶  RISC-V firmware (echo)
   ▲                                     │ TX commit → In-Band Interrupt (carries the response)
   └───────────────── response ──────────┘
```

- **`InventedI3CTarget`** is registered on both the sysbus (MMIO, for the firmware) and the I3C bus (for
  the master). A private write from the master lands in an RX FIFO; the firmware reads it, pushes a
  response into a TX FIFO, and commits — which raises an In-Band Interrupt carrying the response.
- **Forward-on-interrupt bridge mode** (`emulation CreateI3CTCPBridge sysbus.i3c 0x08 3456 true`) writes
  the TCP bytes to the target and delivers the firmware's response asynchronously, when the IBI fires.
  This matches the Java client's polled API (`isDataAvailable` / `receiveData`).
- The **firmware** (`firmware/`, a tiny bare-metal RISC-V program) polls the target and echoes each
  message. Build it with `firmware/build.sh` (needs a RISC-V bare-metal GCC); a pre-built ELF is committed.
- The **Java bridge** (`java/`) implements exactly `sendData`, `isDataAvailable`, `receiveData`, and
  `Main` runs a reliability/consistency loop. Run the whole chain with `java/run-integration.sh`.

Measured here: **100% reliability** over 1000+ round-trips at 16–250 bytes, average latency ~1.3 ms.

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

## SPI counterpart

The same design is mirrored for **SPI**, built on Renode's existing `ISPIPeripheral`
(`byte Transmit(byte)` + `FinishTransmission()`) rather than a new interface. SPI is full-duplex and
selects a target by **chip-select** (not a bus address); it has no dynamic addressing, CCCs or IBIs —
the slave→master signal is a side-band **data-ready interrupt** (the analog of an I3C IBI).

| I3C | SPI equivalent |
|-----|----------------|
| `SimpleI3CPeripheral` | `SimpleSPIPeripheral` — base with a `byte OnTransfer(byte)` hook + response buffer |
| `DummyI3CSlave` / `EchoI3CDevice` | `Mocks.DummySPITarget` (records MOSI) / `Mocks.EchoSPIDevice` (loopback) |
| `SimpleI3CController` | `SimpleSPIController` — `SimpleContainer<ISPIPeripheral>`, chip-select, `TransferHex` |
| `I3CTCPBridge` | `SPITCPBridge` — full-duplex (N in → N out) + forward-on-interrupt mode |
| `InventedI3CTarget` | `InventedSPITarget` — MMIO + `ISPIPeripheral`, RX/TX FIFOs, interrupt on commit |
| `firmware/` (RISC-V) | `firmware-spi/` (RISC-V, same register map) |
| `java/` | `java-spi/` (`sendData`/`isDataAvailable`/`receiveData` + reliability harness) |
| `tests/peripherals/I3C*.robot` | `tests/peripherals/SPI*.robot` (per-feature, consistency, firmware, java) |

Drive it the same way (chip select instead of address):

```
(machine) spi TransferHex 0 "DEADBEEF"                          # full-duplex exchange -> hex MISO
(machine) emulation CreateSPITCPBridge sysbus.spi 0 3456              # full-duplex bridge
(machine) emulation CreateSPITCPBridge sysbus.spi 0 3456 true         # forward-on-interrupt bridge (firmware/async slave)
(machine) start                                                       # transfers run in the time domain
```

Verified end-to-end, Java → bridge → controller → firmware-managed SPI slave → back: **100% reliability**
over 1000+ round-trips (16–250 bytes). All SPI robot suites pass.

**Raw in, raw out, and deterministic.** The TCP client sends raw bytes and receives raw bytes — the
bridge adds no framing, length bytes, or idle-byte filtering. Crucially, the bridge never drives the
controller or slave from its host socket thread: it marshals every transaction onto the machine's time
domain (`machine.HandleTimeDomainEvent(..., timeDomainInternalEvent: false)`), so the **controller and
slave run on the same simulation clock as the CPU**, never concurrently with it. A run is reproducible
regardless of host timing — which is why the emulation must be running (`start`) for a bridge transfer to
execute.

**How the master gets a firmware slave's response.** SPI is master-clocked, so the slave can never push —
the master only receives bytes by clocking. For a synchronous slave the answer rides the same clocks
(full-duplex, N in → N out). For a firmware-managed slave the answer isn't ready within the command
transfer, so the slave delivers it by asserting its **data-ready interrupt** carrying the response bytes;
the bridge forwards that raw payload to the client (forward-on-interrupt mode). This is the deterministic
SPI analog of an I3C IBI, and it replaces host-thread polling — which could never share the CPU's
simulation time. The `InventedSPITarget` frames the command by chip-select and gates the response behind
a commit (which fires the interrupt), so a half-written response is never shifted out.

## SWP counterpart

**SWP** (Single Wire Protocol, [ETSI TS 102 613](https://www.etsi.org/deliver/etsi_ts/102600_102699/102613/))
is the one-wire link between a **CLF** (Contactless Front-end — the master) and a **UICC** (the slave)
in an NFC-enabled handset. Like the I3C and SPI models, the SWP models are a **transport**: they carry
opaque bytes in both directions and track whether the line is powered, and that is all.

The protocol layers — the clause 8 framing and CRC, the ACT activation sequence of clause 11, SHDLC of
clause 10 — are deliberately **not** in the peripherals. A transport that ran its own stack would be
talking *to* a connected proprietary implementation instead of *through* the wire. Those layers belong
to whatever is under test; `tools/swp-reference/` has a standalone, tested implementation to copy or
check against.

| File | Role |
|------|------|
| `ISWPPeripheral.cs` | The target contract: `Powered`, `SetPower`, `Transfer`, `DataAvailable`. |
| `SimpleSWPPeripheral.cs` | Agnostic target — transport endpoint with an `OnTransfer` hook and a raw byte trace. **Subclass this.** |
| `SimpleSWPController.cs` | Agnostic CLF; holds exactly one target (SWP is point to point), owns power, carries bytes. |
| `SWPTCPBridge.cs` | Transparent TCP bridge: raw bytes in, raw bytes out. |
| `Mocks/DummySWPTarget.cs` | Ready-to-use mock target (records bytes, drives S2 unprompted). |
| `Mocks/EchoSWPDevice.cs` | Mock target that echoes each block (for integrity testing). |
| `tools/swp-reference/` | The ETSI framing, ACT and SHDLC as a standalone library — *not* a peripheral. |
| `tests/peripherals/SWP*.robot` | Robot suites: per-feature and byte-integrity. |

### What the wire does, and does not do

```
    ┌──────────────┐                                      ┌──────────────┐
    │     CLF      │ ──── S1, voltage domain ───────────▶ │     UICC     │
    │   (master)   │ ◀─── S2, current domain ──────────── │   (slave)    │
    │ owns power   │      one wire, full duplex           │              │
    └──────────────┘                                      └──────────────┘
      opaque bytes in both directions — nothing added, nothing removed
```

Two consequences worth knowing:

- **Powering the line runs no handshake.** `PowerUp` drives S1 and moves zero bytes. If the stack under
  test performs an ACT exchange, that is just the first traffic to cross the wire afterwards.
- **Nothing is framed, so nothing needs escaping.** `7E`, `7F`, runs of `FF` — every byte value crosses
  unchanged, which the test suites assert.

### Wiring in a platform (`.repl`)

SWP is point to point: one CLF, one wire, one target. There is no addressing on the wire and nothing
to select, so the controller holds exactly one target and its API takes no line argument. A CLF with
two SWP interfaces is **two controllers**, which is what the hardware is:

```repl
swp: SWP.SimpleSWPController @ sysbus
uicc: Mocks.DummySWPTarget @ swp

// a second, independent interface - e.g. the link to an embedded SE
swp2: SWP.SimpleSWPController @ sysbus
ese: Mocks.DummySWPTarget @ swp2
```

Note the target registers with `@ swp` and no index — there is no line to number.

As with the I3C and SPI controllers, the CLF takes **no sysbus address** — it is a separate chip with no
register map, so claiming address space would misrepresent what is memory-mapped.

### Driving it from the monitor

```
(machine) swp PowerUp                        # drives S1. No handshake, no bytes.
(machine) swp Powered                          # -> True
(machine) swp.uicc EnqueueResponseHex "0102A0"
(machine) swp TransferHex "DEADBEEF"         # one full-duplex slot -> [0x1, 0x2, 0xA0]
(machine) swp ReceiveHex                     # empty S1 slot, letting the target talk
(machine) swp.uicc SendDataHex "AB"            # the target drives S2 unprompted
(machine) swp IRQ IsSet                        # -> True
(machine) swp LastReceivedHex                  # -> [0xAB]
(machine) swp.uicc TraceHex                    # raw bytes both ways
(machine) swp PowerDown
```

### TCP bridge

```
(machine) swp PowerUp
(machine) emulation CreateSWPTCPBridge sysbus.swp 3456        # synchronous
(machine) emulation CreateSWPTCPBridge sysbus.swp 3456 true   # forward-on-unsolicited-data
(machine) start
```

Transparent in both directions — the client sends raw bytes and receives raw bytes, so it is a natural
home for a protocol stack written in another language. As with the I3C and SPI bridges, every transfer
is marshalled onto the machine's time domain, so the CLF drives the target on the same simulation clock
as the CPU and a run is reproducible regardless of host timing.

### Wiring a proprietary target

```csharp
namespace Antmicro.Renode.Peripherals.SWP
{
    public class MyUicc : SimpleSWPPeripheral
    {
        // One full-duplex slot: raw bytes in, raw bytes out. Your protocol lives here.
        protected override byte[] OnTransfer(byte[] incoming)
        {
            stack.Feed(incoming);
            return stack.TakePendingBytes();
        }

        protected override void OnPowerChanged(bool powered) => stack.Reset();

        private void Notify(byte[] bytes) => SendData(bytes);   // drive S2 unprompted

        private readonly MyProtocolStack stack = new MyProtocolStack();
    }
}
```

`SWP-INTEGRATION.md` is the full step-by-step guide, with paths.

### Self-test without a Renode checkout

```bash
apt-get install -y mono-mcs mono-runtime     # once
./tools/swp-selftest/run.sh                  # the transport
./tools/swp-reference/selftest.sh            # the protocol reference
```

Both compile the real sources against Renode API stubs and run in seconds. The first also type-checks
the peripherals, so it catches a compile break early. Neither exercises Renode itself, the `.repl`
loader or the monitor — that is what the robot suites are for.
