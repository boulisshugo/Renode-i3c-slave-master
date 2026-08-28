# Using the SWP models with a proprietary design

How to plug your own SWP (Single Wire Protocol, ETSI TS 102 613) device into the models in this
repository. Every path below is relative to the repository root.

**The models are a transport, not a protocol stack.** They carry opaque bytes between the CLF and the
target, in both directions, and track whether the line is powered. They implement no framing, no CRC,
no ACT activation sequence and no SHDLC — on purpose. If they did, a proprietary SWP stack connected
to them would be talking *to* that stack instead of *through* the wire, which is the one thing a
transport must not do. Your design owns the protocol; Renode carries its bytes.

`tools/swp-reference/` has a standalone, tested implementation of those layers if you want to borrow
or check against one.

**Contents**

1. [What the models do and do not do](#1-what-the-models-do-and-do-not-do)
2. [File map](#2-file-map)
3. [Getting the code into a Renode build](#3-getting-the-code-into-a-renode-build)
4. [Step 1 — write your target](#step-1--write-your-target)
5. [Step 2 — write the platform file](#step-2--write-the-platform-file)
6. [Step 3 — drive it](#step-3--drive-it)
7. [Step 4 — see the raw bytes](#step-4--see-the-raw-bytes)
8. [Step 5 — connect an external program](#step-5--connect-an-external-program)
9. [Firmware-managed target](#firmware-managed-target)
10. [Where your protocol goes](#where-your-protocol-goes)
11. [Testing your model](#testing-your-model)
12. [Gotchas that actually bite](#gotchas-that-actually-bite)
13. [Checklist](#checklist)

---

## 1. What the models do and do not do

| Layer | Clause | In the Renode peripherals? |
|-------|--------|----------------------------|
| SHDLC LLC | 10 | **No** — yours, or `tools/swp-reference/` |
| ACT LLC | 11 | **No** — yours, or `tools/swp-reference/` |
| Data link (SOF/EOF, bit stuffing, CRC) | 8 | **No** — yours, or `tools/swp-reference/` |
| Byte carriage, full duplex, both directions | — | **Yes** |
| Power state of the line (the CLF owns S1) | — | **Yes** |
| S1/S2 modulation and electrical timings | 4–7 | No — abstracted |

Two consequences worth being clear about:

- **Powering the line runs no handshake.** `PowerUp` drives S1 and moves zero bytes. If your stack
  performs an ACT exchange, that is simply the first traffic to cross the wire afterwards.
- **Nothing is framed, so nothing needs escaping.** `7E`, `7F`, runs of `FF` — every byte value
  crosses unchanged. That is asserted by the test suites.

---

## 2. File map

Everything under `renode-overlay/` mirrors the directory layout inside a Renode checkout, so the
overlay drops straight in.

### The models

| Path | What it is |
|------|-----------|
| `renode-overlay/src/Infrastructure/src/Emulator/Main/Peripherals/SWP/ISWPPeripheral.cs` | The target contract: `Powered`, `SetPower`, `Transfer`, `DataAvailable`. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SimpleSWPPeripheral.cs` | **The class you subclass.** Transport endpoint with an `OnTransfer` hook and a raw byte trace. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SimpleSWPController.cs` | The CLF (master). Owns power, carries bytes. Usually used as-is. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/SWPTCPBridge.cs` | Transparent TCP bridge and the `CreateSWPTCPBridge` monitor command. |

### Reference implementations to copy from

| Path | What it shows |
|------|--------------|
| `.claude/skills/wire-swp-slave/templates/ProprietarySWPSlave.cs` | **Copy-paste starting point** for your class. |
| `.claude/skills/wire-swp-slave/templates/platform.repl` | Copy-paste starting point for the `.repl`. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/Mocks/EchoSWPDevice.cs` | The smallest possible target — six lines. |
| `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/Mocks/DummySWPTarget.cs` | A target with introspection and monitor helpers. |
| `tools/swp-reference/` | Framing, CRC, ACT and SHDLC as a standalone library — **not** part of the peripherals. |

### Platform and test files

| Path | What it is |
|------|-----------|
| `renode-overlay/tests/peripherals/SWP.repl` | A CLF with two targets, on SWP lines 0 and 1. |
| `renode-overlay/tests/peripherals/SWP-consistency.repl` | A CLF with an echoing target. |
| `renode-overlay/tests/peripherals/SWP.robot` | Per-feature suite — copy a test case as a template. |
| `renode-overlay/tests/peripherals/SWP-consistency.robot` | Byte-integrity suite. |
| `renode-overlay/tests/peripherals/SWP-helpers.py` | Python helpers the robot suites use. |
| `tools/swp-selftest/run.sh` | Exercises the transport in seconds, without a Renode checkout. |
| `tools/swp-reference/selftest.sh` | Exercises the protocol reference, likewise. |
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

The Infrastructure project globs its sources, so **no `.csproj` edits are needed**.

---

## Step 1 — write your target

Put your class here, so the overlay carries it with the rest:

```
renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/MyUicc.cs
```

Namespace **must** be `Antmicro.Renode.Peripherals.SWP` — the `.repl` type prefix is the namespace
tail, so that namespace gives you `SWP.MyUicc`. (A wrong prefix produces `Error E04: Could not
resolve type`.)

```csharp
namespace Antmicro.Renode.Peripherals.SWP
{
    public class MyUicc : SimpleSWPPeripheral
    {
        // One full-duplex slot. `incoming` is whatever the CLF drove on S1 - raw bytes, exactly as
        // they were sent. Return what this target drives on S2 in the same slot, or null.
        //
        // Your protocol lives here: parse frames out of `incoming`, build frames for the answer.
        protected override byte[] OnTransfer(byte[] incoming)
        {
            stack.Feed(incoming);
            return stack.TakePendingBytes();
        }

        // The CLF powered the line up or drove S1 low. Reset your stack here.
        protected override void OnPowerChanged(bool powered)
        {
            stack.Reset();
        }

        // Drive bytes on S2 without being polled - SWP is full duplex. Raises the CLF's IRQ.
        private void Notify(byte[] bytes) => SendData(bytes);

        private readonly MyProtocolStack stack = new MyProtocolStack();
    }
}
```

### The hooks

| Hook | Fires when | Default |
|------|-----------|---------|
| `byte[] OnTransfer(byte[] incoming)` | every full-duplex slot | next `EnqueueResponse` block, else nothing |
| `void OnPowerChanged(bool powered)` | the CLF powers the line up or down | no-op |
| `void SendData(byte[] data)` | *you call it* to drive S2 unprompted | — |

`SetPower`, `Transfer` and `Reset` are `virtual` too, but override them only if you must — `OnTransfer`
is the intended seam.

**Byte boundaries are not a protocol.** One `Transfer` delivers one block, but SWP is a bit-serial
wire: do not assume your peer's frames align with the blocks you receive. Buffer and re-frame in your
own stack, exactly as you would on real hardware.

---

## Step 2 — write the platform file

```repl
swp:  SWP.SimpleSWPController @ sysbus

uicc: SWP.MyUicc @ swp 0
```

**The controller takes no address, and that is deliberate.** The CLF is a separate chip on the far end
of the SWP line, not a block inside the SoC. It has no register map, so it is neither
`IDoubleWordPeripheral` nor `IKnownSize`, and it registers on the sysbus with no address at all. The
monitor still addresses it as `sysbus.swp`.

SWP is point to point, but a CLF commonly has more than one line (one to the UICC, one to an embedded
SE), so **the registration index is the SWP line number**:

```repl
swp:  SWP.SimpleSWPController @ sysbus

uicc: SWP.MyUicc @ swp 0
ese:  SWP.MyEmbeddedSe @ swp 1
```

Load it with `machine LoadPlatformDescription @path/to/your.repl`.

---

## Step 3 — drive it

```
(machine) swp PowerUp 0                     # drives S1. No handshake, no bytes.
(machine) swp Powered                       # -> True
(machine) swp TransferHex 0 "00A40004"      # one full-duplex slot -> what came back on S2
(machine) swp ReceiveHex 0                  # empty S1 slot, giving the target a chance to talk
(machine) swp PowerDown 0                   # S1 low; the target drops its session state
```

Useful state on the CLF:

```
swp LastReceivedHex        swp LastReceivedLine
swp BytesSent              swp BytesReceived
swp IRQ IsSet              swp AcknowledgeInterrupt
swp IsPowered 0            swp PowerUpAll
```

**Power up first.** `Transfer` on an unpowered line logs *"is not powered"* and carries nothing. That
is the model working, not a bug.

---

## Step 4 — see the raw bytes

Every block crossing the wire is traced on the target, in both directions:

```
(machine) swp.uicc TraceHex
in   00A40004
out  9000
out  6F1A
```

| Property / method | Gives you |
|-------------------|-----------|
| `TraceHex` | the rolling trace, one block per line |
| `LastReceivedHex` / `LastSentHex` | the most recent block each way |
| `TraceDepth` | bounds the ring; `0` disables recording (the `Last*` properties stay live). Default 32 |
| `ClearTrace` | empties it |
| `TransferHex "…"` | push a block straight at the target, bypassing the CLF |

Because the transport is transparent, what you see here is exactly what your peer sent — no framing
has been stripped, so you can decode it against a real capture byte for byte.

---

## Step 5 — connect an external program

`SWPTCPBridge` exposes the link over a raw TCP socket, transparently in both directions:

```
(machine) swp PowerUp 0
(machine) emulation CreateSWPTCPBridge sysbus.swp 0 3456          # synchronous
(machine) emulation CreateSWPTCPBridge sysbus.swp 0 3456 true     # forward-on-unsolicited-data
(machine) start
```

- **Synchronous** (default): whatever the target drives on S2 in the same slot streams back.
- **Forward-on-unsolicited-data** (`true`): for a target whose answer needs CPU time. The client's
  bytes go out and nothing comes back yet; when the target later calls `SendData`, those bytes are
  forwarded.

```python
import socket
s = socket.create_connection(("127.0.0.1", 3456))
s.sendall(bytes.fromhex("00A40004"))
print(s.recv(64).hex())
```

This is the natural place to put a protocol stack written in another language: the client speaks raw
SWP bytes on the socket and does its own framing.

**The emulation must be running** (`start`) and the line powered. Every transfer is marshalled onto
the machine's time domain, so the CLF drives the target on the same simulation clock as the CPU and a
run is reproducible regardless of host timing — but marshalled work only drains while virtual time
advances.

---

## Firmware-managed target

If your target is driven by firmware on a simulated CPU rather than by C#, register it on **both** the
sysbus (memory-mapped registers for the firmware) and the SWP line:

```repl
uicc: SWP.MyFirmwareManagedUicc @ {
        sysbus 0x90000000;
        swp 0
    }
```

Your class then also implements `IDoubleWordPeripheral, IKnownSize`, keeps RX/TX FIFOs, and calls
`SendData(response)` when the firmware writes a commit register. The I3C and SPI counterparts do
exactly this and are worth reading:

- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SPI/InventedSPITarget.cs`
- `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/I3C/InventedI3CTarget.cs`

This is the arrangement in which the protocol most naturally lives in firmware, which is where it
lives on real silicon.

> There is no `InventedSWPTarget` in the repo, and no `firmware-swp/` or `java-swp/` directory — the
> SWP side ships the transport, mocks, bridge and tests, not a firmware-in-the-loop stack. The two
> files above are the pattern to follow if you need one.

---

## Where your protocol goes

Three places, depending on what you are testing:

| Put it in | When |
|-----------|------|
| Your `SimpleSWPPeripheral` subclass | you are modelling the UICC in C# |
| CPU firmware behind a memory-mapped target | you are testing real firmware — closest to silicon |
| An external client on the TCP bridge | your stack already exists in another language |

`tools/swp-reference/` implements the ETSI layers standalone — the clause 8 framing (SOF `7E`, EOF
`7F`, bit stuffing, CRC-16 `X¹⁶+X¹²+X⁵+1` init `FFFF`), the ACT frames, and the SHDLC control fields.
It is plain C# you can copy or port, and `tools/swp-reference/selftest.sh` checks it against golden
vectors. Its numeric ACT opcodes are a profile rather than verified spec values — the constants are
gathered at the top of `SWPProtocol.cs` for exactly that reason.

---

## Testing your model

**Fast loop, no Renode checkout:**

```bash
apt-get install -y mono-mcs mono-runtime     # once
./tools/swp-selftest/run.sh                  # the transport
./tools/swp-reference/selftest.sh            # the protocol reference
```

Both compile the real sources against Renode API stubs and run in seconds; the first also type-checks
the peripherals, so it catches a compile break long before a Renode build finishes. Add a scenario for
your class in `tools/swp-selftest/SWPSelfTest.cs`.

**Full loop, inside Renode.** Copy a test case from `renode-overlay/tests/peripherals/SWP.robot`:

```bash
./renode-test tests/peripherals/SWP.robot tests/peripherals/SWP-consistency.robot
```

> **Status of the suites in this repo:** both self-tests pass. The robot suites are written but have
> not been executed here, because that needs a built Renode — run `./setup.sh` to confirm them in your
> environment before relying on them.

---

## Gotchas that actually bite

**Field initializers, not constructor-body assignment.** The base constructor calls the virtual
`Reset()`. Anything `Reset()` touches must be a field initializer, or you get a
`NullReferenceException` at platform-load time.

**`byte[]` parameters are not monitor-bindable.** Expose a `…Hex(string)` helper for anything you want
to call from the monitor or a robot test; keep the `byte[]` version for C#.

**Quote hex arguments in the monitor**, especially long ones: `TransferHex 0 "DEAD…"`. Unquoted long
tokens fail with *"Parameters did not match the signature"*.

**A negative `int` prints as `0xFFFFFFFF`.** Don't assert `== -1` on `LastReceivedLine`.

**Don't give the controller a sysbus address out of habit.** It has no register map. Only a
firmware-managed target gets an address, through the multi-registration form.

**Don't assume block boundaries are frame boundaries.** Buffer and re-frame in your stack.

**The type prefix in a `.repl` is the namespace tail.** `Antmicro.Renode.Peripherals.SWP.MyUicc` is
`SWP.MyUicc`; a mock in `…Peripherals.Mocks` is `Mocks.DummySWPTarget`.

---

## Checklist

1. Subclass `SimpleSWPPeripheral` in namespace `Antmicro.Renode.Peripherals.SWP`, under
   `renode-overlay/src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/`.
2. Override `OnTransfer` and put your protocol there; field-initialize anything `Reset()` touches.
3. Reset your stack in `OnPowerChanged(false)`.
4. Write the `.repl`: `SWP.SimpleSWPController @ sysbus` (no address) plus `SWP.YourClass @ swp <line>`.
5. `swp PowerUp <line>` **before** `TransferHex`.
6. Debug with `swp.<name> TraceHex`.
7. External client: `CreateSWPTCPBridge`, power the line, then `start`.
8. Test with `./tools/swp-selftest/run.sh`, then `./renode-test tests/peripherals/SWP*.robot`.

---

## Further reading in this repository

- `README.md` — the SWP counterpart section.
- `.claude/skills/wire-swp-slave/SKILL.md` — the same material as a task-oriented skill.
- `tools/swp-reference/README.md` — why the protocol lives outside the peripherals.
