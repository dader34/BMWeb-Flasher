# Runtime EWS toggle (design + bench-RE checklist)

Goal: flip the MS45.1 EWS (immobilizer) delete **on the fly**, without a
full-program reflash each time, by sending a custom KWP subcommand to the DME.

This is the DME analogue of the GS20 `06 08` read patch (`Gs20FullReader`): the
tool side is trivial; the capability only exists once the **program firmware
carries a handler** for it. Stock MS45 firmware has no service that changes the
live immobilizer state, so — exactly as a stock GS20 answers `B0` to subcode 8 —
a stock DME will reject the toggle telegram until it is running a patched
program. There is no tool-only shortcut.

## Decisions (locked)

- **Persistence: persistent, via an EEPROM/shadow flag.** The handler writes a
  flag to the DME's EEPROM-backed store; the EWS check reads it at init, so the
  toggle survives a key cycle. (The alternative — a RAM-only state that resets
  on key-off — was not chosen.)
- **Command shape: a custom subcode under an existing, dispatched service**, the
  same pattern as GS20 `06 08`. Less dispatcher surgery than claiming a fresh
  service byte.

## Two halves

### 1. Firmware patch (the load-bearing RE — bench + emulator, like subcode 8)

Installed once with a **full-program flash** (external + MPC; brick-capable, the
same path today's EWS delete rides). After install, toggling costs nothing.

The handler must:

1. **Be reachable by a custom subcode** under the chosen service, added to the
   KWP dispatcher the same way `06 08` was added to the GS20's `06` handler.
2. **On "delete" (mode `0x01`):** write the EEPROM flag = deleted, and set the
   runtime immobilizer-authenticated state so the engine-enable gate passes this
   cycle without a re-init.
3. **On "restore" (mode `0x00`):** write the EEPROM flag = active, and let the
   stock EWS handshake govern again.
4. **On "query" (mode `0x02`):** return the current flag without changing it.
5. **At init:** the EWS check reads the EEPROM flag and, when set, takes the
   deleted path — this is what makes it persist. This init hook is the
   equivalent of statically forcing `0xDB1C7`/`0xDB1D3`, but gated on the flag
   instead of hard-coded, so one flashed program serves both states.
6. **Re-checksum + re-sign** via the existing `Checksums_Signatures`
   (`CorrectProgramChecksums`, `SignMS45Program`) — already in the app.

Addresses to resolve against the `0044570LO02S` image (all provisional until
disassembled and emulator-checked, exactly as the subcode-8 routine is):

- [ ] KWP service dispatcher entry + a free subcode under the chosen service.
- [ ] The runtime immobilizer-authenticated state (the RAM the engine-enable
      gate reads each cycle; the live counterpart of program byte `0xDB1C7`).
- [ ] A free EEPROM/shadow byte for the persisted flag, and the init read site
      in the EWS check.
- [ ] Free program space for the handler body (C166).

Do **not** hardware-flash a hand-written handler before it passes the emulator
loop the subcode-8 routine went through.

### 2. Host command (this repo — `EwsToggle.cs`)

Pure telegram builder + response parser, transport behind `IKwpChannel` so it
unit-tests without hardware, mirroring how `Gs20FullReader` takes `IDs2Link`.
The app implements `IKwpChannel` by sending the raw telegram through the
**active, security-access'd EDIABAS programming session** (after
`authentisierung_start`, as `FlashDME_Data` sets up).

Open integration detail: the MS45 SGBD's **raw-send job name** (the mechanism
behind `seriennummer_lesen`'s `_TEL_ANTWORT`). The `.prg` name table is packed,
so confirm it at runtime via EDIABAS's info jobs rather than guessing.

## Reconcile before trusting offsets

`tests/…/EwsDeleteTests.cs` asserts a known-good deleted image has
`0xDB1C7 == 0x00` and `0xDB1D3 == 0x00`, but
`MS45.1_7561382_EWSdeleted_KNOWNGOOD_fullread.bin` has them at stock
`0x01`/`0x3F` (and its program region `0x60000–0xEFFFF` is byte-identical to the
`0044570` donor). That bin is a different car (differing VIN/coding at
`0x10000`). Decide whether it is a true known-good on the same build before
using it to locate the runtime immobilizer state.
