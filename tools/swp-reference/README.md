# SWP protocol reference

A standalone implementation of the ETSI TS 102 613 layers that sit **above** the wire:

| File | Layer |
|------|-------|
| `SWPFrame.cs` | Data link (clause 8): SOF `7E`, EOF `7F`, MSB-first bit stuffing, CRC-16 `X¹⁶+X¹²+X⁵+1` init `FFFF` |
| `SWPProtocol.cs` | ACT LLC (clause 11) and SHDLC LLC (clause 10): control-field encodings, frame builders, `Describe` |

**This is not part of the Renode peripherals, on purpose.** The SWP models under `renode-overlay/`
are a transparent transport: they carry opaque bytes between the CLF and the target and add nothing.
If they ran their own framing and SHDLC, a proprietary SWP stack connected to them would be talking
*to* that stack rather than *through* the wire, which is the one thing a transport must not do.

So the protocol belongs to whichever side is under test — a proprietary UICC model, CPU firmware, or
an external client on the far end of the TCP bridge. This directory is here so you do not have to
write it from scratch: copy these files into your model, port them to C or Java, or check your own
implementation against them.

Both files are plain C# with no Renode dependency beyond `Misc` helpers in a couple of places, so
they compile standalone.

## Verified behaviour

- CRC-16 check value over the ASCII string `123456789` is `29B1`.
- `7E C0 01 1B 7A 7F` is a complete frame carrying the two-byte payload `C0 01`.
- Payload `FF FF FF FF` (CRC `1D 0F`) becomes `7E FB EF BE FB EC 74 3D FC` — 48 body bits become 54
  after six stuffed zeros, 70 bits on the wire including the flags.
- Round-trips arbitrary payloads, including ones that imitate the `7E`/`7F` flags.

`selftest.sh` checks all of the above.
