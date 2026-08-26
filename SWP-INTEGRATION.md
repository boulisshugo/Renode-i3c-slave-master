# Using the SWP models with a proprietary design

How to plug your own SWP (Single Wire Protocol, ETSI TS 102 613) device into the models in this
repository. Every path below is relative to the repository root.

The short version: the models already implement the framing, the ACT activation sequence and SHDLC on
**both** sides of the link. You subclass one class, override one method, and write a five-line `.repl`.
Everything else is inherited and stays correct.

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

| Layer | Clause | Already implemented |
|-------|--------|---------------------|
| Data link | 8 | SOF `7E`, EOF `7F`, MSB-first bit order, bit stuffing (including the end-of-CRC exception), CRC-16 `X¹⁶+X¹²+X⁵+1` init `FFFF` |
| ACT LLC | 11 | `ACT_SYNC` + `ACT_INFORMATION` → `ACT_POWER_MODE` → `ACT_READY`, and FR-bit frame-resend recovery |
| SHDLC LLC | 10 | `RSET`/`UA` with window and SREJ negotiation, modulo-8 N(S)/N(R), `RR` acknowledgement, `REJ` with resynchronising retransmission |
| Physical | 4–7 | **Not implemented** — S1/S2 modulation and timings are abstracted; see [Matching your silicon](#matching-your-silicon) |

You are expected to supply only the **application layer**: what your device does with the bytes inside
an SHDLC I-frame.

---

## 2. File map

Everything under `renode-overlay/` mirrors the directory layout inside a Renode checkout, so the
overlay drops straight in.

### The models

| Path | What it is |
|------|-----------|
| `renode-overlay/src/Infrastructure/src/Emulator/Main/Peripherals/SWP/ISWPPeripheral.cs` | The UICC contract, plus `SWPInterfaceState` and `SWPPowerMode`. Implement this directly only if you do **not** want the base behaviour. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SimpleSWPPeripheral.cs` | **The class you subclass.** ACT + SHDLC + framing, with hooks. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SimpleSWPController.cs` | The CLF (master). Usually you use it as-is. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPFrame.cs` | Frame codec: `Encode`, `TryDecode`, `ComputeCrc`, and the `Sof`/`Eof`/CRC constants. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPProtocol.cs` | ACT and SHDLC control-field encodings, frame builders, and `Describe`. **Edit this if your opcodes differ.** |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPFrameRecord.cs` | One traced frame: raw wire image, decoded payload, direction, readable name. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPTCPBridge.cs` | Raw TCP bridge and the `CreateSWPTCPBridge` monitor command. |

### Reference implementations to copy from

| Path | What it shows |
|------|--------------|
| `.claude/skills/wire-swp-slave/templates/ProprietarySWPSlave.cs` | **Copy-paste starting point** for your class. |
| `.claude/skills/wire-swp-slave/templates/platform.repl` | Copy-paste starting point for the `.repl`. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/Mocks/EchoSWPDevice.cs` | The smallest possible UICC — six lines. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/Mocks/DummySWPTarget.cs` | A UICC with introspection and monitor helpers. |

### Platform and test files

| Path | What it is |
|------|-----------|
| `renode-overlay/tests/peripherals/SWP.repl` | A CLF with two UICCs, on SWP lines 0 and 1. |
| `renode-overlay/tests/peripherals/SWP-consistency.repl` | A CLF with an echoing UICC. |
| `renode-overlay/tests/peripherals/SWP.robot` | Per-feature suite — copy a test case as a template. |
| `renode-overlay/tests/peripherals/SWP-consistency.robot` | Data-integrity suite. |
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

```csharp
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SWP
{
    public class MyUicc : SimpleSWPPeripheral
    {
        public MyUicc()
        {
            // Advertised to the CLF in ACT_SYNC. Set it to what your silicon really accepts:
            // the CLF reads it and refuses to send anything larger.
            MaxFramePayloadSize = 254;
            MaxWindowSize = 4;
        }

        // One well-sequenced SHDLC I-frame arrived. `payload` is the application bytes only -
        // the control field, CRC and flags have already been taken off.
        //
        // Return a payload to answer with an I-frame (the acknowledgement rides along in its N(R)),
        // or null / empty to answer with a bare RR.
        protected override byte[] OnInformation(byte[] payload)
        {
            return HandleApdu(payload);
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

| Hook | Fires when | Default |
|------|-----------|---------|
| `byte[] OnInformation(byte[] payload)` | a well-sequenced I-frame arrived | next `EnqueueResponsePayload`, else `null` |
| `void OnLinkEstablished()` | the RSET/UA handshake completed | no-op |
| `void OnDeactivated()` | the CLF drove S1 low | no-op |
| `void OnFrameReceived(SWPFrameRecord frame)` | **every** frame in — ACT, SHDLC, malformed | no-op |
| `void OnFrameSent(SWPFrameRecord frame)` | **every** frame out, at every layer | no-op |
| `void SendInformation(byte[] payload)` | *you call it* to transmit unprompted | — |

`Activate()`, `Deactivate()` and `ExchangeFrame()` are `virtual` as well, but override them only if you
genuinely need to intercept the lifecycle or the raw wire — overriding `OnInformation` keeps ACT and
SHDLC correct for free, and forgetting to call `base` in the others disables the protocol.

### Capabilities you advertise

These are plain properties, settable in the constructor **or from the `.repl`**:

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

```repl
swp:  SWP.SimpleSWPController @ sysbus

uicc: SWP.MyUicc @ swp 0
```

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
(machine) swp Activate 0                    # ACT sequence + SHDLC RSET/UA. -> True
(machine) swp InterfaceState                # Deactivated / ActSync / ActPowerMode / ActReady / Activated
(machine) swp LinkEstablished               # -> True
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
  on its acknowledgement streams straight back. Right for a UICC that answers within the same slot.
- **Forward-on-unsolicited-frame** (`true`): for a UICC whose answer needs CPU time. The client's bytes
  go out and nothing comes back yet; when the UICC later calls `SendInformation`, that payload is
  forwarded.

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

If your UICC is driven by firmware on a simulated CPU rather than by C#, register it on **both** the
sysbus (memory-mapped registers for the firmware) and the SWP line:

```repl
uicc: SWP.MyFirmwareManagedUicc @ {
        sysbus 0x90000000;
        swp 0
    }
```

Your class then also implements `IDoubleWordPeripheral, IKnownSize`, keeps RX/TX FIFOs, and calls
`SendInformation(response)` when the firmware writes a commit register. The I3C and SPI counterparts in
this repo do exactly this and are worth reading as working examples:

- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SPI/InventedSPITarget.cs`
- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/I3C/InventedI3CTarget.cs`

> There is no `InventedSWPTarget` in the repo yet, and no `firmware-swp/` or `java-swp/` directory — the
> SWP side ships the models, mocks, bridge and tests, but not the firmware-in-the-loop stack the I3C and
> SPI sides have. The two files above are the pattern to follow if you need one.

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

**Full loop, inside Renode.** Copy a test case from `renode-overlay/tests/peripherals/SWP.robot`:

```bash
./renode-test tests/peripherals/SWP.robot tests/peripherals/SWP-consistency.robot
```

`setup.sh` runs both suites (along with the I3C and SPI ones) at the end of a build.

> **Status of the suites in this repo:** the self-test passes (69 checks). The robot suites have been
> written but not executed here, because that needs a built Renode — run `./setup.sh` to confirm them
> in your environment before relying on them.

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

**The frame hooks run with the peripheral's lock held** (as `OnInformation` does). A handler must not
call back into the peripheral.

**The type prefix in a `.repl` is the namespace tail.** `Antmicro.Renode.Peripherals.SWP.MyUicc` is
`SWP.MyUicc`; a mock in `…Peripherals.Mocks` is `Mocks.DummySWPTarget`.

**`.repl` attribute names are case-sensitive** and must match the C# constructor parameter or property
exactly. Prefer the constructor for anything you always want set — it cannot be mis-spelled, and every
example in this repo does it that way.

---

## Checklist

1. Subclass `SimpleSWPPeripheral` in namespace `Antmicro.Renode.Peripherals.SWP`, under
   `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`.
2. Override `OnInformation`; field-initialize anything `Reset()` touches.
3. Set `MaxFramePayloadSize` and `MaxWindowSize` to what your silicon accepts.
4. Write the `.repl`: `SWP.SimpleSWPController @ sysbus` (no address) plus `SWP.YourClass @ swp <line>`.
5. If the ACT opcodes differ from the profile, edit the constants in `SWPProtocol.cs` — nothing else.
6. `swp Activate <line>` **before** `SendHex`.
7. Debug with `swp.<name> FrameTraceHex`.
8. External client: `CreateSWPTCPBridge`, then `start`.
9. Test with `./tools/swp-selftest/run.sh`, then `./renode-test tests/peripherals/SWP*.robot`.

---

## Further reading in this repository

- `README.md` — the SWP counterpart section, with the layer diagram and standards-fidelity notes.
- `.claude/skills/wire-swp-slave/SKILL.md` — the same material as a task-oriented skill.
- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/*.cs` — every file carries
  a header comment explaining the clause it implements and the choices made.
