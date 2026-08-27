---
name: wire-swp-slave
description: Use when wiring a proprietary SWP (Single Wire Protocol, ETSI TS 102 613) UICC/eSE slave to this repo's SWP models, connecting the SimpleSWPController CLF master to it, writing the .repl platform file, or hooking up a TCP or Java client that owns the CLF's ACT/SHDLC layers itself. Covers where the ACT/SHDLC protocol layer belongs (firmware via InventedSWPTarget, or the host-side SWPTargetStack via SoftwareSWPTarget), the register map and ACT_EVT/DEACT_EVT interrupt a firmware-managed target exposes, the transport hooks (OnPayloadReceived / TransmitPayload / SetS1) and the stack hooks (OnInformation / SendInformation / OnLinkEstablished), the frame codec (SOF/EOF, bit stuffing, CRC-16), monitor commands, the three TCP-bridge modes (application, forward-on-unsolicited-frame, and the length-prefixed LPDU bridge whose client owns the CLF protocol), the java-swp client, and the Renode/monitor gotchas that bite in practice.
---

# Wiring a proprietary SWP slave in Renode

For the same material as a standalone document the user can read outside a Claude session, see
`SWP-INTEGRATION.md` at the repository root.

This repo provides agnostic SWP models
(`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`), built on a new
`ISWPPeripheral` contract (`Activate` / `Deactivate` / `ExchangeFrame` / `FrameAvailable`):

- `SWPFrame` — the data link layer codec: SOF `7E`, bit stuffing, CRC-16, EOF `7F`.
- `SWPProtocol` — the ACT and SHDLC control-field encodings and frame builders.
- `SimpleSWPPeripheral` — the SWP **hardware**: framing, CRC, S1/S2 slots, the frame trace. **It answers
  nothing on its own.**
- `InventedSWPTarget` — that transport plus a memory-mapped register window: the firmware-in-the-loop
  UICC, where ACT and SHDLC run on the emulated CPU.
- `SoftwareSWPTarget` — that transport plus `SWPTargetStack`, a host-side C# implementation of ACT and
  SHDLC, for models with no firmware in the simulation.
- `SWPTargetStack` — the UICC-side ACT + SHDLC state machine as a plain class; also the reference for a
  C port.
- `SimpleSWPController` — the CLF (master), event-driven, a `SimpleContainer<ISWPPeripheral>` keyed by
  SWP interface index.
- `SWPTCPBridge` — bridges a UICC to a raw TCP socket, application payloads only.
- `SWPLpduBridge` — bridges whole LPDUs, so the client owns the **CLF's** ACT and SHDLC layers
  (`CreateSWPLpduBridge`, `ProtocolOwner = External`). `java-swp/` is a worked client for it.
- Mocks: `DummySWPTarget` (records payloads, transmits unsolicited frames) and `EchoSWPDevice`
  (loopback), both `SoftwareSWPTarget`s.

**THE RULE THAT DECIDES EVERYTHING ELSE: the slave peripheral does not generate the protocol answers.**
ACT_SYNC, ACT_READY, UA, RR, REJ and every N(R) are the target's *firmware*, not its SWP contact. A
model that invented them would hide exactly the firmware bugs you are simulating to find. So
`SimpleSWPPeripheral` hands the received LLC payload up and stays silent, and you pick who answers:
`InventedSWPTarget` (firmware does) or `SoftwareSWPTarget` (a host-side stack does, explicitly, because
there is no firmware).

The same rule applies at the other end: `SimpleSWPController` runs ACT and SHDLC by default, but on a
real CLF those are host software too, so `ProtocolOwner = External` makes it a transceiver as well and
the LPDU bridge hands the layers to a client. `java-swp/src/swp/ClfStack.java` is that client, and it is
the mirror of `firmware-swp/main.c` at the other end of the wire.

An **LPDU** is the LLC payload of an SWP frame: the control field and what follows it, before the
SOF/stuffing/CRC/EOF go on. It is the unit both bridges' protocol-owning modes speak, because it is
exactly what software owns and hardware never touches.

Wiring has three steps: **(1)** pick a base and subclass it, **(2)** write the `.repl`, **(3)** drive
the master.

## Key SWP facts (vs I3C and SPI)

- **Point to point, full duplex, one wire.** The CLF drives S1 in the voltage domain, the UICC answers
  on S2 in the current domain, both at once. There is no bus address and no chip select — and no
  numbered line either: nothing on an SWP wire is addressed. The registration index in a `.repl` is
  Renode plumbing (a `SimpleContainer` keys children by number), loosely backed by the fact that a CLF
  chip commonly has more than one SWP contact — one to the UICC, one to an embedded SE. It selects
  **which interface of this CLF**, and nothing more.
- **The CLF owns power.** Only the master can activate or deactivate the interface. Nothing the UICC
  does is meaningful until `Activate` has run.
- **The link is framed and sequenced, unlike I3C/SPI.** Every exchange carries a real CRC-16 and real
  modulo-8 N(S)/N(R) sequence numbers. A slave that gets its sequencing wrong is answered with a REJ,
  not silently tolerated.
- **The slave can talk unprompted, and normally does.** `TransmitPayload(payload)` (or
  `SendInformation` on a `SoftwareSWPTarget`) drives S2 on the UICC's own initiative — the SWP
  equivalent of an I3C In-Band Interrupt or an SPI data-ready line. It raises the controller's `IRQ`
  GPIO.
- **The answer does not ride the frame that asked for it.** Firmware only runs *after* the receiving
  slot is over, so `ExchangeFrame` returning empty is normal, `swp Activate 0` returns `False` against
  a firmware-managed target (S1 is up, the CLF is waiting — check `IsActivationPending` /
  `IsLinkEstablished`), and `SendHex` returns `[]` with the answer arriving later through
  `PayloadReceived` / `LastReceivedPayloadHex` / the IRQ.

---

## Step 1 — Pick a base and subclass it

Put the class in namespace `Antmicro.Renode.Peripherals.SWP` (repl prefix `SWP.`), under
`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`. The SDK-globbed csproj
picks it up automatically. A copy-paste start is in `templates/ProprietarySWPSlave.cs`.

| Your UICC's ACT + SHDLC run… | Subclass | You write |
|---|---|---|
| as firmware on a simulated CPU | `InventedSWPTarget` | the firmware — often no C# at all |
| in C#, no firmware in the simulation | `SoftwareSWPTarget` | `OnInformation` |

### Firmware in the loop — `InventedSWPTarget`

Usable as-is. Register it on the sysbus **and** the SWP interface, point the firmware at the register window,
and every frame on the wire comes from the firmware.

| Offset | Name | Access | Meaning |
|--------|------|--------|---------|
| `0x00` | `STATUS` | R | bit0 `ACT_EVT` (latched), bit1 `DEACT_EVT` (latched), bit2 `RX_FRAME`, bit3 `POWERED`; bits[23:8] = bytes left in the current RX frame |
| `0x04` | `STATUS_CLEAR` | W | write 1 to clear `ACT_EVT` / `DEACT_EVT` |
| `0x08` | `IRQ_ENABLE` | RW | which `STATUS` bits assert the IRQ line (GPIO 0) |
| `0x0C` | `RX_DATA` | R | pop one byte of the current LLC payload, **control field first** |
| `0x10` | `RX_NEXT` | W | discard the rest of the current frame, move to the next |
| `0x14` | `TX_DATA` | W | push one byte of the outgoing LLC payload |
| `0x18` | `TX_COMMIT` | W | frame it, CRC it, drive it onto S2 |
| `0x1C` | `CONTROL` | W | bit0 = flush the RX and TX buffers |
| `0x20` | `LLC_STATE` | RW | the firmware publishes its LLC state (introspection only) |

The contract, in four lines: `ACT_EVT` latches on S1 rising and **nothing is sent** — the firmware's
handler opens the LLC and pushes `ACT_SYNC`; a received frame is queued whole and `RX_FRAME` carries its
byte count, which is the frame boundary (**drain exactly that many bytes**); an answer exists only after
`TX_DATA`… `TX_COMMIT`; `DEACT_EVT` latches on S1 falling and every buffered frame is dropped.

`firmware-swp/main.c` is a complete working LLC layer in ~300 lines of C — ACT with the FR-bit repeat,
SHDLC `RSET`/`UA`, modulo-8 sequencing, `RR`, `REJ`, and one placeholder application function. Start
from it, and see `SWP-firmware.repl` / `SWP-firmware.robot` for the platform and the suite.

**A bench that owns the power-up order** (VPS → S1 → the event, with delays between) turns the automatic
coupling off and places each edge itself:

```
swp.uicc AutoActivationEvent false
swp.uicc SetS1 true          # powered; no event, no frame
swp.uicc TriggerActEvent     # now the firmware is interrupted
swp.uicc SetS1 false ; swp.uicc TriggerDeactEvent
```

### No firmware — `SoftwareSWPTarget`

| Hook | When it fires | Default |
|------|---------------|---------|
| `byte[] OnInformation(byte[] information)` | a well-sequenced SHDLC I-frame arrived; application bytes only | the next `EnqueueResponsePayload` payload, else `null` |
| `OnLinkEstablished()` | the RSET/UA handshake completed | no-op |
| `SendInformation(byte[] information)` | *you call it* to transmit unprompted | — |

Returning a payload from `OnInformation` answers with an I-frame that also carries the acknowledgement;
returning `null` answers with a bare RR. Either way the sequencing is handled by `SWPTargetStack`.

Capabilities advertised in ACT_INFORMATION are plain properties here, settable from a `.repl`:
`ProtocolVersion`, `SupportedLlcs`, `MaxFramePayloadSize`, `SupportedPowerModes`, `MaxWindowSize`,
`SelectiveRejectSupport`. The CLF reads `MaxFramePayloadSize` out of ACT_SYNC and refuses to send a
larger payload, so set it to whatever your silicon really accepts. (On a firmware-managed target these
live in the firmware, in the `ACT_SYNC` it builds — there is nothing to configure on the model.)

### The transport hooks — on both bases

| Hook | When it fires | Default |
|------|---------------|---------|
| `byte[] OnPayloadReceived(byte[] payload)` | a well-formed frame arrived; **the complete LLC payload**, control field first | `null` — S2 stays silent |
| `byte[] OnActivated()` | the CLF drove S1 up | `null` — nothing is sent |
| `OnDeactivated()` | the CLF drove S1 low | no-op |
| `OnFrameReceived(SWPFrameRecord)` | every frame in — ACT, SHDLC, and malformed ones | no-op |
| `OnFrameSent(SWPFrameRecord)` | every frame out, at every layer | no-op |
| `TransmitPayload(byte[] payload)` | *you call it* to put one LLC payload on S2 | — |

`Activate()`, `Deactivate()` and `ExchangeFrame()` are `virtual` too, so a model can intercept the
lifecycle or the raw wire frame — but override them only if you really need to, and always call `base`.

**CRITICAL gotcha — field initializers, not constructor-body assignment.** The base constructor calls
the virtual `Reset()`. Any field `Reset()` touches must be a **field initializer** (derived field
initializers run before the base ctor), or you get a `NullReferenceException` at platform-load time:

```csharp
private readonly Queue<byte[]> pending = new Queue<byte[]>();   // field initializer - safe in Reset()
private readonly object locker = new object();
```

### Getting at the raw frames

`OnFrameReceived` / `OnFrameSent` hand you a `SWPFrameRecord` for **every** frame crossing the wire,
whichever layer it belongs to:

| Field | What it holds |
|-------|---------------|
| `WireFrame` | the raw on-wire image — SOF, bit-stuffed body, CRC, EOF, bit-packed |
| `Payload` | the decoded LLC payload, control field first; empty when the frame was malformed |
| `Description` | `"ACT_SYNC"`, `"I   N(S)=0 N(R)=1 +2B"`, `"RR N(R)=2"`, `"malformed: CRC mismatch…"` |
| `Direction`, `IsMalformed`, `WireHex`, `PayloadHex` | convenience |

Recording happens at the two choke points every frame must pass — `Transmit` on the way out, the
decode in `ExchangeFrame` on the way in — so no layer and no code path can slip past. **This matters:**
the opening `ACT_SYNC` and every frame from `TransmitPayload` / `SendInformation` never pass through
`ExchangeFrame`, so a model that hooks only that method silently loses them — and on a firmware-managed
target that is *most* of the traffic.

Prefer `FrameTraced` (an event) over subclassing when a bridge or a test just wants the bytes. Both the
hooks and the event fire with the peripheral's lock held, so a handler must not call back into it.

---

## Step 2 — Build the `.repl` platform file

**Type prefix = namespace tail.** A class in `...Peripherals.SWP` is `SWP.ClassName`; a mock in
`...Peripherals.Mocks` is `Mocks.ClassName`. (Wrong prefix → `Error E04: Could not resolve type`.)

```repl
swp:  SWP.SimpleSWPController @ sysbus

uicc: SWP.MyProprietarySWPSlave @ swp 0
```

A firmware-managed one is registered twice — memory-mapped for the CPU, on the line for the CLF (see
`renode-overlay/tests/peripherals/SWP-firmware.repl` for the whole platform):

```repl
uicc: SWP.InventedSWPTarget @ {
        sysbus 0x90000000;
        swp 0
    }

swp:  SWP.SimpleSWPController @ sysbus
```

**The controller takes no address.** The CLF is a separate chip on the far end of the SWP line, not a
block inside the SoC: it has no register map, so it is not `IDoubleWordPeripheral` and not `IKnownSize`,
and it registers on the sysbus with **no address at all**. Giving it one would make the bus lie about
what is actually memory-mapped. The monitor still reaches it as `sysbus.swp`. Only a genuinely
memory-mapped, firmware-driven UICC gets an address, via the multi-registration form below.

Load it: `machine LoadPlatformDescription @tests/peripherals/<name>.repl`.

---

## Step 3 — Drive the master (monitor / robot / C#)

```
swp Activate 0                              # S1 up, then the ACT sequence as the target answers
swp InterfaceState                          # Deactivated / ActSync / ActPowerMode / ActReady / Activated
swp LinkEstablished                         # SHDLC link up?
swp GetWindowSize 0                         # window agreed in the RSET handshake
swp GetTargetMaxFramePayloadSize 0          # what the UICC advertised in ACT_SYNC
swp SendHex 0 "DEADBEEF"                    # one I-frame -> hex payload of the answer
swp PollHex 0                               # bare RR, giving the UICC a slot to answer
swp Deactivate 0                            # drive S1 low, drop all state
swp IRQ IsSet                               # an unsolicited UICC frame drives this GPIO
swp AcknowledgeInterrupt
swp LastReceivedPayloadHex                  # payload of the most recent I-frame in
swp IsActivationPending 0                   # True = S1 up, waiting on the target's ACT_SYNC
swp IsLinkEstablished 0                     # the one to poll against a firmware-managed target
swp RetryActivation 0                       # re-send ACT_POWER_MODE with FR = 1
swp.uicc EnqueueResponsePayloadHex "0102"   # SoftwareSWPTarget: queue what the UICC answers with
swp.uicc RequestServiceWithData "AABB"      # DummySWPTarget: transmit unprompted
swp.uicc LlcState                           # InventedSWPTarget: what the firmware says it is doing
swp.uicc SetS1 true / TriggerActEvent       # InventedSWPTarget: place the edges by hand
```

**Against a firmware-managed target, `Activate` returns `False` and `SendHex` returns `[]`** — the CPU
has not run yet. That is the model being honest, not a failure: `start` the emulation and poll
`IsLinkEstablished` / `LastReceivedPayloadHex`, or watch the firmware's UART.

Every frame is traced, so the whole conversation is readable from the monitor without writing C#:

```
swp.uicc FrameTraceHex          # the rolling trace: direction, raw wire bytes, decoded name
swp.uicc LastFrameOutHex        # raw on-wire image of the last frame out
swp.uicc LastPayloadInHex       # decoded payload of the last frame in, control field included
swp.uicc LastFrameIn            # its name, e.g. "ACT_POWER_MODE full power"
swp.uicc FrameTraceDepth 0      # 0 disables recording; Last* stay live. Default 32
swp.uicc ClearFrameTrace
swp.uicc ExchangeFrameHex "7EC0011B7A7F"    # inject a raw frame, e.g. replaying a capture
```

A trace of a full activation looks like this — note that ACT and SHDLC frames land in the same log:

```
out  7E0101051000032EA47F      ACT_SYNC +5B
in   7E02016B4C7F              ACT_POWER_MODE full power
out  7E03D1937F                ACT_READY
in   7EF882003E66DFC0          Reset +2B
out  7EE6040012C97F            UnnumberedAcknowledgement +2B
```

The data link layer is inspectable on its own, which is the quickest way to check a capture against
the model:

```
swp EncodeFrameHex "C001"        # -> [0x7E, 0xC0, 0x1, 0x1B, 0x7A, 0x7F]
swp DecodeFrameHex "7EC0011B7A7F"# -> [0xC0, 0x1]  (or "invalid frame: CRC mismatch: ...")
swp ComputeFrameCrc "313233343536373839"   # -> 0x29B1, the CRC check value for "123456789"
```

**Monitor gotchas (same as I3C/SPI):**

- **Don't give the controller a sysbus address out of habit.** `SWP.SimpleSWPController @ sysbus 0x…` looks
natural and is wrong: the controller has no register map. Only a firmware-managed UICC, which really is
memory-mapped, gets an address.

**Quote hex/string args**, especially long ones: `SendHex 0 "DEAD…"`. Unquoted long tokens fail with
  *"Parameters did not match the signature"*.
- **`byte[]` params are not monitor-friendly.** Expose `…Hex(int line, string hex)` helpers; keep the
  `byte[]` overloads for C# test-benches.
- **Avoid overloads differing only by an added `string`** (the monitor binds the longer one with `null`).
- **A negative `int` prints as `0xFFFFFFFF`** — don't assert `== -1` on `LastReceivedLine`; assert the
  positive case instead.
- **Activate before sending.** `Send` on a link that was never activated logs *"the SHDLC link is not
  established"* and returns nothing — that is the model working, not a bug.

---

## Step 4 — Connect an external client via a TCP bridge

Two bridges, cutting the stack at different heights.

```
emulation CreateSWPTCPBridge sysbus.swp 0 3456          # application: synchronous mode
emulation CreateSWPTCPBridge sysbus.swp 0 3456 true     # application: forward-on-unsolicited-frame
emulation CreateSWPLpduBridge sysbus.swp 0 3457         # LPDU: the client owns the CLF protocol
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
  back. Right for a `SoftwareSWPTarget` (`EchoSWPDevice`, `DummySWPTarget`), which can answer in-slot.
- **Forward-on-unsolicited-frame (`true`):** **the mode for a firmware-managed target.** The client's
  bytes go out as an I-frame and nothing comes back yet; when the firmware later commits its answer, the
  controller decodes and sequence-checks that frame and forwards the payload. The bridge subscribes to
  the controller's `PayloadReceived`, so a corrupt or out-of-sequence frame never reaches the client.

### The LPDU bridge — when the CLF's protocol is yours

`CreateSWPLpduBridge` sets `ProtocolOwner` to `External` and the controller stops interpreting anything:
it frames what it is given (`SendLpdu`) and publishes every LPDU it receives (`LpduReceived`), control
field first. Drive S1 with `PowerUp` — `Activate` would run a protocol that is no longer the model's.

```
emulation CreateSWPLpduBridge sysbus.swp 0 3457
swp PowerUp 0
start
```

**The socket is length-prefixed here** (2 bytes big-endian, then the LPDU, both directions), unlike the
raw application bridge. TCP has no record boundaries and an LPDU boundary is load-bearing — the control
field is the first byte of one — and the frame's own SOF/EOF cannot delimit it because they are
bit-stuffed and bit-packed, which would put the data link layer back in the client.

`java-swp/` is a complete client: `ClfStack.activate()` waits for `ACT_SYNC`, sends `ACT_POWER_MODE`,
takes `ACT_READY`, sends `RSET`, takes the `UA`; `ClfStack.send()` owns N(S)/N(R). If the client
connects after Renode powered S1 — the usual race — it recovers the missed `ACT_SYNC` with the
frame-resend bit rather than guessing, which is the specification's own mechanism.

## Build & test

```bash
./tools/swp-selftest/run.sh          # seconds, no Renode checkout - type-check + protocol scenarios
dotnet build src/Infrastructure/src/Infrastructure.csproj -c Release -p:GUI_DISABLED=true   # compile-check
./firmware-swp/build.sh              # needs riscv64-unknown-elf-gcc; the built ELF is committed too
./renode-test tests/peripherals/SWP.robot tests/peripherals/SWP-consistency.robot \
              tests/peripherals/SWP-firmware.robot
```

Run the self-test first: it compiles the real sources against Renode API stubs
(`tools/swp-selftest/`) and drives the CLF and UICC through the codec, activation, SHDLC and the
error-recovery paths, so a protocol regression shows up long before a Renode build finishes. Add a
scenario to `SWPSelfTest.cs` when you add a hook. If you change a class the stubs stand in for, the
stub may need a matching signature — that is the one maintenance cost.

(The Renode build overrides the target framework to `net8.0`; a bare `dotnet build` of the net6.0 csproj
fails on the GStreamer/GirCore packages.)

## Checklist for a new proprietary SWP slave

1. Decide where ACT and SHDLC live. Firmware → `InventedSWPTarget`. No firmware in the simulation →
   `SoftwareSWPTarget`. Never `SimpleSWPPeripheral` on its own: it answers nothing, on purpose.
2. **Firmware:** start from `firmware-swp/main.c`; open the LLC on `ACT_EVT`, drain exactly `RX_COUNT`
   bytes, answer with `TX_DATA` + `TX_COMMIT`, close on `DEACT_EVT`.
   **Host-side stack:** subclass `SoftwareSWPTarget` in namespace `...Peripherals.SWP`, override
   `OnInformation`, and set the ACT_INFORMATION properties (`MaxFramePayloadSize` above all).
3. Field-initialize anything `Reset()` touches.
4. `.repl`: `SWP.<YourClass> @ swp <line>` (or `@ { sysbus 0x..; swp <line> }` when firmware-managed),
   with `SWP.SimpleSWPController @ sysbus` (no address — the controller has no register map).
5. Drive from the monitor: `swp Activate <line>` first, then `SendHex` (quote args). Against firmware,
   `start` and poll `IsLinkEstablished` rather than trusting `Activate`'s return value.
6. Bridge: `CreateSWPTCPBridge` — `true` for a firmware-managed slave; activate the line, then `start`.
7. If the CLF's protocol layer is yours too, `swp ProtocolOwner External` + `CreateSWPLpduBridge` +
   `swp PowerUp <iface>`, and speak LPDUs — copy `java-swp/src/swp/ClfStack.java`.
8. `./tools/swp-selftest/run.sh` and `./java-swp/run-selftest.sh` before anything else — they
   type-check and run the protocol scenarios in seconds.
