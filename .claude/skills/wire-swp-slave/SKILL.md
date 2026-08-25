---
name: wire-swp-slave
description: Use when wiring a proprietary SWP (Single Wire Protocol, ETSI TS 102 613) UICC/eSE slave to this repo's SimpleSWPPeripheral, connecting the SimpleSWPController CLF master to it, writing the .repl platform file, or hooking up a TCP client. Covers the ACT activation sequence, the SHDLC hooks (OnInformation / SendInformation / OnLinkEstablished), the frame codec (SOF/EOF, bit stuffing, CRC-16), monitor commands, the two TCP-bridge modes, and the Renode/monitor gotchas that bite in practice.
---

# Wiring a proprietary SWP slave in Renode

This repo provides agnostic SWP models
(`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`), built on a new
`ISWPPeripheral` contract (`Activate` / `Deactivate` / `ExchangeFrame` / `FrameAvailable`):

- `SWPFrame` — the data link layer codec: SOF `7E`, bit stuffing, CRC-16, EOF `7F`.
- `SWPProtocol` — the ACT and SHDLC control-field encodings and frame builders.
- `SimpleSWPPeripheral` — the base you subclass for a proprietary UICC. ACT and SHDLC are already done.
- `SimpleSWPController` — the CLF (master), a `SimpleContainer<ISWPPeripheral>` keyed by SWP line.
- `SWPTCPBridge` — bridges a UICC to a raw TCP socket.
- Mocks: `DummySWPTarget` (records payloads, transmits unsolicited frames) and `EchoSWPDevice` (loopback).

Wiring has three steps: **(1)** subclass the slave, **(2)** write the `.repl`, **(3)** drive the master.

## Key SWP facts (vs I3C and SPI)

- **Point to point, full duplex, one wire.** The CLF drives S1 in the voltage domain, the UICC answers
  on S2 in the current domain, both at once. There is no bus address and no chip select; the CLF's
  registration index here is its **SWP line number** (a CLF often has one line to the UICC and one to
  an embedded SE).
- **The CLF owns power.** Only the master can activate or deactivate the interface. Nothing the UICC
  does is meaningful until `Activate` has run.
- **The link is framed and sequenced, unlike I3C/SPI.** Every exchange carries a real CRC-16 and real
  modulo-8 N(S)/N(R) sequence numbers. A slave that gets its sequencing wrong is answered with a REJ,
  not silently tolerated — so let `SimpleSWPPeripheral` handle SHDLC and only override `OnInformation`.
- **The slave can talk unprompted.** `SendInformation(payload)` transmits an I-frame on the UICC's own
  initiative — the SWP equivalent of an I3C In-Band Interrupt or an SPI data-ready line. It raises the
  controller's `IRQ` GPIO.

---

## Step 1 — Subclass `SimpleSWPPeripheral`

Put the class in namespace `Antmicro.Renode.Peripherals.SWP` (repl prefix `SWP.`), under
`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`. The SDK-globbed csproj
picks it up automatically. A copy-paste start is in `templates/ProprietarySWPSlave.cs`.

| Hook | When it fires | Default |
|------|---------------|---------|
| `byte[] OnInformation(byte[] payload)` | a well-sequenced SHDLC I-frame arrived | the next `EnqueueResponsePayload` payload, else `null` |
| `OnLinkEstablished()` | the RSET/UA handshake completed | no-op |
| `OnDeactivated()` | the CLF drove S1 low | no-op |
| `SendInformation(byte[] payload)` | *you call it* to transmit unprompted | — |

Returning a payload from `OnInformation` answers with an I-frame that also carries the acknowledgement;
returning `null` answers with a bare RR. Either way the sequencing is handled for you.

Capabilities advertised in ACT_INFORMATION are plain properties, settable from a `.repl`:
`ProtocolVersion`, `SupportedLlcs`, `MaxFramePayloadSize`, `SupportedPowerModes`, `MaxWindowSize`,
`SelectiveRejectSupport`. The CLF reads `MaxFramePayloadSize` out of ACT_SYNC and refuses to send a
larger payload, so set it to whatever your silicon really accepts.

**CRITICAL gotcha — field initializers, not constructor-body assignment.** The base constructor calls
the virtual `Reset()`. Any field `Reset()` touches must be a **field initializer** (it runs before the
base ctor), or you get a `NullReferenceException` at platform-load time:

```csharp
private readonly Queue<byte[]> pending = new Queue<byte[]>();   // field initializer - safe in Reset()
private readonly object locker = new object();
```

---

## Step 2 — Build the `.repl` platform file

**Type prefix = namespace tail.** A class in `...Peripherals.SWP` is `SWP.ClassName`; a mock in
`...Peripherals.Mocks` is `Mocks.ClassName`. (Wrong prefix → `Error E04: Could not resolve type`.)

```repl
swp:  SWP.SimpleSWPController @ sysbus 0x40012000

uicc: SWP.MyProprietarySWPSlave @ swp 0
```

Multi-registration (a firmware-managed UICC on both the sysbus and the SWP line) uses `@ { ... }`, the
same as the I3C and SPI models:

```repl
uicc: SWP.MyFirmwareManagedUicc @ {
        sysbus 0x90000000;
        swp 0
    }
```

Load it: `machine LoadPlatformDescription @tests/peripherals/<name>.repl`.

---

## Step 3 — Drive the master (monitor / robot / C#)

```
swp Activate 0                              # full ACT sequence + SHDLC RSET/UA -> True
swp InterfaceState                          # Deactivated / ActSync / ActPowerMode / ActReady / Activated
swp LinkEstablished                         # SHDLC link up?
swp GetWindowSize 0                         # window agreed in the RSET handshake
swp GetTargetMaxFramePayloadSize 0          # what the UICC advertised in ACT_SYNC
swp SendHex 0 "DEADBEEF"                    # one I-frame -> hex payload of the answer
swp PollHex 0                               # bare RR, giving the UICC a slot to answer
swp Deactivate 0                            # drive S1 low, drop all state
swp IRQ IsSet                               # an unsolicited UICC frame drives this GPIO
swp AcknowledgeInterrupt
swp LastReceivedPayloadHex                  # payload of the most recent frame in
swp.uicc EnqueueResponsePayloadHex "0102"   # queue what the UICC answers with
swp.uicc RequestServiceWithData "AABB"      # DummySWPTarget: transmit unprompted
```

The data link layer is inspectable on its own, which is the quickest way to check a capture against
the model:

```
swp EncodeFrameHex "C001"        # -> [0x7E, 0xC0, 0x1, 0x1B, 0x7A, 0x7F]
swp DecodeFrameHex "7EC0011B7A7F"# -> [0xC0, 0x1]  (or "invalid frame: CRC mismatch: ...")
swp ComputeFrameCrc "313233343536373839"   # -> 0x29B1, the CRC check value for "123456789"
```

**Monitor gotchas (same as I3C/SPI):**

- **Quote hex/string args**, especially long ones: `SendHex 0 "DEAD…"`. Unquoted long tokens fail with
  *"Parameters did not match the signature"*.
- **`byte[]` params are not monitor-friendly.** Expose `…Hex(int line, string hex)` helpers; keep the
  `byte[]` overloads for C# test-benches.
- **Avoid overloads differing only by an added `string`** (the monitor binds the longer one with `null`).
- **A negative `int` prints as `0xFFFFFFFF`** — don't assert `== -1` on `LastReceivedLine`; assert the
  positive case instead.
- **Activate before sending.** `Send` on a link that was never activated logs *"the SHDLC link is not
  established"* and returns nothing — that is the model working, not a bug.

---

## Step 4 — Connect an external client via the TCP bridge

```
emulation CreateSWPTCPBridge sysbus.swp 0 3456          # synchronous mode
emulation CreateSWPTCPBridge sysbus.swp 0 3456 true     # forward-on-unsolicited-frame mode
```

**Raw payloads in, raw payloads out.** The client speaks only application bytes: the SWP framing, the
CRC and the SHDLC control byte are added and stripped inside the emulation, exactly as on a real link.

**Determinism (the whole point).** The bridge never touches the controller or the slave from the host
socket thread. It marshals every exchange onto the machine's time domain via
`machine.HandleTimeDomainEvent(..., timeDomainInternalEvent: false)`, so the CLF drives the UICC on the
**same simulation clock as the CPU**. Consequence: **the emulation must be running** (`start`) for a
bridge exchange to execute, and the line must be activated first.

Pick the mode by how the UICC produces its answer:

- **Synchronous (default):** the payload the UICC piggybacks on its acknowledgement streams straight
  back. Right for `EchoSWPDevice` and register-style slaves.
- **Forward-on-unsolicited-frame (`true`):** for a UICC whose answer needs CPU time (a firmware-managed
  target). The client's bytes go out as an I-frame and nothing comes back yet; when the UICC later calls
  `SendInformation(payload)`, that payload is forwarded to the client.

## Build & test

```bash
dotnet build src/Infrastructure/src/Infrastructure.csproj -c Release -p:GUI_DISABLED=true   # compile-check
./renode-test tests/peripherals/SWP.robot tests/peripherals/SWP-consistency.robot
```

(The Renode build overrides the target framework to `net8.0`; a bare `dotnet build` of the net6.0 csproj
fails on the GStreamer/GirCore packages.)

## Checklist for a new proprietary SWP slave

1. Subclass `SimpleSWPPeripheral` in namespace `...Peripherals.SWP`; override `OnInformation`;
   field-initialize anything `Reset()` touches.
2. Set the ACT_INFORMATION properties (`MaxFramePayloadSize` above all) to what the silicon accepts.
3. (Firmware-managed) add `IDoubleWordPeripheral, IKnownSize`, a register map, lock shared FIFOs, and
   call `SendInformation(response)` on the commit register write.
4. `.repl`: `SWP.<YourClass> @ swp <line>` (or `@ { sysbus 0x..; swp <line> }`), with
   `SWP.SimpleSWPController @ sysbus 0x..`.
5. Drive from the monitor: `swp Activate <line>` first, then `SendHex` (quote args).
6. Bridge: `CreateSWPTCPBridge` — `true` for a firmware/async slave; activate the line, then `start`.
