---
name: wire-i3c-slave
description: Use when wiring a proprietary I3C slave peripheral to this repo's SimpleI3CPeripheral, connecting the SimpleI3CController master to it, writing the .repl platform file, or hooking up the Java TCP client. Covers the subclass hooks (OnWritePrivate/OnReadPrivate/OnCommonCommandCode/RequestInBandInterrupt), memory-mapped + I3C multi-registration for firmware-managed slaves, monitor commands, the two TCP-bridge modes, and the Java client API — plus the Renode/monitor gotchas that bite in practice.
---

# Wiring a proprietary I3C slave in Renode

This repo provides agnostic I3C models (`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/I3C/`):

- `II3CPeripheral` — the slave contract.
- `SimpleI3CPeripheral` — the base you subclass for a proprietary slave.
- `SimpleI3CController` — the master (a `SimpleContainer<II3CPeripheral>`).
- `I3CTCPBridge` — bridges a target to a raw TCP socket (Java/other clients).
- `InventedI3CTarget` — an example memory-mapped, firmware-managed slave.

Wiring a proprietary slave has up to four steps: **(1)** subclass the slave, **(2)** write the `.repl`,
**(3)** drive the master, **(4)** connect a Java client. Do only the steps you need.

---

## Step 1 — Subclass `SimpleI3CPeripheral`

Put the class in namespace `Antmicro.Renode.Peripherals.I3C` (so its `.repl` prefix is `I3C.`), under
`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/I3C/`. The SDK-globbed csproj
picks it up automatically — no project edits.

Override only the hooks you need; the base handles buffering, addressing and the IBI plumbing:

| Hook | When it fires | Default |
|------|---------------|---------|
| `OnWritePrivate(byte[] data)` | master did a private write | no-op |
| `OnReadPrivate(int count)` → `byte[]` | master reads `count` bytes | dequeues `EnqueueResponseBytes`, zero-pads |
| `OnCommonCommandCode(byte code, byte[] payload)` | broadcast or direct CCC | no-op |
| `OnFinishTransmission()` | Stop / repeated Start | no-op |
| `RequestInBandInterrupt(byte mdb, byte[] data = null)` | *you call it* to raise an IBI to the master | — |

A copy-paste starting point is in `templates/ProprietaryI3CSlave.cs`.

**Constructor:** `SimpleI3CPeripheral(ulong provisionedId = 0, byte busCharacteristics = 0, byte
deviceCharacteristics = 0, byte staticAddress = 0)`. Pass these through so they can be set from the `.repl`.

**CRITICAL gotcha — field initializers, not constructor-body assignment.** The base constructor calls
the virtual `Reset()`. If your `Reset()` touches a field, that field must be a **field initializer**
(runs before the base ctor), not assigned in your constructor body (runs after) — otherwise you get a
`NullReferenceException` at platform-load time. Correct:

```csharp
private readonly Queue<byte> rxFifo = new Queue<byte>();   // field initializer - safe in Reset()
private readonly object locker = new object();
```

**Thread-safety (only if a client/bridge writes while a CPU runs):** an external socket thread may call
`OnWritePrivate` while emulated firmware reads your state on the CPU thread. Guard shared state with a
`lock` (see `InventedI3CTarget.cs`). Prefer **polling** firmware over cross-thread CPU IRQs.

### Firmware-managed slave (memory-mapped + I3C at once)

To let CPU firmware manage the slave, also implement `IDoubleWordPeripheral, IKnownSize` and register on
**both** the sysbus (MMIO for the firmware) and the I3C bus (for the master). See `InventedI3CTarget.cs`:
RX FIFO filled by `OnWritePrivate`, drained by firmware via `ReadDoubleWord`; firmware pushes a response
via `WriteDoubleWord` and, on a commit register write, you call `RequestInBandInterrupt(mdb, response)`
so the master (and TCP bridge) get the answer asynchronously.

---

## Step 2 — Build the `.repl` platform file

The `.repl` instantiates peripherals and registers them on buses. Syntax: `name: Type @ bus address`,
with constructor parameters as indented `name: value` lines.

**Type prefix = the tail of the namespace.** A class in `...Peripherals.I3C` is `I3C.ClassName`; one in
`...Peripherals.Mocks` is `Mocks.ClassName`. (This is why `DummyI3CSlave` is `Mocks.DummyI3CSlave`, not
`I3C.DummyI3CSlave` — getting this wrong gives `Error E04: Could not resolve type`.)

Minimal master + proprietary slave:

```repl
i3c: I3C.SimpleI3CController @ sysbus

slave: I3C.MyProprietaryI3CSlave @ i3c 0x08
    provisionedId: 0x1234567890AB
    busCharacteristics: 0x02
    deviceCharacteristics: 0xC5
```

- The controller sits on the sysbus (it is `IKnownSize`); the slave registers on the **controller**
  (`@ i3c 0x08`) at its I3C address.
- Constructor params are matched by name (case-insensitive). `ulong`/`byte` values are plain numbers.

**Multi-registration** (a firmware-managed slave on both the sysbus and the I3C bus) uses the `@ { ... }`
form — see `templates/platform.repl` and `renode-overlay/tests/peripherals/I3C-firmware.repl`:

```repl
target: I3C.InventedI3CTarget @ {
        sysbus 0x90000000;
        i3c 0x08
    }
    provisionedId: 0x1122334455
```

Load it: `machine LoadPlatformDescription @tests/peripherals/<name>.repl` (the `@` path is relative to
the Renode root).

---

## Step 3 — Drive the master (monitor / robot / C#)

`SimpleI3CController` is monitor- and robot-callable:

```
i3c AssignDynamicAddresses                  # simplified ENTDAA (assigns each target its reg address)
i3c WritePrivateHex 0x08 "DEADBEEF"         # SDR private write
i3c ReadPrivateHex 0x08 4                   # SDR private read -> "[0xDE, 0xAD, 0xBE, 0xEF]"
i3c SendBroadcastCommandCode 0x06           # broadcast CCC (all targets)
i3c SendDirectCommandCode 0x80 0x08         # direct CCC (one target)
i3c AcknowledgeInBandInterrupt              # clear the IRQ line after an IBI
i3c IRQ IsSet                               # IBI drives this GPIO
```

**Monitor gotchas (all learned the hard way):**

- **Quote hex/string args**, especially long ones: `WritePrivateHex 0x08 "DEAD..."`. An unquoted long
  token fails with *"Parameters did not match the signature"*.
- **`byte[]` params are not monitor-friendly.** Expose `...Hex(int addr, string hex)` helpers (convert
  with `Misc.HexStringToByteArray`) for monitor/robot use; keep `byte[]` overloads for C# test-benches.
- **Avoid overloads that differ only by an added `string`.** The monitor binds the *longer* overload and
  passes `null` for the missing arg → NRE. Give the hex variant a distinct name (e.g.
  `RequestInBandInterruptWithData`), don't overload `RequestInBandInterrupt(int)`.
- **A negative `int` prints as `0xFFFFFFFF`.** Don't assert `== -1`; assert the positive thing instead
  (e.g. `Should Not Be Equal As Numbers ${x} 0x80`).

---

## Step 4 — Connect a Java (or other) client via the TCP bridge

Create the bridge from the monitor (it starts listening immediately):

```
emulation CreateI3CTCPBridge sysbus.i3c 0x08 3456          # write-then-read mode (synchronous slave)
emulation CreateI3CTCPBridge sysbus.i3c 0x08 3456 true     # forward-on-interrupt mode (firmware/async slave)
```

**Pick the mode by how your slave answers:**

- **Write-then-read (default):** bytes from TCP → private write; the bridge immediately reads back the
  response and returns it. Good for synchronous slaves (`ReadLength` sets the read size; 0 = mirror the
  write length).
- **Forward-on-interrupt (`true`):** bytes from TCP → private write only; the response is delivered
  later, when the slave raises an IBI (the bridge forwards the IBI payload **after the MDB**). This is
  required for a firmware-managed slave, which can't answer synchronously while the CPU is mid-quantum.
  Match it with a **polling** client.

The Java client (`java/src/i3c/I3CBridge.java`) implements exactly three methods:

```java
I3CBridge bridge = new I3CBridge("127.0.0.1", 3456);
bridge.sendData(payload);                 // -> master private-writes to the slave
while (!bridge.isDataAvailable()) { /* poll */ }
byte[] response = bridge.receiveData();   // <- bytes the slave sent back
```

`java/src/i3c/Main.java` is a reliability/consistency harness (random payloads, byte-for-byte checks,
latency stats). Build with `java/build.sh`; run the whole chain (starts Renode + bridge + firmware) with
`java/run-integration.sh`. To drive it under robot instead, see `tests/peripherals/I3C-java.robot`
(uses the `Process` library with `I3C_JAVA_CP` pointing at `java/out`).

---

## Build & test

```bash
./setup.sh                       # clone Renode, overlay files, build headless, build firmware+java, run all suites
# or, incrementally, inside a Renode checkout with the overlay applied:
./build.sh --no-gui
./renode-test tests/peripherals/I3C.robot            # per-feature
./renode-test tests/peripherals/I3C-consistency.robot
./renode-test tests/peripherals/I3C-firmware.robot   # firmware-managed slave over TCP
```

Compilation of a new slave is verified fastest with:
`dotnet build src/Infrastructure/src/Infrastructure.csproj -c Release -p:GUI_DISABLED=true` (the Renode
build overrides the target framework to `net8.0`; a bare `dotnet build` of the net6.0 csproj fails on
the GStreamer/GirCore packages).

## Checklist for a new proprietary slave

1. Subclass `SimpleI3CPeripheral` in namespace `...Peripherals.I3C`; override the hooks you need;
   field-initialize anything `Reset()` touches.
2. (Firmware-managed) add `IDoubleWordPeripheral, IKnownSize`, a register map, and lock shared FIFOs.
3. `.repl`: `I3C.<YourClass> @ i3c 0xADDR` (or `@ { sysbus 0x..; i3c 0x.. }`), with
   `I3C.SimpleI3CController @ sysbus` (no address - the controller has no register map).
4. Drive from the monitor with `WritePrivateHex`/`ReadPrivateHex`/CCC/IBI helpers (quote args).
5. Bridge: `CreateI3CTCPBridge` — `true` for a firmware/async slave, default for a synchronous one.
6. Connect the Java client (`sendData`/`isDataAvailable`/`receiveData`); add a robot test.
