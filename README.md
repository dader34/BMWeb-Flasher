# BMWeb Flasher

A macOS/Linux port of the MS45 DME flasher, built with **.NET 8 + Avalonia**
instead of the original WPF / .NET Framework. Reads and flashes full and partial
binaries from the MS45.0 and MS45.1, auto-correcting checksums and signing files
before they are written.

This fork keeps the original's flashing logic byte-for-byte and adds a
cross-platform UI, an EWS-delete option, and a fault-code reader for the DME and
the transmission (TCU).

> Fork of [terraphantm/MS45-Flasher](https://github.com/terraphantm/MS45-Flasher).
> The checksum/signature and flash sequences are unchanged from upstream; the
> port replaces the Windows-only shell around them.

---

## What's different from upstream

- **Runs on macOS and Linux.** WPF to Avalonia; .NET Framework 4.5.2 to .NET 8.
  `kernel32!SetThreadExecutionState` (keep-awake) becomes `caffeinate` on macOS.
- **No Windows install assumptions.** Serial ports are `/dev/cu.usbserial-*`
  device paths, not `COM1`. Settings live in a JSON file under the user config
  dir instead of an `.exe.config`. EDIABAS's Windows-1252 dependency is handled
  by registering `CodePagesEncodingProvider` at startup (it is not built into
  .NET 8, which otherwise crashed every `new EdiabasNet()`).
- **EWS delete** (MS45.1 program `0044570LO02S` only). Patches the immobilizer
  check out of the program before a full-binary flash. Gated on the DME's own
  program reference, read over the wire via `ZIF_LESEN` ($22 $2503), so it will
  not enable on a car running a different program. **EWS delete requires a full
  program flash** (external + MPC); there is no external-only shortcut, so it is
  the brick-capable path (see below).
- **Fault Codes tab**: read / clear / export CSV, with a per-fault detail
  window, for either the **DME** (`ms450ds0.prg`, with SAE P-codes) or the
  **TCU** (`gs20.prg`).

---

## Prerequisites

- **.NET 8 SDK** (or newer with roll-forward). Verified on .NET 10 SDK / macOS
  arm64.
- An **INPA-compatible OBDII cable** (K+DCAN / FTDI). Set the cable latency to
  1 ms. The app auto-detects the cable; you do not need to know the port name.
- The EDIABAS SGBD files for your car. On first launch the app offers to
  download the E46 data set for you, so you normally do not need to provide
  these manually. If you already have an EDIABAS install, you can point the app
  at that folder instead (see [First run](#first-run)).

`EdiabasLib` is vendored as a git submodule and built from source; you do **not**
need a prebuilt `EdiabasLib.dll`.

## Building

```sh
git clone --recurse-submodules https://github.com/dader34/BMWeb-Flasher.git
cd BMWeb-Flasher
./build/setup.sh                       # pulls the submodule + adds its net8.0 target
dotnet build -c Release src/BmwebFlasher
dotnet run  -c Release --project src/BmwebFlasher
```

`build/setup.sh` is idempotent. It applies `build/ediabaslib-net8.patch`, which
adds a plain `net8.0` target framework to the EdiabasLib submodule (upstream
targets only `net*-windows` and `net481`). Nothing in the OBD serial path is
Windows-specific: `EdInterfaceObd` drives `System.IO.Ports.SerialPort`, which
is cross-platform on .NET 8+.

To run the tests (the EWS-delete patch has a full test suite):

```sh
dotnet test tests/BmwebFlasher.Tests
```

The fixture tests that verify the EWS patch against real images are skipped
unless `MS45_STOCK_BIN` and `MS45_EWS_BIN` point at local copies.

---

## Usage

Settings (serial port, ECU folder, SGBD) are stored automatically and reused on
the next launch.

### First run

On first launch, if no SGBD data is set up, a setup screen offers three choices:

- **Download required files** downloads the E46 data set and stores it under the
  app data folder (`~/Library/Application Support/bmweb-flasher/ecu` on macOS).
  A progress bar shows the download.
- **Choose an existing EDIABAS folder** points the app at a folder you already
  have (for example an EDIABAS `Ecu` directory). It is validated for the
  required SGBDs before it is accepted.
- **Skip** leaves it unset; you can configure it later with **Load SGBD**.

The app reads SGBDs from whichever folder it ends up pointed at. Downloading is
just one way to fill that path; **Load SGBD** can repoint it anywhere afterward,
and the choice persists.

The serial port is **auto-detected** on launch (it filters out non-cable ports
and prefers an FTDI / K+DCAN adapter). **Set Serial Port** lets you override the
choice, rescan after plugging in, or type a path manually.

### Identify

Connect the cable to the OBDII port with the ignition on, then **Identify DME**.
On success the DME information panel fills in, including the program reference
used to gate EWS delete.

### Reading

- **Tune only:** click **Read DME**.
- **Full backup:** check **Full Binary** first. This produces two files: the
  `_Flash` file (external flash) and the `_MPC` file (internal CPU flash). Keep
  both.

### Flashing

Make a full backup first.

**Tune** (parameter region only; recoverable, cannot brick the DME):
- **Load File**, pick a tune-sized file (`0x1D000`) with Full Binary
  **unchecked** (or a full binary with Full Binary **checked**, which extracts
  the tune slice for you), then **Flash Tune**.

**Full program** (external + MPC; this is the path that can brick the DME):
- Check **Full Binary**, **Load File** (external), **Load File 2 (MPC)**,
  optionally tick **EWS Delete**, then **Flash Program**.
- Both regions are always written together. The program signature
  (`FLASH_SIGNATUR_PRUEFEN Programm`) is computed over the external flash and the
  MPC as one, and finalizes only after the MPC block, so **there is no
  external-only / skip-MPC flash** -- writing just the external region leaves the
  program invalid (`Programm nicht vorhanden`). This is why **EWS delete needs a
  full program flash**: the EWS patch is in the external program, but the program
  is not valid until the MPC block finalizes the signature.
- A failed program flash is usually recoverable: the MPC bootloader lets the DME
  keep identifying over OBD, so you can re-flash a valid full program. Keep a
  known-good external + MPC pair, and ideally a BDM cable, on hand.
- If the files do not match you can render the DME unbootable, recoverable only
  with a BDM tool. The app does basic checking but it is not foolproof.

### Fault codes

On the **Fault Codes** tab, choose the **Module** (DME or TCU), then **Read
Fault Codes**. Double-click a fault for the full detail (location, symptom,
P-code text where available). **Export CSV** writes every field. **Clear Fault
Codes** erases the selected module's memory; faults for problems still present
return on the next drive cycle.

---

## Status

Verified on a real E46 (325i, MS45.1 + GS20):

- Tune flash: write then read-back is byte-identical to the flashed file.
- TCU calibration flash: erase, write and commit over raw DS2, with the
  read-back byte-identical to the written calibration.
- DME + TCU fault read / clear / export.
- Serial + security-access + memory read/write over a macOS FTDI cable.
- **Test Full Read** against a patched GS20: a tester's module identified
  correctly, and a full 512 KB read came back byte-for-byte correct against a
  known image.
- **.0DA calibration loading**: BMW's own Daten files are decoded to the raw
  64 KB image, verified against two calibrations read back off real modules.
- **GS20 program write**: four sector erases, 2,223 write telegrams and a
  commit, run on a tester's bench module; it identified after an ignition
  cycle. The sector map and the session surviving its own program's erase
  were both measured by that run rather than inferred.

Not yet exercised on a car through this port: the **full-program / EWS flash**
path (the brick-capable one). Treat it as unproven and keep a full backup.

**Write Program** erases and reprograms `0x0A0000-0x0DFFFF`. The module
keeps serving the programming session until it is power-cycled, so a failed
write can be redone at once -- but a bad program that is then power-cycled
leaves a module that does not answer over the diagnostic port, and recovery
is the boot-strap loader on the bench. The supply must read above 11.5 V or
nothing is erased. Not yet exercised: a stock (unpatched) image, and a write
onto a module at a different software release.

**Read Selected Region** needs a module flashed with a patched program. Stock GS20
firmware answers the `06` read only for the calibration, so the boot block and
the program come back `B0`; the patch adds a subcode (`06 08`) that reads any
address. A stock module is detected and reported rather than failing chunk by
chunk. The button only reads -- nothing is written either way. A full 512 KB
read has not yet been run end to end on hardware.

Note: the transmission uses `gs20.prg` directly rather than the `D_EGS.grp`
group file, because the group file's multi-module probe aborts on the first
non-responding candidate through this transport (an EDIABAS retry-timing gap,
not a code bug). `gs20.prg` is the only automatic TCU the M54 E46 shipped.

---

## Built using

* [EdiabasLib](https://github.com/uholeschak/ediabaslib): communicates with the ECUs
* [Avalonia](https://avaloniaui.net): cross-platform UI

## Acknowledgments

Upstream MS45-Flasher by [terraphantm](https://github.com/terraphantm/MS45-Flasher),
with disassembly help credited there to Hassmaschine. This fork only ports and
extends that work.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).

## Disclaimer

This program is inherently invasive and can render your DME unbootable and your
car undriveable. Care must be taken when using it. In no respect shall the
authors or contributors incur any liability for any damages arising out of,
resulting from, or in any way connected to the use of the application. Use at
your own risk, on a vehicle you own.
