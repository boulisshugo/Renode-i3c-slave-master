---
name: wire-spi-slave
description: Use when wiring a proprietary SPI slave peripheral to this repo's SimpleSPIPeripheral, connecting the SimpleSPIController master to it by chip-select, writing the .repl platform file, or hooking up the Java TCP client. Covers the full-duplex OnTransfer hook, the data-ready interrupt (SPI analog of an I3C IBI), memory-mapped + SPI multi-registration for firmware-managed slaves, monitor commands, the two TCP-bridge modes, and the Java client API — plus the Renode/monitor gotchas that bite in practice.
---

# Wiring a proprietary SPI slave in Renode

This repo provides agnostic SPI models (`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SPI/`),
built on Renode's own `ISPIPeripheral` (`byte Transmit(byte)` + `FinishTransmission()`):

- `SimpleSPIPeripheral` — the base you subclass for a proprietary slave.
- `SimpleSPIController` — the master (a `SimpleContainer<ISPIPeripheral>`), chip-select based.
- `SPITCPBridge` — bridges a target to a raw TCP socket (Java/other clients).
- `InventedSPITarget` — an example memory-mapped, firmware-managed slave.
- Mocks: `DummySPITarget` (records received bytes, raises interrupts) and `EchoSPIDevice` (loopback).

Wiring has up to four steps: **(1)** subclass the slave, **(2)** write the `.repl`, **(3)** drive the
master, **(4)** connect a Java client. Do only the steps you need.

## Key SPI facts (vs I3C)

- **Full-duplex, no addressing on the wire.** Each clocked byte exchanges one byte each way; the master
  picks a slave with a per-slave **chip-select**, not a bus address. So there is no dynamic addressing,
  no CCC. A slave signals the master out-of-band via a **data-ready / interrupt GPIO** — modeled here as
  `SimpleSPIPeripheral.InterruptRequested` (the SPI analog of an I3C In-Band Interrupt).
- The interface (`ISPIPeripheral`) already exists in Renode, so you build on it rather than defining one.

---

## Step 1 — Subclass `SimpleSPIPeripheral`

Put the class in namespace `Antmicro.Renode.Peripherals.SPI` (repl prefix `SPI.`), under
`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SPI/`. The SDK-globbed csproj
picks it up automatically. A copy-paste start is in `templates/ProprietarySPISlave.cs`.

| Hook | When it fires | Default |
|------|---------------|---------|
| `byte OnTransfer(byte incoming)` | one clocked byte (MOSI in, MISO out) | shifts out the next `EnqueueResponseBytes` byte, else 0 |
| `OnFinishTransmission()` | chip-select deasserted | no-op |
| `RequestInterrupt(byte[] data = null)` | *you call it* to assert the data-ready line, handing bytes to the master | — |

`OnTransfer` returns the MISO byte for *this* transfer, based on state loaded earlier — SPI is full-duplex,
so record `incoming` and return your prepared byte in the same call.

**CRITICAL gotcha — field initializers, not constructor-body assignment.** The base constructor calls the
virtual `Reset()`. Any field `Reset()` touches must be a **field initializer** (runs before the base
ctor), or you get a `NullReferenceException` at platform-load time:

```csharp
private readonly Queue<byte> rxFifo = new Queue<byte>();   // field initializer - safe in Reset()
private readonly object locker = new object();
```

**Thread-safety (only if a client/bridge transfers while a CPU runs):** guard shared state touched by both
`OnTransfer` (socket thread) and firmware register reads (CPU thread) with a `lock`; prefer **polling**
firmware over cross-thread CPU IRQs. See `InventedSPITarget.cs`.

### Firmware-managed slave (memory-mapped + SPI at once)

Also implement `IDoubleWordPeripheral, IKnownSize` and register on **both** the sysbus (MMIO for firmware)
and the SPI bus. See `InventedSPITarget.cs`: MOSI bytes fill an RX FIFO the firmware drains via
`ReadDoubleWord`; the firmware pushes a response via `WriteDoubleWord` and, on a commit register write,
calls `RequestInterrupt(response)` so the master (and TCP bridge) get the answer asynchronously.

---

## Step 2 — Build the `.repl` platform file

**Type prefix = namespace tail.** A class in `...Peripherals.SPI` is `SPI.ClassName`; a mock in
`...Peripherals.Mocks` is `Mocks.ClassName`. (Wrong prefix → `Error E04: Could not resolve type`.)

Minimal master + proprietary slave (registered by **chip-select index**):

```repl
spi: SPI.SimpleSPIController @ sysbus 0x40011000

slave: SPI.MyProprietarySPISlave @ spi 0
```

**Multi-registration** (a firmware-managed slave on both the sysbus and the SPI bus) uses `@ { ... }` —
see `templates/platform.repl` and `renode-overlay/tests/peripherals/SPI-firmware.repl`:

```repl
target: SPI.InventedSPITarget @ {
        sysbus 0x90000000;
        spi 0
    }
```

Load it: `machine LoadPlatformDescription @tests/peripherals/<name>.repl`.

---

## Step 3 — Drive the master (monitor / robot / C#)

```
spi TransferHex 0 "DEADBEEF"          # full-duplex exchange with chip-select 0 -> hex MISO bytes
spi AcknowledgeInterrupt              # clear the IRQ line after a data-ready interrupt
spi IRQ IsSet                         # the interrupt drives this GPIO
spi.slave0 EnqueueResponseBytesHex "0102"   # queue MISO bytes on the mock/slave
spi.slave0 RequestInterrupt                  # DummySPITarget: assert the data-ready line
spi.slave0 LastReceivedHex                   # DummySPITarget: MOSI bytes of the last transaction
```

**Monitor gotchas (learned the hard way on I3C, they apply here too):**

- **Quote hex/string args**, especially long ones: `TransferHex 0 "DEAD…"`. Unquoted long tokens fail
  with *"Parameters did not match the signature"*.
- **`byte[]` params are not monitor-friendly.** Expose `…Hex(int cs, string hex)` helpers; keep `byte[]`
  overloads for C# test-benches.
- **Avoid overloads differing only by an added `string`** (the monitor binds the longer one with `null`).
  Name a data variant distinctly, e.g. `RequestInterruptWithData(string)` vs `RequestInterrupt()`.
- **A negative `int` prints as `0xFFFFFFFF`** — don't assert `== -1`; assert the positive thing instead.

---

## Step 4 — Connect a Java (or other) client via the TCP bridge

```
emulation CreateSPITCPBridge sysbus.spi 0 3456          # full-duplex mode (synchronous slave)
emulation CreateSPITCPBridge sysbus.spi 0 3456 true     # forward-on-interrupt mode (firmware/async slave)
```

- **Full-duplex (default):** bytes from TCP are clocked to the target and the MISO bytes of the same
  transfer stream straight back — N in, N out. Right for synchronous slaves (`EchoSPIDevice`, register slaves).
- **Forward-on-interrupt (`true`):** bytes from TCP are clocked in, but the response arrives later, when
  the target asserts its interrupt line — required for a firmware-managed slave, which can't answer while
  the CPU is mid-quantum. Match it with a **polling** client.

The Java client (`java-spi/src/spi/SPIBridge.java`) implements exactly `sendData`, `isDataAvailable`,
`receiveData`. `java-spi/src/spi/Main.java` is a reliability harness; `java-spi/run-integration.sh` drives
the whole chain. Under robot, see `tests/peripherals/SPI-java.robot` (Process library + `I3C_SPI_JAVA_CP`).

## Build & test

```bash
dotnet build src/Infrastructure/src/Infrastructure.csproj -c Release -p:GUI_DISABLED=true   # compile-check
./renode-test tests/peripherals/SPI.robot tests/peripherals/SPI-consistency.robot tests/peripherals/SPI-firmware.robot
```

(The Renode build overrides the target framework to `net8.0`; a bare `dotnet build` of the net6.0 csproj
fails on the GStreamer/GirCore packages.)

## Checklist for a new proprietary SPI slave

1. Subclass `SimpleSPIPeripheral` in namespace `...Peripherals.SPI`; override `OnTransfer`; field-initialize
   anything `Reset()` touches.
2. (Firmware-managed) add `IDoubleWordPeripheral, IKnownSize`, a register map, and lock shared FIFOs.
3. `.repl`: `SPI.<YourClass> @ spi <cs>` (or `@ { sysbus 0x..; spi <cs> }`), with `SPI.SimpleSPIController @ sysbus 0x..`.
4. Drive from the monitor with `TransferHex` / interrupt helpers (quote args).
5. Bridge: `CreateSPITCPBridge` — `true` for a firmware/async slave, default for a synchronous one.
6. Connect the Java client (`sendData`/`isDataAvailable`/`receiveData`); add a robot test.
