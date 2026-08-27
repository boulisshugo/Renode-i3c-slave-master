# Using the SWP models with a proprietary design

How to plug your own SWP (Single Wire Protocol, ETSI TS 102 613) device into the models in this
repository. Every path below is relative to the repository root.

The short version: the models implement the framing, the ACT activation sequence and SHDLC on **both**
sides of the link — but on the target side, **the peripheral does not answer the CLF by itself**. The
SWP contact is a transceiver; ACT and SHDLC are firmware. You choose where that firmware lives:

- **firmware in the loop** — `InventedSWPTarget`, a register window on the sysbus. Received payloads go
  to the emulated CPU, and every `ACT_SYNC`, `ACT_READY`, `UA` and `N(R)` on the wire is one your
  firmware built. This is what real silicon does.
- **host-side stack** — `SoftwareSWPTarget`, which is the same transceiver plus `SWPTargetStack`, a C#
  implementation of ACT and SHDLC. For mocks, benches and consistency suites where no firmware runs.

Both take a five-line `.repl`.

**Contents**

1. [What is already done for you](#1-what-is-already-done-for-you)
2. [File map](#2-file-map)
3. [Getting the code into a Renode build](#3-getting-the-code-into-a-renode-build)
4. [Step 1 — write your UICC](#step-1--write-your-uicc)
5. [Step 2 — write the platform file](#step-2--write-the-platform-file)
6. [Step 3 — drive it](#step-3--drive-it)
7. [Step 4 — see the raw frames](#step-4--see-the-raw-frames)
8. [Step 5 — connect an external program](#step-5--connect-an-external-program)
9. [Firmware-managed UICC](#firmware-managed-uicc)
10. [Matching your silicon](#matching-your-silicon)
11. [Testing your model](#testing-your-model)
12. [Gotchas that actually bite](#gotchas-that-actually-bite)
13. [Checklist](#checklist)

---

## 1. What is already done for you

| Layer | Clause | Where it lives |
|-------|--------|----------------|
| Physical | 4–7 | **Not implemented** — S1/S2 modulation and timings are abstracted; see [Matching your silicon](#matching-your-silicon) |
| Data link | 8 | **`SimpleSWPPeripheral` (hardware).** SOF `7E`, EOF `7F`, MSB-first bit order, bit stuffing (including the end-of-CRC exception), CRC-16 `X¹⁶+X¹²+X⁵+1` init `FFFF` |
| ACT LLC | 11 | **Your firmware**, or `SWPTargetStack` if you have none. `ACT_SYNC` + `ACT_INFORMATION` → `ACT_POWER_MODE` → `ACT_READY`, and FR-bit frame-resend recovery |
| SHDLC LLC | 10 | **Your firmware**, or `SWPTargetStack`. `RSET`/`UA` with window and SREJ negotiation, modulo-8 N(S)/N(R), `RR` acknowledgement, `REJ` with resynchronising retransmission |
| CLF side | 8/10/11 | **`SimpleSWPController`**, all three layers, ready to use |

### Why the target does not answer for you

A `SimpleSWPPeripheral` on its own receives a frame, checks its CRC, hands the payload up — and sends
nothing. That is not an omission; it is the contract. On the chips these models stand in for, the ACT
and SHDLC layers are firmware, and a model that invented an `ACT_READY` would be putting a frame on the
wire that the firmware never sent. Firmware bugs — a missing `ACT_READY`, a stale `N(R)`, a late `UA` —
would be papered over by the model instead of showing up in the simulation, which is the one thing you
bought the simulation for.

So the choice in Step 1 is not a style preference: pick `InventedSWPTarget` when there is firmware to
run, and `SoftwareSWPTarget` when there is not, and the wire stays honest either way.

### What follows from it

**The answer does not ride the frame that asked for it.** SWP is full duplex, and firmware only runs
*after* the receiving slot is over. So:

- `swp Activate 0` returns `False` against a firmware-managed target — S1 is up and the CLF is waiting,
  not failing. Watch `swp IsLinkEstablished 0`, or a UART line from the firmware.
- `swp SendHex 0 "…"` returns `[]`; the answer arrives later, through the controller's
  `PayloadReceived` event, `swp LastReceivedPayloadHex` and the CLF's IRQ line.
- The TCP bridge has a mode for exactly this — see [Step 5](#step-5--connect-an-external-program).

Against a `SoftwareSWPTarget` the whole handshake still completes inside the call, because a host-side
stack can answer inside the slot.

---

## 2. File map

Everything under `renode-overlay/` mirrors the directory layout inside a Renode checkout, so the
overlay drops straight in.

### The models

| Path | What it is |
|------|-----------|
| `renode-overlay/src/Infrastructure/src/Emulator/Main/Peripherals/SWP/ISWPPeripheral.cs` | The UICC contract, plus `SWPInterfaceState` and `SWPPowerMode`. Implement this directly only if you do **not** want the base behaviour. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SimpleSWPPeripheral.cs` | The SWP **hardware**: framing, CRC, S1/S2 slots, frame trace. Answers nothing on its own. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/InventedSWPTarget.cs` | **Subclass this for firmware in the loop.** Memory-mapped register window: RX frames, TX + commit, ACT/DEACT interrupt. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SoftwareSWPTarget.cs` | **Subclass this when there is no firmware.** The transport plus a host-side ACT/SHDLC stack, with `OnInformation`. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPTargetStack.cs` | The UICC-side ACT + SHDLC state machine as a plain class — the host-side stand-in for firmware, and the reference for a C port. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SimpleSWPController.cs` | The CLF (master), event-driven. Usually you use it as-is. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPFrame.cs` | Frame codec: `Encode`, `TryDecode`, `ComputeCrc`, and the `Sof`/`Eof`/CRC constants. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPProtocol.cs` | ACT and SHDLC control-field encodings, frame builders, and `Describe`. **Edit this if your opcodes differ.** |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPFrameRecord.cs` | One traced frame: raw wire image, decoded payload, direction, readable name. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPTCPBridge.cs` | Raw TCP bridge and the `CreateSWPTCPBridge` monitor command. |

### Reference implementations to copy from

| Path | What it shows |
|------|--------------|
| `.claude/skills/wire-swp-slave/templates/ProprietarySWPSlave.cs` | **Copy-paste starting point** for your class. |
| `.claude/skills/wire-swp-slave/templates/platform.repl` | Copy-paste starting point for the `.repl`. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/Mocks/EchoSWPDevice.cs` | The smallest possible UICC — six lines, host-side stack. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/Mocks/DummySWPTarget.cs` | A UICC with introspection and monitor helpers, host-side stack. |
| `firmware-swp/main.c` | **A complete SWP LLC layer in C**: ACT, SHDLC and the register access, ~300 lines. The thing to port from. |

### Platform and test files

| Path | What it is |
|------|-----------|
| `renode-overlay/tests/peripherals/SWP.repl` | A CLF with two UICCs, on SWP lines 0 and 1. |
| `renode-overlay/tests/peripherals/SWP-consistency.repl` | A CLF with an echoing UICC. |
| `renode-overlay/tests/peripherals/SWP-firmware.repl` | A RISC-V CPU, a UART and a firmware-managed UICC on the sysbus and on SWP line 0. |
| `renode-overlay/tests/peripherals/SWP.robot` | Per-feature suite — copy a test case as a template. |
| `renode-overlay/tests/peripherals/SWP-consistency.robot` | Data-integrity suite. |
| `renode-overlay/tests/peripherals/SWP-firmware.robot` | Firmware-in-the-loop suite: activation driven from C, round-trips through the CPU. |
| `renode-overlay/tests/peripherals/SWP-helpers.py` | Python helpers the robot suites use (TCP bridge client, hex utilities). |
| `tools/swp-selftest/run.sh` | Compiles and exercises the models in seconds without a Renode checkout. |
| `setup.sh` | Clones Renode, overlays these files, builds, runs the suites. |

---

## 3. Getting the code into a Renode build

Either let the script do it:

```bash
./setup.sh
```

This clones Renode next to the repo (override with `RENODE_DIR`, `RENODE_REMOTE`, `RENODE_REV`),
overlays the files, builds headless, and runs the test suites.

Or, if you already have a Renode checkout:

```bash
cp -r renode-overlay/. /path/to/renode/
cd /path/to/renode
./build.sh --no-gui
./renode-test tests/peripherals/SWP.robot
```

The Infrastructure project globs its sources, so **no `.csproj` edits are needed** — a new `.cs` file in
the right directory is picked up automatically.

---

## Step 1 — write your UICC

Put your class here, so the overlay carries it with the rest:

```
renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/MyUicc.cs
```

Namespace **must** be `Antmicro.Renode.Peripherals.SWP` — the `.repl` type prefix is the namespace tail,
so that namespace gives you `SWP.MyUicc`. (A wrong prefix produces `Error E04: Could not resolve type`.)

First decide which base class you are on:

| Your UICC's ACT + SHDLC layers run… | Subclass | You supply |
|---|---|---|
| as firmware on a simulated CPU | `InventedSWPTarget` | the register map if the default does not fit, and the firmware |
| in C#, because there is no firmware in the simulation | `SoftwareSWPTarget` | `OnInformation` — the application layer |

### 1a. Firmware in the loop

Often you write **no C# at all**: `InventedSWPTarget` is usable as-is, and the work is the firmware.
Register it on the sysbus and the SWP line (see [Step 2](#step-2--write-the-platform-file)), point your
firmware at the register window, and everything on the wire comes from your code.

The register window (`Size = 0x100`):

| Offset | Name | Access | Meaning |
|--------|------|--------|---------|
| `0x00` | `STATUS` | R | bit0 `ACT_EVT` (latched), bit1 `DEACT_EVT` (latched), bit2 `RX_FRAME`, bit3 `POWERED`; bits[23:8] = bytes left in the current RX frame |
| `0x04` | `STATUS_CLEAR` | W | write 1 to clear `ACT_EVT` / `DEACT_EVT` |
| `0x08` | `IRQ_ENABLE` | RW | which `STATUS` bits assert the IRQ line |
| `0x0C` | `RX_DATA` | R | pop one byte of the current LLC payload, **control field first** |
| `0x10` | `RX_NEXT` | W | discard the rest of the current frame and move to the next |
| `0x14` | `TX_DATA` | W | push one byte of the outgoing LLC payload |
| `0x18` | `TX_COMMIT` | W | frame it, CRC it and drive it onto S2 |
| `0x1C` | `CONTROL` | W | bit0 = flush the RX and TX buffers |
| `0x20` | `LLC_STATE` | RW | the firmware publishes its LLC state (introspection only) |

The flow, and what the hardware does *not* do:

1. **Activation.** The CLF drives S1. `ACT_EVT` latches and the IRQ line goes up. **Nothing is sent.**
   Your interrupt handler opens the LLC and pushes an `ACT_SYNC` payload of your own making — the
   `SyncId`, the bit duration, whatever your profile carries; the hardware neither knows nor cares.
2. **Reception.** A frame arrives, its framing and CRC are checked, its complete LLC payload is queued,
   `RX_FRAME` goes up with the byte count. Drain exactly that many bytes: the count is the frame
   boundary, and it is what keeps two frames from running together.
3. **Transmission.** Write the answer into `TX_DATA`, then `TX_COMMIT`. Only then does a frame exist.
4. **Deactivation.** S1 goes low, buffered frames are dropped, `DEACT_EVT` latches and the IRQ goes up.

`firmware-swp/main.c` is a complete, working implementation of all four in ~300 lines of C: ACT with the
FR-bit repeat, SHDLC with `RSET`/`UA`, modulo-8 sequencing, `RR`, `REJ`, and one placeholder application
function to replace. Start from it.

If you want C# on top of the register window as well — a firmware-managed target with some extra
behaviour — subclass it:

```csharp
public class MyFirmwareUicc : InventedSWPTarget
{
    protected override byte[] OnPayloadReceived(byte[] payload)
    {
        // Called with the complete LLC payload, control field first, before it is queued for the CPU.
        // Return null to stay silent in this slot (which is what the base does, and what you want).
        return base.OnPayloadReceived(payload);
    }
}
```

**A bench that owns the power-up order.** On silicon, S1 rising and the `ACT_EVT` interrupt reaching the
CPU are separated by real time, and a sequencer that models VPS → S1 → event needs to place each edge
itself. Set `AutoActivationEvent` to `false` and raise them separately:

```
(machine) swp.uicc AutoActivationEvent false
(machine) swp.uicc SetS1 true            # the contact is powered; no event yet, no frame yet
   … your delay …
(machine) swp.uicc TriggerActEvent       # now the firmware is interrupted
```

`SetS1 false` / `TriggerDeactEvent` are the mirror image. Left at the default, the events follow the S1
edges immediately.

### 1b. No firmware: the host-side stack

```csharp
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SWP
{
    public class MyUicc : SoftwareSWPTarget
    {
        public MyUicc()
        {
            // Advertised to the CLF in ACT_SYNC. Set it to what your silicon really accepts:
            // the CLF reads it and refuses to send anything larger.
            MaxFramePayloadSize = 254;
            MaxWindowSize = 4;
        }

        // One well-sequenced SHDLC I-frame arrived. `information` is the application bytes only -
        // the control field, CRC and flags have already been taken off.
        //
        // Return a payload to answer with an I-frame (the acknowledgement rides along in its N(R)),
        // or null / empty to answer with a bare RR.
        protected override byte[] OnInformation(byte[] information)
        {
            return HandleApdu(information);
        }

        // Transmit without being polled - SWP is full duplex. Raises the CLF's IRQ line.
        private void SensorReady()
        {
            SendInformation(new byte[] { 0xF0, 0x5A });
        }

        private byte[] HandleApdu(byte[] apdu) => new byte[] { 0x90, 0x00 };
    }
}
```

### The hooks

On `SimpleSWPPeripheral` — the hardware, available on both bases:

| Hook | Fires when | Default |
|------|-----------|---------|
| `byte[] OnPayloadReceived(byte[] payload)` | a well-formed frame arrived; **the complete LLC payload**, control field first | `null` — S2 stays silent |
| `byte[] OnActivated()` | the CLF drove S1 up | `null` — nothing is sent |
| `void OnDeactivated()` | the CLF drove S1 low | no-op |
| `void OnFrameReceived(SWPFrameRecord frame)` | **every** frame in — ACT, SHDLC, malformed | no-op |
| `void OnFrameSent(SWPFrameRecord frame)` | **every** frame out, at every layer | no-op |
| `void TransmitPayload(byte[] payload)` | *you call it* to put one LLC payload on S2 | — |
| `void SetS1(bool)` | *you call it* to drive the contact from a bench | — |

On `SoftwareSWPTarget` — the host-side stack, in addition:

| Hook | Fires when | Default |
|------|-----------|---------|
| `byte[] OnInformation(byte[] information)` | a well-**sequenced** I-frame arrived; application bytes only | next `EnqueueResponsePayload`, else `null` |
| `void OnLinkEstablished()` | the RSET/UA handshake completed | no-op |
| `void SendInformation(byte[] information)` | *you call it* to transmit unprompted | — |

On `InventedSWPTarget` — the firmware-facing side, in addition: `TriggerActEvent()`,
`TriggerDeactEvent()`, `AutoActivationEvent`, and the `LlcState` / `PendingRxFrames` /
`UncommittedTxBytes` properties for the monitor.

`Activate()`, `Deactivate()` and `ExchangeFrame()` are `virtual` as well, but override them only if you
genuinely need to intercept the lifecycle or the raw wire — and always call `base`.

### Capabilities you advertise

On a firmware-managed target these are **in your firmware**, in the `ACT_SYNC` payload it builds and in
the `UA` it answers `RSET` with — there is nothing to configure on the model. On a `SoftwareSWPTarget`
they are plain properties, settable in the constructor **or from the `.repl`**:

| Property | Default | Advertised in |
|----------|---------|---------------|
| `MaxFramePayloadSize` | 4096 | `ACT_INFORMATION` — the CLF refuses to send more |
| `ProtocolVersion` | 1 | `ACT_INFORMATION` |
| `SupportedLlcs` | `Shdlc \| Act` | `ACT_INFORMATION` |
| `SupportedPowerModes` | `0x03` (both) | `ACT_INFORMATION` |
| `MaxWindowSize` | 4 | the `UA` answer to `RSET` |
| `SelectiveRejectSupport` | `false` | the `UA` answer to `RSET` |

---

## Step 2 — write the platform file

A host-side-stack UICC:

```repl
swp:  SWP.SimpleSWPController @ sysbus

uicc: SWP.MyUicc @ swp 0
```

A firmware-managed one is registered **twice** — memory-mapped for the CPU, and on the SWP line for the
CLF:

```repl
uicc: SWP.InventedSWPTarget @ {
        sysbus 0x90000000;
        swp 0
    }

swp:  SWP.SimpleSWPController @ sysbus
```

`renode-overlay/tests/peripherals/SWP-firmware.repl` is that platform complete with a RISC-V CPU and a
UART.

**The controller takes no address, and that is deliberate.** The CLF is a separate chip on the far end
of the SWP line, not a block inside the SoC. It has no register map, so `SimpleSWPController` is neither
`IDoubleWordPeripheral` nor `IKnownSize`, and it registers on the sysbus with no address at all — giving
it one would make the bus lie about what is actually memory-mapped, and would suggest firmware could
reach it through registers, which it cannot. The monitor still addresses it as `sysbus.swp`.

The only thing in an SWP platform that legitimately takes a sysbus address is a **firmware-managed
UICC**, which really is memory-mapped — see [Firmware-managed UICC](#firmware-managed-uicc).

**Where to set the capabilities.** The most reliable place is your class's constructor, as in Step 1 —
it is plain C# and cannot be mis-spelled. Renode's `.repl` can also set public properties directly, but
names there are case-sensitive and must match the C# spelling exactly
(`MaxFramePayloadSize: 254`); every example in this repo binds *constructor parameters* rather than
properties, so if a `.repl` line is rejected with a property error, move the value into the constructor
or set it from the monitor instead:

```
(machine) swp.uicc MaxFramePayloadSize 254
```

That monitor form is what `renode-overlay/tests/peripherals/SWP.robot` uses.

SWP is point to point, but a CLF commonly has more than one line (one to the UICC, one to an embedded
SE), so **the registration index is the SWP line number**:

```repl
swp:  SWP.SimpleSWPController @ sysbus

uicc: SWP.MyUicc @ swp 0
ese:  SWP.MyEmbeddedSe @ swp 1
```

Load it with `machine LoadPlatformDescription @path/to/your.repl`. Working examples live in
`renode-overlay/tests/peripherals/SWP.repl` and
`renode-overlay/tests/peripherals/SWP-consistency.repl`.

---

## Step 3 — drive it

```
(machine) swp Activate 0                    # S1 up, then the ACT sequence as the target answers
(machine) swp InterfaceState                # Deactivated / ActSync / ActPowerMode / ActReady / Activated
(machine) swp LinkEstablished               # -> True once RSET/UA has completed
(machine) swp SendHex 0 "00A40004"          # one I-frame -> the answer's payload, hex
(machine) swp PollHex 0                     # bare RR, giving the UICC a slot to answer
(machine) swp Deactivate 0                  # S1 low, all state dropped
```

Useful state on the CLF:

```
swp GetWindowSize 0                  swp GetTargetMaxFramePayloadSize 0
swp LastReceivedPayloadHex           swp LastReceivedLine
swp FramesSent / FramesReceived      swp CrcErrors / RejectsReceived / Retransmissions
swp IRQ IsSet                        swp AcknowledgeInterrupt
```

Configurable on the CLF before activating: `PowerMode` (`FullPower` / `LowPower`), `WindowSize`,
`SelectiveRejectSupport`, `ActivationRetries`.

**`Activate` first.** `Send` on a link that was never activated logs *"the SHDLC link is not
established"* and returns nothing. That is the model working, not a bug.

**`Activate` returning `False` is not a failure against a firmware-managed target.** It means S1 is up
and the CLF is waiting for the firmware's `ACT_SYNC`, which cannot exist until the CPU has run:

```
(machine) swp Activate 0                    # -> False
(machine) swp IsActivationPending 0          # -> True   (waiting, not failed)
(machine) start
(machine) swp IsLinkEstablished 0            # -> True once the firmware has answered
(machine) swp.uicc LlcState                  # what the firmware says it is doing
```

`swp RetryActivation 0` re-sends `ACT_POWER_MODE` with the FR bit set — the specification's recovery
for an ACT frame the CLF did not get intact. Against a target that answers in-slot, `Activate` already
does that itself, up to `ActivationRetries` times.

Likewise `SendHex` returns `[]` against a firmware-managed target; the answer lands in
`swp LastReceivedPayloadHex` and raises `swp IRQ` once the CPU has produced it.

---

## Step 4 — see the raw frames

Every frame crossing the wire is traced on the target, whichever layer it belongs to, so ACT and SHDLC
land in one log:

```
(machine) swp Activate 0
(machine) swp.uicc FrameTraceHex
out  7E0101051000032EA47F      ACT_SYNC +5B
in   7E02016B4C7F              ACT_POWER_MODE full power
out  7E03D1937F                ACT_READY
in   7EF882003E66DFC0          Reset +2B
out  7EE6040012C97F            UnnumberedAcknowledgement +2B
```

| Property / method | Gives you |
|-------------------|-----------|
| `FrameTraceHex` | the rolling trace, one frame per line |
| `LastFrameInHex` / `LastFrameOutHex` | the raw on-wire image |
| `LastPayloadInHex` / `LastPayloadOutHex` | the decoded LLC payload, control field included |
| `LastFrameIn` / `LastFrameOut` | its readable name |
| `FrameTraceDepth` | bounds the ring; `0` disables recording (the `Last*` properties stay live). Default 32 |
| `ClearFrameTrace` | empties it |
| `ExchangeFrameHex "7E…"` | inject a raw frame, e.g. replaying a capture or a deliberately corrupt one |

Malformed frames are traced too, flagged rather than decoded — a trace that hides bad frames is no use
for the job you opened it for.

From code, override `OnFrameReceived` / `OnFrameSent`, or subscribe to the `FrameTraced` event if you
would rather not subclass. Recording happens at the two choke points every frame must pass, which
matters: **the opening `ACT_SYNC` and unsolicited I-frames never pass through `ExchangeFrame`**, so a
model that hooks only that method loses them silently.

The framing is also inspectable on its own, which is the quickest way to check a capture:

```
(machine) swp EncodeFrameHex "C001"                  # -> [0x7E, 0xC0, 0x1, 0x1B, 0x7A, 0x7F]
(machine) swp DecodeFrameHex "7EC0011B7A7F"          # -> [0xC0, 0x1]   (or "invalid frame: …")
(machine) swp ComputeFrameCrc "313233343536373839"   # -> 0x29B1, the CRC check value for "123456789"
```

---

## Step 5 — connect an external program

`SWPTCPBridge` exposes the link over a raw TCP socket. The client speaks **application payloads only** —
the framing, CRC and SHDLC control byte are added and stripped inside the emulation.

```
(machine) swp Activate 0
(machine) emulation CreateSWPTCPBridge sysbus.swp 0 3456          # synchronous
(machine) emulation CreateSWPTCPBridge sysbus.swp 0 3456 true     # forward-on-unsolicited-frame
(machine) start
```

- **Synchronous** (default): the client's bytes go out as one I-frame and whatever the UICC piggybacks
  on its acknowledgement streams straight back. Right for a `SoftwareSWPTarget`, which can answer
  within the same slot.
- **Forward-on-unsolicited-frame** (`true`): **the mode for a firmware-managed target.** The client's
  bytes go out and nothing comes back yet; when the firmware later commits its answer, the controller
  decodes and sequence-checks that frame and the payload is forwarded. The bridge subscribes to the
  controller's `PayloadReceived`, so a corrupt or out-of-sequence frame never reaches the client.

```python
import socket
s = socket.create_connection(("127.0.0.1", 3456))
s.sendall(bytes.fromhex("00A40004"))
print(s.recv(64).hex())
```

**The emulation must be running** (`start`) and the line must be activated first. Every exchange is
marshalled onto the machine's time domain, so the CLF drives the UICC on the same simulation clock as
the CPU and a run is reproducible regardless of host timing — but marshalled work only drains while
virtual time advances.

---

## Firmware-managed UICC

This is the case the models are shaped around, and it now ships end to end:

| Piece | Path |
|-------|------|
| The peripheral | `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/InventedSWPTarget.cs` |
| The firmware | `firmware-swp/main.c`, `firmware-swp/build.sh` (RISC-V bare metal, ~300 lines of C) |
| The platform | `renode-overlay/tests/peripherals/SWP-firmware.repl` |
| The suite | `renode-overlay/tests/peripherals/SWP-firmware.robot` |

Run it:

```bash
./firmware-swp/build.sh          # needs riscv64-unknown-elf-gcc; the built ELF is committed too
./renode-test tests/peripherals/SWP-firmware.robot
```

or by hand:

```
(monitor) mach create
(machine) machine LoadPlatformDescription @tests/peripherals/SWP-firmware.repl
(machine) sysbus LoadELF @tests/peripherals/swp-firmware.elf
(machine) swp Activate 0                                    # latches ACT_EVT; returns False
(machine) emulation CreateSWPTCPBridge sysbus.swp 0 3456 true
(machine) start
(machine) swp IsLinkEstablished 0                           # -> True, built by the firmware
```

The UART narrates what the firmware is doing, which is the fastest way to see the split at work:

```
swp-firmware: ready
swp-firmware: ACT_SYNC sent
swp-firmware: ACT_READY sent (full power)
swp-firmware: SHDLC link established
```

Every one of those frames exists because `main.c` built it. Comment out the `ACT_READY` branch and the
CLF simply never activates — which is exactly what would happen on a bench, and is the whole reason the
peripheral does not answer for you.

### Porting your own firmware onto it

`firmware-swp/main.c` is laid out as a real LLC layer, so a port is mostly renaming:

| In `main.c` | The equivalent in a typical SWP LLC | 
|---|---|
| the `SWP_STAT_ACT_EVT` branch in `main()` | the ACT-event branch of your interrupt handler |
| `llc_open()` | `SWP_LLC_Open()` + `SWP_LLC_ACT_Send()` |
| `act_receive()` | `SWP_LLC_ACT_Receive()` — including the FR-bit repeat |
| `llc_close()` | `SWP_LLC_Close()` on the deactivation bit |
| `SWP_LLC_STATE` writes | your `SWP_LLC_CLOSED` / `OPENED` / `ACT_SYNC_SENT` / `ACT_READY_SENT` status |
| `swp_app()` | your application layer above SHDLC |

The demo polls `STATUS` rather than taking the interrupt, so the platform needs no interrupt controller.
The IRQ line carries the same three sources (`ACT_EVT`, `DEACT_EVT`, `RX_FRAME`) and is GPIO 0 on the
peripheral — wire it to one in the `.repl` if your firmware is interrupt-driven:

```repl
uicc: SWP.InventedSWPTarget @ {
        sysbus 0x90000000;
        swp 0
    }
    -> plic@11
```

The I3C and SPI counterparts in this repo follow the same pattern and are worth reading alongside it:

- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SPI/InventedSPITarget.cs`
- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/I3C/InventedI3CTarget.cs`

> There is no `java-swp/` directory — the SWP side has no Java bridge client of its own. The TCP bridge
> is protocol-agnostic raw bytes, so `java/` and `java-spi/` are the pattern if you want one.

---

## Matching your silicon

Three things in these models are deliberate simplifications. Change them here and nothing else needs to
move.

**1. The numeric ACT opcodes and the `ACT_INFORMATION` layout are a profile, not verified spec values.**
The frame set, the fields they carry and the sequencing follow ETSI TS 102 613, but the specific
control-byte values were not confirmed against the specification text. They are gathered at the top of
`renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPProtocol.cs`:

```csharp
public const byte ActSync = 0x01;
public const byte ActPowerMode = 0x02;
public const byte ActReady = 0x03;
public const byte ActPowerModeFullPowerBit = 0x01;
public const byte ActPowerModeFrameResendBit = 0x02;
```

`BuildActSync` just below them defines the `ACT_INFORMATION` field order. Both sides of the link and the
`Describe` helper read these constants, so editing them keeps everything consistent.

The SHDLC encoding, by contrast, **is** the ETSI one as found in shipping stacks (the Linux kernel's
`net/nfc/hci/llc_shdlc.c`), so leave that alone unless you know otherwise.

**2. The physical layer is abstracted.** S1/S2 pulse-width modulation, current-domain signalling and the
electrical activation timings are not simulated. `Activate()`, `Deactivate()` and `ExchangeFrame()`
stand in for them. If you need bit-level S1/S2, it has to be layered underneath — nothing above the wire
would change.

**3. Frames are not segmented.** A payload larger than the UICC's advertised `MaxFramePayloadSize` is
refused with a warning rather than split across frames. If your design chains large payloads, implement
that above `OnInformation` — SHDLC itself does not do reassembly, and neither does this model.

Also absent, kept simple on purpose: the CLT (contactless tunnelling) LLC, SHDLC timers T1/T2/T3, and
pipelining more than one unacknowledged I-frame — the negotiated window is honoured and reported, but
the models exchange one frame at a time.

---

## Testing your model

**Fast loop, no Renode checkout.** `tools/swp-selftest/run.sh` compiles the real SWP sources against
Renode API stubs and drives them through the codec, activation, SHDLC and the error-recovery paths in a
couple of seconds:

```bash
apt-get install -y mono-mcs mono-runtime     # once
./tools/swp-selftest/run.sh
```

Add a scenario for your class in `tools/swp-selftest/SWPSelfTest.cs`. It also type-checks the sources,
so it catches a compile break long before a Renode build finishes. If you change a class the stubs stand
in for, `tools/swp-selftest/RenodeStubs.cs` may need a matching signature — that is its one maintenance
cost.

The self-test covers the layering explicitly — a `Layering` section asserts that a bare
`SimpleSWPPeripheral` answers `ACT_POWER_MODE`, `RSET` and an I-frame with **nothing**, and a
`Firmware` section drives an `InventedSWPTarget` through its registers from a stand-in firmware and
checks that the CLF only gets an activated link once that firmware has run.

**Full loop, inside Renode.** Copy a test case from `renode-overlay/tests/peripherals/SWP.robot`:

```bash
./renode-test tests/peripherals/SWP.robot \
              tests/peripherals/SWP-consistency.robot \
              tests/peripherals/SWP-firmware.robot
```

`setup.sh` runs all three suites (along with the I3C and SPI ones) at the end of a build.

> **Status of the suites in this repo:** the self-test passes (101 checks) and `firmware-swp/` builds
> with `riscv64-unknown-elf-gcc`. The robot suites have been written but not executed here, because
> that needs a built Renode — run `./setup.sh` to confirm them in your environment before relying on
> them.

---

## Gotchas that actually bite

**Field initializers, not constructor-body assignment.** The base constructor calls the virtual
`Reset()`. Anything `Reset()` touches must be a field initializer, or you get a
`NullReferenceException` at platform-load time:

```csharp
private readonly Queue<byte[]> pending = new Queue<byte[]>();   // safe
private readonly object locker = new object();
```

**`byte[]` parameters are not monitor-bindable.** Expose a `…Hex(string)` helper for anything you want
to call from the monitor or a robot test; keep the `byte[]` version for C#. This is why
`ExchangeFrameHex` exists alongside `ExchangeFrame`.

**Quote hex arguments in the monitor**, especially long ones: `SendHex 0 "DEAD…"`. Unquoted long tokens
fail with *"Parameters did not match the signature"*.

**Avoid overloads that differ only by an added `string`** — the monitor binds the longer one with
`null`. Name the variant distinctly.

**A negative `int` prints as `0xFFFFFFFF`.** Don't assert `== -1` on `LastReceivedLine`; assert the
positive case instead.

**The frame hooks run with the peripheral's lock held** (as `OnPayloadReceived` and `OnInformation` do).
A handler must not call back into the peripheral.

**Don't expect an in-slot answer from firmware.** `ExchangeFrame` returning empty is the normal case,
not a dropped frame: the CPU has not run yet. If you are asserting on `SendHex`'s return value against
a firmware-managed target, you are asserting on the wrong thing — watch `LastReceivedPayloadHex`, the
`PayloadReceived` event, or the bridge in forward mode.

**Drain exactly `RX_COUNT` bytes.** The byte count in `STATUS` is the frame boundary. Reading past it
pulls in the next frame's control byte and the SHDLC sequencing falls apart two frames later, a long
way from the actual mistake.

**The type prefix in a `.repl` is the namespace tail.** `Antmicro.Renode.Peripherals.SWP.MyUicc` is
`SWP.MyUicc`; a mock in `…Peripherals.Mocks` is `Mocks.DummySWPTarget`.

**Don't give the controller an address out of habit.** `SWP.SimpleSWPController @ sysbus 0x40012000`
looks natural and is wrong — the controller has no registers. Only a firmware-managed UICC gets an
address, through the multi-registration form.

**`.repl` attribute names are case-sensitive** and must match the C# constructor parameter or property
exactly. Prefer the constructor for anything you always want set — it cannot be mis-spelled, and every
example in this repo does it that way.

---

## Checklist

1. Decide where ACT and SHDLC live: firmware (`InventedSWPTarget`) or the host-side stack
   (`SoftwareSWPTarget`). Never `SimpleSWPPeripheral` on its own unless you are supplying the protocol
   some other way — on its own it answers nothing, by design.
2. **Firmware in the loop:** start from `firmware-swp/main.c`; drain exactly `RX_COUNT` bytes per frame;
   answer with `TX_DATA` + `TX_COMMIT`; open the LLC on `ACT_EVT` and close it on `DEACT_EVT`.
   **Host-side stack:** subclass `SoftwareSWPTarget` in namespace `Antmicro.Renode.Peripherals.SWP`,
   override `OnInformation`, and set `MaxFramePayloadSize` / `MaxWindowSize`.
3. Field-initialize anything `Reset()` touches — the base constructor calls it.
4. Write the `.repl`: `SWP.SimpleSWPController @ sysbus` (no address) plus your target on
   `swp <line>` — and on `sysbus <address>` as well if it is firmware-managed.
5. If the ACT opcodes differ from the profile, edit the constants in `SWPProtocol.cs` — and in your
   firmware, which now owns the payload contents.
6. `swp Activate <line>` **before** `SendHex`; against firmware, wait for `IsLinkEstablished` rather
   than trusting `Activate`'s return value.
7. Debug with `swp.<name> FrameTraceHex` — and `LlcState` for what the firmware thinks it is doing.
8. External client: `CreateSWPTCPBridge` (add `true` for a firmware-managed target), then `start`.
9. Test with `./tools/swp-selftest/run.sh`, then `./renode-test tests/peripherals/SWP*.robot`.

---

## Further reading in this repository

- `README.md` — the SWP counterpart section, with the layer diagram and standards-fidelity notes.
- `.claude/skills/wire-swp-slave/SKILL.md` — the same material as a task-oriented skill.
- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/*.cs` — every file carries
  a header comment explaining the clause it implements and the choices made.
