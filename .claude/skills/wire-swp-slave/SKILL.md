---
name: wire-swp-slave
description: Use when wiring a proprietary SWP (Single Wire Protocol, ETSI TS 102 613) UICC/eSE target to this repo's SimpleSWPPeripheral, connecting the SimpleSWPController CLF master to it, writing the .repl platform file, or hooking up a TCP client. The models are a transparent transport - no framing, CRC, ACT or SHDLC - so this covers the OnTransfer hook, power control, the raw byte trace, monitor commands, the two TCP-bridge modes, where your protocol stack goes, and the Renode/monitor gotchas that bite in practice.
---

# Wiring a proprietary SWP target in Renode

For the same material as a standalone document the user can read outside a Claude session, see
`SWP-INTEGRATION.md` at the repository root.

This repo provides agnostic SWP models
(`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`), built on a new
`ISWPPeripheral` contract (`Powered` / `SetPower` / `Transfer` / `DataAvailable`):

- `SimpleSWPPeripheral` — the base you subclass for a proprietary target.
- `SimpleSWPController` — the CLF (master). Holds exactly one target: SWP is point to point.
- `SWPTCPBridge` — bridges a target to a raw TCP socket.
- Mocks: `DummySWPTarget` (records bytes, drives S2 unprompted) and `EchoSWPDevice` (loopback).

## The one thing to understand first

**These models are a transport, not a protocol stack.** They carry opaque bytes between the CLF and
the target in both directions, and track whether the line is powered. They implement **no** framing,
**no** CRC, **no** ACT activation sequence and **no** SHDLC.

That is deliberate. If the peripheral ran its own stack, a proprietary SWP implementation connected to
it would be talking *to* that stack instead of *through* the wire — the one thing a transport must not
do. The protocol belongs to whatever is under test.

Two consequences that surprise people:

- **`PowerUp` runs no handshake.** It drives S1 and moves zero bytes. Any ACT exchange the stack under
  test performs is simply the first traffic afterwards.
- **Nothing is framed, so nothing needs escaping.** `7E`, `7F`, runs of `FF` all cross unchanged.

`tools/swp-reference/` has the ETSI framing, ACT and SHDLC as a standalone, tested library — copy it,
port it, or check against it. It is not compiled into Renode.

## Key SWP facts (vs I3C and SPI)

- **Point to point, full duplex, one wire.** The CLF drives S1 (voltage domain), the target answers on
  S2 (current domain), both at once. No bus address, no chip select, and **no line index** — one
  controller holds one target, and its API takes no line argument. A CLF with two SWP interfaces is
  two controllers.
- **The CLF owns power.** Only the master can power the interface up or down. Nothing crosses an
  unpowered line.
- **The target can talk unprompted.** `SendData(bytes)` drives S2 without being polled — the SWP
  equivalent of an I3C In-Band Interrupt or an SPI data-ready line. It raises the controller's `IRQ`.

---

## Step 1 — Subclass `SimpleSWPPeripheral`

Put the class in namespace `Antmicro.Renode.Peripherals.SWP` (repl prefix `SWP.`), under
`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`. The SDK-globbed csproj
picks it up automatically. A copy-paste start is in `templates/ProprietarySWPSlave.cs`.

| Hook | When it fires | Default |
|------|---------------|---------|
| `byte[] OnTransfer(byte[] incoming)` | every full-duplex slot | next `EnqueueResponse` block, else nothing |
| `void OnPowerChanged(bool powered)` | the CLF powers the line up or down | no-op |
| `void SendData(byte[] data)` | *you call it* to drive S2 unprompted | — |

`OnTransfer` is the seam: `incoming` is exactly what the peer drove on S1, and the return value is
exactly what this target drives on S2 in the same slot. Put the protocol stack behind it.

`SetPower`, `Transfer` and `Reset` are `virtual` too, but override them only if you must.

**Byte boundaries are not frame boundaries.** One `Transfer` delivers one block, but SWP is a
bit-serial wire — do not assume the peer's frames align with the blocks you receive. Buffer and
re-frame in your own stack, as you would on real hardware.

**CRITICAL gotcha — field initializers, not constructor-body assignment.** The base constructor calls
the virtual `Reset()`. Any field `Reset()` touches must be a **field initializer**, or you get a
`NullReferenceException` at platform-load time:

```csharp
private readonly MyProtocolStack stack = new MyProtocolStack();   // field initializer - safe
private readonly object locker = new object();
```

---

## Step 2 — Build the `.repl` platform file

**Type prefix = namespace tail.** A class in `...Peripherals.SWP` is `SWP.ClassName`; a mock in
`...Peripherals.Mocks` is `Mocks.ClassName`. (Wrong prefix → `Error E04: Could not resolve type`.)

```repl
swp:  SWP.SimpleSWPController @ sysbus

uicc: SWP.MyProprietarySWPSlave @ swp
```

Note `@ swp` with no index — there is no line to number, and registering a second target on the same
controller is refused. A second SWP interface is a second controller with its own target.

**The controller takes no address.** The CLF is a separate chip on the far end of the SWP line, not a
block inside the SoC: it has no register map, so it is not `IDoubleWordPeripheral` and not
`IKnownSize`, and it registers on the sysbus with **no address at all**. Giving it one would make the
bus lie about what is actually memory-mapped. The monitor still reaches it as `sysbus.swp`. Only a
genuinely memory-mapped, firmware-driven target gets an address, via the multi-registration form below.

Multi-registration (a firmware-managed target on both the sysbus and the SWP line) uses `@ { ... }`,
the same as the I3C and SPI models:

```repl
uicc: SWP.MyFirmwareManagedUicc @ {
        sysbus 0x90000000;
        swp
    }
```

Load it: `machine LoadPlatformDescription @tests/peripherals/<name>.repl`.

---

## Step 3 — Drive the master (monitor / robot / C#)

```
swp PowerUp                          # drives S1. No handshake, no bytes.
swp Powered                            # -> True
swp IsPowered                        # per-line
swp TransferHex "00A40004"           # one full-duplex slot -> hex of what came back on S2
swp ReceiveHex                       # empty S1 slot, letting the target talk
swp PowerDown                        # S1 low; the target drops its session state
swp IRQ IsSet                          # unsolicited data from a target drives this GPIO
swp AcknowledgeInterrupt
swp LastReceivedHex                    # bytes of the most recent block in
swp BytesSent / swp BytesReceived
swp.uicc EnqueueResponseHex "0102"     # queue what the target drives on S2 next slot
swp.uicc SendDataHex "AABB"            # DummySWPTarget: drive S2 unprompted
swp.uicc TraceHex                      # raw bytes both ways, one block per line
swp.uicc LastReceivedHex / LastSentHex
swp.uicc TraceDepth 0                  # 0 disables recording; Last* stay live. Default 32
swp.uicc ClearTrace
swp.uicc TransferHex "7E01"            # push a block straight at the target, bypassing the CLF
```

A trace looks like this — raw bytes, nothing decoded, because the transport does not know the protocol:

```
in   00A40004
out  9000
out  6F1A
```

**Monitor gotchas (same as I3C/SPI):**

- **Power up first.** `Transfer` on an unpowered line logs *"is not powered"* and carries nothing.
  That is the model working, not a bug.
- **Don't give the controller a sysbus address out of habit.** `SWP.SimpleSWPController @ sysbus 0x…`
  looks natural and is wrong: the controller has no register map.
- **Quote hex/string args**, especially long ones: `TransferHex 0 "DEAD…"`. Unquoted long tokens fail
  with *"Parameters did not match the signature"*.
- **`byte[]` params are not monitor-friendly.** Expose `…Hex(...)` helpers; keep `byte[]` for C#.
- **Avoid overloads differing only by an added `string`** (the monitor binds the longer one with `null`).

---

## Step 4 — Connect an external client via the TCP bridge

```
emulation CreateSWPTCPBridge sysbus.swp 3456          # synchronous
emulation CreateSWPTCPBridge sysbus.swp 3456 true     # forward-on-unsolicited-data
```

**Transparent in both directions.** The client sends raw bytes and receives raw bytes; nothing is added
or removed anywhere in the path. That makes it the natural home for a protocol stack written in another
language.

**Determinism.** The bridge never touches the controller or the target from the host socket thread. It
marshals every transfer onto the machine's time domain via
`machine.HandleTimeDomainEvent(..., timeDomainInternalEvent: false)`, so the CLF drives the target on
the **same simulation clock as the CPU**. Consequence: **the emulation must be running** (`start`) and
**the line must be powered** for a bridge transfer to execute.

- **Synchronous (default):** whatever the target drives on S2 in the same slot streams back.
- **Forward-on-unsolicited-data (`true`):** for a target whose answer needs CPU time. The client's
  bytes go out and nothing comes back yet; when the target later calls `SendData`, those bytes are
  forwarded.

## Where the protocol goes

| Put it in | When |
|-----------|------|
| Your `SimpleSWPPeripheral` subclass | modelling the target in C# |
| CPU firmware behind a memory-mapped target | testing real firmware — closest to silicon |
| An external client on the TCP bridge | the stack already exists in another language |

## Build & test

```bash
./tools/swp-selftest/run.sh          # the transport - seconds, no Renode checkout, also type-checks
./tools/swp-reference/selftest.sh    # the standalone protocol reference
dotnet build src/Infrastructure/src/Infrastructure.csproj -c Release -p:GUI_DISABLED=true
./renode-test tests/peripherals/SWP.robot tests/peripherals/SWP-consistency.robot
```

(The Renode build overrides the target framework to `net8.0`; a bare `dotnet build` of the net6.0
csproj fails on the GStreamer/GirCore packages.)

Add a scenario to `tools/swp-selftest/SWPSelfTest.cs` when you add a hook. If you change a class the
stubs stand in for, `tools/swp-selftest/RenodeStubs.cs` may need a matching signature — that is its one
maintenance cost.

## Checklist for a new proprietary SWP target

1. Subclass `SimpleSWPPeripheral` in namespace `...Peripherals.SWP`; override `OnTransfer` and put your
   protocol there; field-initialize anything `Reset()` touches.
2. Reset your stack in `OnPowerChanged(false)`.
3. Don't assume block boundaries are frame boundaries — buffer and re-frame.
4. `.repl`: `SWP.<YourClass> @ swp` (or `@ { sysbus 0x..; swp }`), with
   `SWP.SimpleSWPController @ sysbus` (no address — the controller has no register map).
5. `swp PowerUp` **before** `TransferHex`.
6. Debug with `swp.<name> TraceHex`.
7. Bridge: `CreateSWPTCPBridge`, power the line, then `start`.
