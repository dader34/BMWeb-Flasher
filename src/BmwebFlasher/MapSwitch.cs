using System;
using System.Collections.Generic;

namespace BmwebFlasher
{
    /// <summary>
    /// Two-map switching for the MS45.1, program 0044570LO02S. EXPERIMENTAL:
    /// built from disassembly, not yet proven on a car.
    ///
    /// What the patch does:
    ///
    ///   - A second calibration ("map 2") is stored in the empty external flash
    ///     at 0xE0000-0xF7FFF. The CPU sees the flash mirrored at 0xFFE00000 and
    ///     0xFFF00000, and the program addresses the calibration as 0xFFE40000+,
    ///     so map 2 is the same address plus 0xA0000.
    ///
    ///   - Every table/axis lookup goes through 16 routines in the MPC that take
    ///     the table pointer in r3. Each gets a hook: when the map flag is set
    ///     and r3 points into the calibration, 0xA0000 is added.
    ///
    ///   - Single values are read inline, relative to r2, at some 4,400 sites,
    ///     so they cannot be hooked one by one. The program sets
    ///     r2 = 0xFFE47FF0 once, at start (three start sequences, no interrupt
    ///     prologue touches it), never stores through it, never stores a
    ///     pointer derived from it, and every offset used lies inside the
    ///     calibration. So the <see cref="Scope.FullTune"/> build moves r2 by
    ///     0xA0000 while map 2 is selected: at stored-data restore, and at the
    ///     gesture. Pointers derived from a moved r2 point into map 2 already
    ///     and fall outside the range the lookup hooks redirect, so nothing is
    ///     redirected twice. The earlier <see cref="Scope.MapsOnly"/> builds
    ///     left r2 alone, so their single values always came from map 1.
    ///
    ///   - The routine that builds CAN frame 0x316 (engine speed for the cluster)
    ///     is hooked where it stores the rpm. The chosen <see cref="Trigger"/>
    ///     toggles the map: the DSC button pressed two or four times, at any
    ///     time, or brake + full throttle held for 5 s with the engine stopped
    ///     and the car stationary. With the engine stopped the tach then shows
    ///     1000 rpm for map 1 or 2000 rpm for map 2, and does so for 3 s at
    ///     ignition-on; with the engine running the check-engine lamp blinks
    ///     once for map 1, twice for map 2 (see <see cref="MilStoreAddr"/>).
    ///     Images carrying another version, or the other trigger, are
    ///     recognised and brought up to date by <see cref="Build"/>.
    ///
    ///     The DSC button is not wired to the DME. The DSC module reports its
    ///     state in CAN frame 0x153 (ASC1), which the DME keeps at RAM
    ///     0x3FDCAC. A short press switches DTC on or off and flips bit 1 of
    ///     the first byte there at once (0x80 / 0x82), see
    ///     <see cref="VarDscState"/>. The stub counts changes of that bit
    ///     that come within 2 s of each other; four short presses give four
    ///     changes and leave DTC where it started.
    ///
    ///   - The selection is kept in bit 7 of stored-data block 56 (one byte that
    ///     stock code only ever sets to 0, 1 or 2), by replacing that block's
    ///     init / restore / save routines.
    ///
    ///   - The DME's safety monitor sums three code ranges while it runs and
    ///     compares the result with a value stored in the program header
    ///     ("ROM test level 2"). One range is MPC 0xBAE8-0xF5F7, which holds
    ///     the lookup routines, so hooking them changes the sum and the stored
    ///     value is brought up to date, see <see cref="CorrectRomTestSum"/>.
    ///     Builds before this did not, and on the car the monitor reset the
    ///     DME at start-up (faults 28B2 and 2796).
    ///
    ///   - The monitor sums the calibration the same way, cal 0x240-0x7C8,
    ///     through absolute addresses (map 1's), but reads the stored value
    ///     to compare with, cal 0x57DC-0x57E3, relative to r2: from the
    ///     selected map. So map 2 must carry map 1's value there, whatever its
    ///     own bytes are, see <see cref="CorrectCalTestSums"/>.
    ///
    /// Two things are assumed rather than proven, see <see cref="RamFlag"/> and
    /// <see cref="CallsPerSecond"/>.
    ///
    /// Checksums and signatures are NOT computed here; the caller runs the
    /// image through Checksums_Signatures, as for any other program flash.
    /// Because map 2 lives inside the program checksum range, changing it
    /// means a full program flash. Map 1 is the ordinary calibration.
    /// </summary>
    public static class MapSwitch
    {
        public const string SupportedProgramVersion = "0044570LO02S";

        public const int FullFlashLength = 0x100000;
        public const int MpcLength = 0x70000;

        /// <summary>The calibration partition in the external flash.</summary>
        public const int CalibrationStart = 0x40000;
        public const int CalibrationLength = 0x1D000;

        /// <summary>Where map 2 is stored, and how much of a tune fits there.</summary>
        public const int Map2Start = 0xE0000;
        public const int Map2Length = 0x18000;

        private const int ProgramVersionOffset = 0x6031C;
        private const int DataVersionOffset = 0x10;     // within a calibration
        private const int DataVersionLength = 12;

        private const int MpcFreeStart = 0x6E550;
        private const int MpcFreeEnd = 0x70000;

        private const uint R13 = 0x004017F0;

        /// <summary>Entry points of the lookup routines (MPC addresses).</summary>
        private static readonly int[] LookupEntries =
        {
            0xCFC8, 0xD054, 0xD0E4, 0xD178, 0xD210, 0xD264, 0xD2C0, 0xD31C,
            0xD380, 0xD38C, 0xD39C, 0xD3B8, 0xD3DC, 0xD44C, 0xD4C0, 0xD660,
        };

        /// <summary>The instruction each lookup routine starts with, unpatched.</summary>
        private static readonly uint[] LookupStockInsns =
        {
            0x89830000, 0x88E30000, 0xA1830000, 0xA0E30000, 0x88A30000, 0x88A30000, 0xA0A30000, 0xA0A30000,
            0x898DD7C6, 0x898DD7C6, 0x898DD7C6, 0x898DD7C6, 0x38E30000, 0x38E30000, 0x88ADD7C4, 0x88ADD7C4,
        };

        private const int TachStoreAddr = 0x4B6C4;        // sth r3,-0x3B28(r13)
        private const uint TachStoreInsn = 0xB06DC4D8;
        private const int TachVar = -0x3B28;
        internal const int Tach1000 = 6400;               // rpm * 6.4
        internal const int Tach2000 = 12800;

        /// <summary>
        /// The routine that builds CAN frame 0x545 (DME4: lamps, fuel
        /// consumption) is hooked at its last store of byte 0, which carries
        /// the check-engine lamp in bit 1. With the engine running the tach
        /// cannot be borrowed for the indication (the gearbox and DSC read
        /// the engine speed from 0x316), so the map is shown by blinking the
        /// check-engine lamp instead: once for map 1, twice for map 2. Only
        /// the cluster acts on that bit; the gearbox reads bytes 6-7 of 0x545
        /// and nothing else. The frame's bytes are kept in reverse order, so
        /// byte 0 is the last of the eight at r13-0x3B0C.
        /// </summary>
        private const int MilStoreAddr = 0x4BB78;         // stb r0,-0x3B05(r13)
        private const uint MilStoreInsn = 0x980DC4FB;
        private const int MilByte = -0x3B05;
        private const int MilBit = 0x02;
        private const int BlinkOnCalls = 50;              // 500 ms on ...
        private const int BlinkPeriodCalls = 100;         // ... in a 1 s period

        /// <summary>
        /// The fuel counter store in the same builder. An earlier build held
        /// the counter from here to swing the economy gauge, which only reads
        /// while the car is moving; it is gone. The site is kept only to be
        /// put back to stock when such a build is updated.
        /// </summary>
        private const int EconStoreAddr = 0x4BBA4;        // stw r0,-0x3B08(r13)
        private const uint EconStoreInsn = 0x900DC4F8;

        private const int NvDescriptor = 0x28AC + 56 * 0x1C;
        private static readonly uint[] NvStock = { 0xFFFCA0F8, 0xFFFCA104, 0xFFFCA110 };
        private const int NvVar = -0x3FD1;
        private const int NvSaveRequest = -0x2CC8 + 56;

        private const int VarEngineSpeed = -0x4BEC;       // 16 bit, rpm
        private const int VarVehicleSpeed = -0x3F95;      // 8 bit, km/h
        private const int VarPedal = -0x4061;             // 8 bit, 0.39 % per count
        private const int VarBrakeA = -0x4001;
        private const int VarBrakeB = -0x4002;

        /// <summary>
        /// The byte of the last CAN frame 0x153 (ASC1) from the DSC module
        /// that follows a short press of the DSC button. The DME's copy
        /// routine (MPC 0x4A480) keeps the frame's eight bytes at RAM 0x3FDCAC
        /// in reverse order, so this first byte is CAN byte 7. Watched on the
        /// car (2026-09-29): it reads 0x80, and 0x82 while DTC is on (the
        /// "A in a circle" lamp), flipping at once with each short press.
        /// Nothing else in it moves with the engine stopped.
        ///
        /// The published ASC1 layout calls byte 7 an alive counter; this
        /// DSC module keeps its DTC state there. Byte 0 bit 2 (LV_ASC_PASV,
        /// at 0x3FDCB3) is what the first build watched: it is DSC fully
        /// off, which the button only gives after a ~2 s hold, so the changes
        /// came too far apart to add up.
        /// </summary>
        private const int VarDscState = -0x3B44;
        /// <summary>The bit of that byte a short press toggles.</summary>
        public const int DscStateMask = 0x02;

        /// <summary>What the first DSC build watched: DSC fully off, bits 2-3 of CAN byte 0.</summary>
        private const int FirstVarDscState = -0x3B3D;
        private const int FirstDscStateMask = 0x0C;

        /// <summary>
        /// Which byte and bit of the 0x153 frame a DSC build watches.
        ///
        ///   Car     the short-press (DTC) state, see <see cref="VarDscState"/>.
        ///   First   the first build: DSC fully off, which needs a long press.
        ///           Kept so an image carrying it is recognised and updated.
        /// </summary>
        internal enum DscWatch { Car, First }

        /// <summary>
        /// RAM worth reading on a car to see the trigger work (the DME
        /// answers speicher_lesen_ascii on segment LAR for RAM): the last
        /// 0x153 frame (8 bytes, CAN byte 7 first), and the DME's engine
        /// speed, vehicle speed and the tach value the stub stores. The DSC
        /// trigger was diagnosed this way on 2026-09-29.
        /// </summary>
        public const uint RamCanAsc1 = 0x3FDCAC;
        public const uint RamDscState = (uint)((int)R13 + VarDscState);
        public const uint RamEngineSpeed = (uint)((int)R13 + VarEngineSpeed);
        public const uint RamVehicleSpeed = (uint)((int)R13 + VarVehicleSpeed);
        public const uint RamTach = (uint)((int)R13 + TachVar);

        /// <summary>
        /// RAM used by the patch. UNVERIFIED: these are gaps that look like
        /// alignment padding (a byte variable, three unreferenced bytes, then a
        /// 32-bit variable). No code addresses them directly, which is not proof
        /// that nothing uses them. Read them on a running car before trusting
        /// this build.
        /// </summary>
        public const uint RamFlag = 0x3FA195;             // bit0 map, bit1 latched (pedal), bits 2-3 last DSC state
        public const uint RamHoldCounter = 0x3FA196;      // 16 bit: the pedal hold, or the time since the last DSC press
        public const uint RamDisplayCounter = 0x3FA1ED;   // 8 bit
        /// <summary>
        /// How many DSC state changes have come in quick succession. The
        /// byte display counter of the earlier versions, which this version
        /// does not use: its display counter is <see cref="RamWideDisplayCounter"/>.
        /// </summary>
        public const uint RamPressCounter = RamDisplayCounter;
        /// <summary>
        /// The two bytes after the display counter, in the same gap. The
        /// delayed version counted down to its indication here. This
        /// version keeps its display counter here instead, because the
        /// longer indication at ignition-on does not fit in a byte.
        /// </summary>
        public const uint RamStartupDelay = 0x3FA1EE;     // 16 bit
        public const uint RamWideDisplayCounter = RamStartupDelay;
        /// <summary>
        /// The check-engine blink: how many blinks are left, and where in
        /// the blink period the lamp is. Another gap that looks like
        /// alignment padding (three unreferenced bytes at 0x3FA1C9).
        /// </summary>
        public const uint RamBlinks = 0x3FA1C9;           // 8 bit
        public const uint RamBlinkPhase = 0x3FA1CA;       // 8 bit
        /// <summary>
        /// Calls left in which DSC state changes are not counted, from
        /// ignition-on. The receive buffer starts at zero and the DSC module's
        /// first frames settle into their normal state; the two changes that
        /// makes inside the press window read as a double tap, and flipped
        /// the map at every key-on on the two-press build. Another verified
        /// gap (0x3FA281-0x3FA283 are unreferenced).
        /// </summary>
        public const uint RamStartupLockout = 0x3FA282;   // 16 bit
        public const int StartupLockoutSeconds = 10;
        private const int StartupLockoutCalls = StartupLockoutSeconds * 100;

        /// <summary>
        /// UNVERIFIED: how often the 0x316 builder is assumed to run. It sets
        /// the real length of the 5 s hold and the 1.5 s tach indication.
        /// </summary>
        public const int CallsPerSecond = 100;

        private const int HoldCalls = 5 * CallsPerSecond;
        private const int DisplayCalls = 150;

        /// <summary>
        /// How many DSC presses toggle the map (the caller chooses from
        /// <see cref="DscPressChoices"/>), and how close together they must be.
        /// An even count leaves DTC as it started.
        /// </summary>
        public const int DefaultDscPresses = 4;
        public static readonly int[] DscPressChoices = { 2, 4 };
        public const int DscPressWindowSeconds = 2;
        private const int DscPressWindowCalls = DscPressWindowSeconds * CallsPerSecond;

        /// <summary>How long the tach shows the map at ignition-on.</summary>
        public const int StartupDisplaySeconds = 3;
        private const int StartupDisplayCalls = StartupDisplaySeconds * CallsPerSecond;

        /// <summary>How long the delayed version waits after power-up.</summary>
        public const int StartupDelaySeconds = 6;
        private const int StartupDelayCalls = StartupDelaySeconds * CallsPerSecond;

        /// <summary>
        /// When the tach shows the active map without being asked.
        ///
        ///   ImmediateLong  this version: at power-up, for 3 s.
        ///   Immediate      at power-up, for 1.5 s.
        ///   None           never, only after the gesture.
        ///   Delayed        once, a few seconds after power-up.
        ///
        /// None and Delayed were built while the immediate indication was
        /// blamed for relays clicking and lamps flashing at start-up. The
        /// cause was the safety monitor's code sum, see
        /// <see cref="CorrectRomTestSum"/>, so the indication is immediate
        /// again. The gesture's own indication is 1.5 s in every version.
        /// </summary>
        internal enum StartupIndication { None, Immediate, Delayed, ImmediateLong }

        /// <summary>
        /// What toggles the map. Either can be built today; the caller chooses.
        ///
        ///   DscButton      the DSC button pressed four times.
        ///   Pedals         brake and full throttle held for 5 s. The only
        ///                  trigger of the earlier versions.
        /// </summary>
        public enum Trigger { Pedals, DscButton }

        public const Trigger DefaultTrigger = Trigger.DscButton;

        public static string Describe(Trigger trigger, int dscPresses = DefaultDscPresses)
            => trigger == Trigger.DscButton
                ? "DSC button pressed " + dscPresses + " times"
                : "brake + full throttle held 5 s";

        /// <summary>
        /// How much of the tune switches.
        ///
        ///   FullTune   single values too, by moving r2, see the class summary.
        ///              The only scope built today.
        ///   MapsOnly   tables and curves; single values stay map 1's. The
        ///              scope of the earlier builds, kept so they are recognised.
        /// </summary>
        public enum Scope { MapsOnly, FullTune }

        public static string Describe(Scope scope)
            => scope == Scope.FullTune ? "full tune" : "maps only";

        /// <summary>r2 as the program sets it: the calibration's small-data base.</summary>
        private const int R2High = -0x1C;                 // lis r2, -0x1C  -> 0xFFE40000
        private const int R2Low = 0x7FF0;                 // ori r2, r2, 0x7FF0

        /// <summary>A version of the code: what triggers the switch and how the map is shown at power-up.</summary>
        internal readonly struct Version
        {
            public readonly Trigger Trigger;
            public readonly StartupIndication Startup;
            /// <summary>Only meaningful for the DSC trigger, as is <see cref="Presses"/>.</summary>
            public readonly DscWatch Watch;
            public readonly Scope Scope;
            /// <summary>How many DSC presses toggle the map; 0 for the pedals.</summary>
            public readonly int Presses;
            /// <summary>
            /// The DSC trigger works with the engine running and the car
            /// moving; the earlier DSC builds, like the pedals, only with the
            /// engine stopped and the car stationary.
            /// </summary>
            public readonly bool WhileRunning;
            /// <summary>
            /// Generation 0 started the ignition-on indication from the
            /// stored-data defaults as well as from the restore, and its
            /// driving builds blinked the lamp with a stuck phase. Generations
            /// 1 and 2 used the economy gauge, which only reads while moving.
            /// Generation 3: the tach with the engine stopped (from the
            /// restore only at ignition-on), the check-engine lamp blinking
            /// while the engine runs, and DSC presses ignored for 10 s after
            /// ignition-on. Builds of the generations in between are
            /// recognised only as earlier builds, see <see cref="CarriesSomeBuild"/>.
            /// </summary>
            public readonly int Gen;

            public Version(Trigger trigger, StartupIndication startup, DscWatch watch = DscWatch.Car,
                Scope scope = Scope.MapsOnly, int presses = DefaultDscPresses, bool whileRunning = false, int gen = 0)
            {
                Trigger = trigger;
                Startup = startup;
                Watch = trigger == Trigger.DscButton ? watch : DscWatch.Car;
                Scope = scope;
                Presses = trigger == Trigger.DscButton ? presses : 0;
                WhileRunning = trigger == Trigger.DscButton && whileRunning;
                Gen = gen;
            }

            /// <summary>Whether the lamp stub is built and its site hooked: the driving builds from generation 3.</summary>
            public bool LampHook => WhileRunning && Gen >= 3;
            /// <summary>Whether DSC presses are ignored for a while after ignition-on.</summary>
            public bool Lockout => Trigger == Trigger.DscButton && Gen >= 3;

            public static bool operator ==(Version a, Version b)
                => a.Trigger == b.Trigger && a.Startup == b.Startup && a.Watch == b.Watch && a.Scope == b.Scope &&
                   a.Presses == b.Presses && a.WhileRunning == b.WhileRunning && a.Gen == b.Gen;
            public static bool operator !=(Version a, Version b) => !(a == b);
            public override bool Equals(object obj) => obj is Version v && this == v;
            public override int GetHashCode()
                => ((((((int)Trigger * 16 + (int)Startup) * 4 + (int)Watch) * 2 + (int)Scope) * 8 + Presses) * 2 +
                    (WhileRunning ? 1 : 0)) * 4 + Gen;
        }

        private const int CurrentGen = 3;

        /// <summary>
        /// The version built today for a trigger: full tune, 3 s on the tach
        /// at ignition-on, and for the DSC button a switch that works while
        /// driving, shown on the lamp.
        /// </summary>
        private static Version CurrentVersion(Trigger trigger, int dscPresses = DefaultDscPresses)
            => new Version(trigger, StartupIndication.ImmediateLong, DscWatch.Car, Scope.FullTune, dscPresses, true, CurrentGen);

        private static bool IsCurrent(Version version)
            => version == CurrentVersion(version.Trigger, version.Presses) &&
               (version.Trigger != Trigger.DscButton || Array.IndexOf(DscPressChoices, version.Presses) >= 0);

        private static readonly Version[] KnownVersions =
        {
            CurrentVersion(Trigger.DscButton, 4),
            CurrentVersion(Trigger.DscButton, 2),
            CurrentVersion(Trigger.Pedals),
            // Generations 1 and 2 (the economy gauge) and the driving builds
            // of generation 0 (the lamp with a stuck phase) are not
            // reproduced; they are recognised as earlier builds only.
            // Generation 0 with the pedals:
            new Version(Trigger.Pedals, StartupIndication.ImmediateLong, DscWatch.Car, Scope.FullTune),
            // the DSC builds that only switched with the engine stopped
            new Version(Trigger.DscButton, StartupIndication.ImmediateLong, DscWatch.Car, Scope.FullTune, 4),
            new Version(Trigger.DscButton, StartupIndication.ImmediateLong, DscWatch.Car, Scope.FullTune, 2),
            new Version(Trigger.DscButton, StartupIndication.ImmediateLong, DscWatch.Car, Scope.MapsOnly),
            new Version(Trigger.Pedals, StartupIndication.ImmediateLong, DscWatch.Car, Scope.MapsOnly),
            new Version(Trigger.DscButton, StartupIndication.ImmediateLong, DscWatch.First),
            new Version(Trigger.Pedals, StartupIndication.Immediate),
            new Version(Trigger.Pedals, StartupIndication.None),
            new Version(Trigger.Pedals, StartupIndication.Delayed),
        };
        private const int PedalFull = 0xF0;               // 93.75 %
        private const int PedalReleased = 0x20;           // 12.5 %

        private const int Map2DeltaHigh = 0x000A;         // addis r3,r3,0xA

        /// <summary>
        /// The safety monitor's code sum: the stored value, and the table of
        /// ranges it covers (start and end address, end not included).
        /// </summary>
        private const int RomTestSumOffset = 0x60600;
        private const int RomTestRangesOffset = 0x60608;
        private static readonly uint[] RomTestRanges =
        {
            0x0000BAE8, 0x0000F5F8,       // MPC, holds the lookup routines
            0xFFF60630, 0xFFF68C2C,       // external flash
            0x00000140, 0x000002D4,       // MPC
        };
        private const ulong RomTestSeed = 0x0123456789ABCDEFUL;
        private const int CalibrationRangeTag = 0x7FF2;   // address >> 17

        /// <summary>
        /// The safety monitor's calibration sum: the range it covers (listed
        /// in the program header right after the code ranges) and where the
        /// calibration stores the value, as an offset into the calibration.
        /// </summary>
        private const int CalTestRangeOffset = RomTestRangesOffset + 4 * 6;
        private const uint CalibrationBase = 0xFFE40000;   // where the program sees the calibration
        private const uint CalTestRangeStart = 0xFFE40240, CalTestRangeEnd = 0xFFE407C8;
        private const int CalTestSumOffset = 0x57DC;

        public sealed class Result
        {
            public byte[] Flash;
            public byte[] Mpc;
            /// <summary>True when the input already carried the patch.</summary>
            public bool WasAlreadyPatched;
            /// <summary>True when the input carried the earlier version and its code was replaced.</summary>
            public bool WasUpdated;
            public bool MapsIdentical;
            public int CodeBytes;
            public List<string> Log = new List<string>();
        }

        // ------------------------------------------------------------------
        // Inspection
        // ------------------------------------------------------------------

        public static string ReadProgramVersion(byte[] flash)
            => ReadAscii(flash, ProgramVersionOffset, 12);

        /// <summary>The data version string of a calibration, e.g. 0044570LO00S.</summary>
        public static string ReadDataVersion(byte[] calibration)
            => ReadAscii(calibration, DataVersionOffset, DataVersionLength);

        private static string ReadAscii(byte[] data, int offset, int length)
        {
            if (data == null || data.Length < offset + length)
                return null;
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                if (b < 0x20 || b > 0x7E)
                    return null;
                chars[i] = (char)b;
            }
            return new string(chars);
        }

        /// <summary>
        /// True when the pair carries the map switch and a second tune is
        /// stored in the map 2 area.
        /// </summary>
        public static bool HasMap2(byte[] flash, byte[] mpc)
        {
            if (flash == null || flash.Length != FullFlashLength || !IsAlreadyPatched(mpc))
                return false;
            return ReadAscii(flash, Map2Start + DataVersionOffset, DataVersionLength) != null;
        }

        /// <summary>
        /// True when the MPC carries the map switch, in this version or an
        /// earlier one.
        /// </summary>
        public static bool IsAlreadyPatched(byte[] mpc) => VersionOf(mpc) != null || CarriesSomeBuild(mpc);

        /// <summary>The trigger a patched MPC switches on; null when it is not patched.</summary>
        public static Trigger? InstalledTrigger(byte[] mpc) => VersionOf(mpc)?.Trigger;

        /// <summary>How many DSC presses a patched MPC switches on; 0 for the pedals, null when it is not patched.</summary>
        public static int? InstalledDscPresses(byte[] mpc) => VersionOf(mpc)?.Presses;

        /// <summary>How much of the tune a patched MPC switches; null when it is not patched.</summary>
        public static Scope? InstalledScope(byte[] mpc) => VersionOf(mpc)?.Scope;

        private static Version? VersionOf(byte[] mpc)
        {
            foreach (Version version in KnownVersions)
                if (CarriesVersion(mpc, version))
                    return version;
            return null;
        }

        /// <summary>
        /// True when the MPC carries a build of the map switch that is not
        /// reproduced any more (the lamp-blink build): every hook site
        /// branches into the free area, or is stock, and the free area starts
        /// with the lookup stubs, which every build shares. Such a build is
        /// replaced whole by <see cref="Build"/>.
        /// </summary>
        private static bool CarriesSomeBuild(byte[] mpc)
        {
            if (mpc == null || mpc.Length != MpcLength)
                return false;

            var freeArea = new byte[CarCheckLength];
            Buffer.BlockCopy(mpc, MpcFreeStart, freeArea, 0, CarCheckLength);
            if (!LooksLikeOurCode(freeArea))
                return false;

            for (int i = 0; i < LookupEntries.Length; i++)
                if (!BranchesIntoFreeArea(mpc, LookupEntries[i], false))
                    return false;
            if (!BranchesIntoFreeArea(mpc, TachStoreAddr, true))
                return false;
            if (Read32(mpc, MilStoreAddr) != MilStoreInsn && !BranchesIntoFreeArea(mpc, MilStoreAddr, true))
                return false;
            if (Read32(mpc, EconStoreAddr) != EconStoreInsn && !BranchesIntoFreeArea(mpc, EconStoreAddr, true))
                return false;
            for (int i = 0; i < 3; i++)
            {
                uint fn = Read32(mpc, NvDescriptor + 4 * i);
                if (fn < MpcFreeStart || fn >= MpcFreeEnd)
                    return false;
            }
            return true;
        }

        private static bool BranchesIntoFreeArea(byte[] mpc, int at, bool link)
        {
            uint insn = Read32(mpc, at);
            if ((insn & 0xFC000003) != (link ? 0x48000001u : 0x48000000u))
                return false;
            int offset = (int)(insn & 0x03FFFFFC);
            if ((offset & 0x02000000) != 0)
                offset -= 0x04000000;
            long target = at + offset;
            return target >= MpcFreeStart && target < MpcFreeEnd;
        }

        /// <summary>
        /// Whether the free area begins with this class's lookup stubs. They
        /// come first in every build and never changed, so this tells our
        /// code from any other modification even when the rest differs.
        /// </summary>
        private static bool LooksLikeOurCode(byte[] freeArea)
        {
            BuildCode(CurrentVersion(DefaultTrigger), out _, out uint tachStub, out _, out _);
            byte[] code = BuildCode(CurrentVersion(DefaultTrigger), out _, out _, out _, out _);
            int lookupBytes = (int)(tachStub - MpcFreeStart);
            if (freeArea.Length < lookupBytes)
                return false;
            for (int i = 0; i < lookupBytes; i++)
                if (freeArea[i] != code[i])
                    return false;
            return true;
        }

        /// <summary>
        /// Where in the MPC to look to tell whether a car carries the map
        /// switch, and how much to read: the free area the code goes in.
        /// </summary>
        public const int CarCheckOffset = MpcFreeStart;
        public const int CarCheckLength = 0x600;

        /// <summary>Where a stored map 2 keeps its data version, in the external flash.</summary>
        public const int Map2DataVersionOffset = Map2Start + DataVersionOffset;
        public const int DataVersionFieldLength = DataVersionLength;

        public enum CarState
        {
            /// <summary>The free area is empty.</summary>
            NotInstalled,
            /// <summary>The code this class writes today.</summary>
            Current,
            /// <summary>The code of an earlier version.</summary>
            Earlier,
            /// <summary>Something else is in the free area.</summary>
            Unrecognised,
        }

        /// <summary>
        /// Tells from the first <see cref="CarCheckLength"/> bytes of the
        /// MPC's free area, as read from a car, whether it carries the map
        /// switch.
        /// </summary>
        public static CarState StateOnCar(byte[] freeArea)
        {
            if (FreeAreaIsEmpty(freeArea))
                return CarState.NotInstalled;
            Version? version = VersionOnCar(freeArea);
            if (version == null)
                return LooksLikeOurCode(freeArea) ? CarState.Earlier : CarState.Unrecognised;
            return IsCurrent(version.Value) ? CarState.Current : CarState.Earlier;
        }

        /// <summary>The trigger the car's map switch uses, from the same bytes; null when it carries none.</summary>
        public static Trigger? TriggerOnCar(byte[] freeArea) => VersionOnCar(freeArea)?.Trigger;

        /// <summary>How many DSC presses the car's map switch needs; 0 for the pedals, null when it carries none.</summary>
        public static int? DscPressesOnCar(byte[] freeArea) => VersionOnCar(freeArea)?.Presses;

        /// <summary>How much of the tune the car's map switch switches; null when it carries none.</summary>
        public static Scope? ScopeOnCar(byte[] freeArea) => VersionOnCar(freeArea)?.Scope;

        private static void CheckFreeArea(byte[] freeArea)
        {
            if (freeArea == null || freeArea.Length != CarCheckLength)
                throw new ArgumentException("Expected the first 0x" + CarCheckLength.ToString("X") +
                                            " bytes of the MPC's free area.", nameof(freeArea));
        }

        private static bool FreeAreaMatches(byte[] freeArea, byte[] code)
        {
            for (int i = 0; i < freeArea.Length; i++)
                if (freeArea[i] != (i < code.Length ? code[i] : (byte)0xFF))
                    return false;
            return true;
        }

        private static bool FreeAreaIsEmpty(byte[] freeArea)
        {
            CheckFreeArea(freeArea);
            return FreeAreaMatches(freeArea, new byte[0]);
        }

        private static Version? VersionOnCar(byte[] freeArea)
        {
            CheckFreeArea(freeArea);
            foreach (Version version in KnownVersions)
            {
                byte[] code = BuildCode(version, out _, out _, out _, out _);
                if (code.Length <= CarCheckLength && FreeAreaMatches(freeArea, code))
                    return version;
            }
            return null;
        }

        /// <summary>
        /// Turns the map 2 area, as read from a car, back into the tune it
        /// was stored from. Only tunes that are empty past the area are
        /// ever stored, so filling the rest with 0xFF gives the original,
        /// checksum and signature included - except for the safety monitor's
        /// sum at 0x57DC, which holds map 1's value, see
        /// <see cref="CorrectCalTestSums"/>. Returns null when the area
        /// holds no tune.
        /// </summary>
        public static byte[] Map2AsCalibration(byte[] map2Area)
        {
            if (map2Area == null || map2Area.Length != Map2Length)
                throw new ArgumentException("Expected the 0x" + Map2Length.ToString("X") +
                                            " bytes of the map 2 area.", nameof(map2Area));

            if (ReadAscii(map2Area, DataVersionOffset, DataVersionLength) == null)
                return null;

            var cal = new byte[CalibrationLength];
            for (int i = Map2Length; i < cal.Length; i++)
                cal[i] = 0xFF;
            Buffer.BlockCopy(map2Area, 0, cal, 0, Map2Length);
            return cal;
        }

        /// <summary>The data version in a field read from the car, or null when it holds none.</summary>
        public static string DataVersionFrom(byte[] field)
            => field != null && field.Length == DataVersionLength ? ReadAscii(field, 0, DataVersionLength) : null;

        /// <summary>True when the MPC carries exactly the code this class writes today, for either trigger.</summary>
        public static bool IsCurrentVersion(byte[] mpc)
        {
            Version? version = VersionOf(mpc);
            return version != null && IsCurrent(version.Value);
        }

        private static bool CarriesVersion(byte[] mpc, Version version)
        {
            if (mpc == null || mpc.Length != MpcLength)
                return false;

            byte[] code = BuildCode(version, out uint[] lookupStubs, out uint tachStub, out uint runStub, out uint[] nvStubs);
            for (int i = 0; i < code.Length; i++)
                if (mpc[MpcFreeStart + i] != code[i])
                    return false;
            for (int i = MpcFreeStart + code.Length; i < MpcFreeEnd; i++)
                if (mpc[i] != 0xFF)
                    return false;

            for (int i = 0; i < LookupEntries.Length; i++)
                if (Read32(mpc, LookupEntries[i]) != Branch((uint)LookupEntries[i], lookupStubs[i], false))
                    return false;
            if (Read32(mpc, TachStoreAddr) != Branch(TachStoreAddr, tachStub, true))
                return false;
            // The lamp site is hooked by the driving builds; the old gauge
            // site is stock in every reproduced version.
            uint milExpected = version.LampHook ? Branch(MilStoreAddr, runStub, true) : MilStoreInsn;
            if (Read32(mpc, MilStoreAddr) != milExpected)
                return false;
            if (Read32(mpc, EconStoreAddr) != EconStoreInsn)
                return false;
            for (int i = 0; i < 3; i++)
                if (Read32(mpc, NvDescriptor + 4 * i) != nvStubs[i])
                    return false;
            return true;
        }

        /// <summary>
        /// Why this pair cannot be patched, or null when it can. A pair that is
        /// already patched is accepted (its maps can be replaced).
        /// </summary>
        public static string BlockedReason(byte[] flash, byte[] mpc)
        {
            if (flash == null || flash.Length != FullFlashLength)
                return "Map switch needs a full 1 MB external flash image.";
            if (mpc == null || mpc.Length != MpcLength)
                return "Map switch needs the 448 KB MPC (internal flash) image.";

            string version = ReadProgramVersion(flash);
            if (version != SupportedProgramVersion)
            {
                return "Map switch is only built for program " + SupportedProgramVersion +
                       ", but this image reports " + (version ?? "an unreadable version") + ".";
            }

            if (IsAlreadyPatched(mpc))
                return null;

            for (int i = MpcFreeStart; i < MpcFreeEnd; i++)
                if (mpc[i] != 0xFF)
                    return "The MPC's free area (0x6E550 up) is not empty, so it carries some other modification.";

            for (int i = Map2Start; i < Map2Start + Map2Length; i++)
                if (flash[i] != 0xFF)
                    return "The external flash area for map 2 (0xE0000-0xF7FFF) is not empty.";

            for (int i = 0; i < LookupEntries.Length; i++)
                if (Read32(mpc, LookupEntries[i]) != LookupStockInsns[i])
                    return "The MPC does not carry the expected code at lookup routine 0x" +
                           LookupEntries[i].ToString("X") + ".";

            if (Read32(mpc, TachStoreAddr) != TachStoreInsn)
                return "The MPC does not carry the expected code at the engine speed frame builder.";

            if (Read32(mpc, MilStoreAddr) != MilStoreInsn || Read32(mpc, EconStoreAddr) != EconStoreInsn)
                return "The MPC does not carry the expected code at the lamp / fuel frame builder.";

            for (int i = 0; i < 3; i++)
                if (Read32(mpc, NvDescriptor + 4 * i) != NvStock[i])
                    return "The MPC's stored-data table does not match the expected layout.";

            return null;
        }

        /// <summary>
        /// Takes a tune in any accepted form (a calibration partial of
        /// 0x1D000-0x20000 bytes, or a full 1 MB image) and returns the
        /// 0x1D000-byte calibration. Throws when the file is neither.
        /// </summary>
        public static byte[] ExtractCalibration(byte[] file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            var cal = new byte[CalibrationLength];
            if (file.Length == FullFlashLength)
                Buffer.BlockCopy(file, CalibrationStart, cal, 0, CalibrationLength);
            else if (file.Length >= CalibrationLength && file.Length <= 0x20000)
                Buffer.BlockCopy(file, 0, cal, 0, CalibrationLength);
            else
                throw new InvalidOperationException(
                    "A tune must be a calibration partial (0x1D000 bytes) or a full 1 MB image; " +
                    "this file is 0x" + file.Length.ToString("X") + " bytes.");
            return cal;
        }

        /// <summary>Why a calibration cannot be stored as map 2, or null when it can.</summary>
        public static string Map2BlockedReason(byte[] calibration, byte[] map1)
        {
            if (calibration == null || calibration.Length != CalibrationLength)
                return "Map 2 is not a 0x1D000-byte calibration.";

            for (int i = Map2Length; i < CalibrationLength; i++)
                if (calibration[i] != 0xFF)
                    return "Map 2 has data past offset 0x" + Map2Length.ToString("X") +
                           ", which does not fit in the free flash area.";

            string v1 = ReadDataVersion(map1);
            string v2 = ReadDataVersion(calibration);
            if (v1 == null || v2 == null || v1 != v2)
                return "Map 2 is for data version " + (v2 ?? "(unreadable)") + " but map 1 is " +
                       (v1 ?? "(unreadable)") + ". Both maps must share one layout.";

            return null;
        }

        // ------------------------------------------------------------------
        // Build
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns patched copies of the pair. <paramref name="map1"/> null keeps
        /// the calibration already in the image; <paramref name="map2"/> null
        /// stores a copy of map 1. The caller's arrays are left alone.
        /// </summary>
        public static Result Build(byte[] flash, byte[] mpc, byte[] map1, byte[] map2)
            => Build(flash, mpc, map1, map2, DefaultTrigger);

        /// <summary>
        /// As <see cref="Build(byte[], byte[], byte[], byte[])"/>, choosing the
        /// trigger and, for the DSC button, the number of presses. A pair
        /// that carries other choices, or an earlier version, has its code
        /// replaced.
        /// </summary>
        public static Result Build(byte[] flash, byte[] mpc, byte[] map1, byte[] map2, Trigger trigger,
            int dscPresses = DefaultDscPresses)
        {
            if (trigger == Trigger.DscButton && Array.IndexOf(DscPressChoices, dscPresses) < 0)
                throw new ArgumentException("The DSC button trigger takes 2 or 4 presses.", nameof(dscPresses));
            return Build(flash, mpc, map1, map2, CurrentVersion(trigger, dscPresses));
        }

        /// <summary>
        /// As <see cref="Build(byte[], byte[], byte[], byte[])"/>, choosing one
        /// of the earlier pedal-triggered versions by its start-up indication.
        /// Only the tests build these, to prove an image carrying one is
        /// recognised and updated.
        /// </summary>
        internal static Result Build(byte[] flash, byte[] mpc, byte[] map1, byte[] map2,
            StartupIndication startupIndication)
            => Build(flash, mpc, map1, map2, new Version(Trigger.Pedals, startupIndication));

        internal static Result Build(byte[] flash, byte[] mpc, byte[] map1, byte[] map2, Version version)
        {
            string blocked = BlockedReason(flash, mpc);
            if (blocked != null)
                throw new InvalidOperationException(blocked);

            var result = new Result
            {
                Flash = (byte[])flash.Clone(),
                Mpc = (byte[])mpc.Clone(),
                WasAlreadyPatched = IsAlreadyPatched(mpc),
            };

            // An unpatched pair must carry the sum its own code gives. A
            // patched one may not: earlier builds left the stock value.
            if (!result.WasAlreadyPatched && ReadRomTestSum(flash) != RomTestSum(flash, mpc))
                throw new InvalidOperationException(
                    "The safety monitor's code sum in the program header does not match the code, " +
                    "so this pair is not what the map switch was built for.");

            if (map1 != null)
            {
                if (map1.Length != CalibrationLength)
                    throw new InvalidOperationException("Map 1 is not a 0x1D000-byte calibration.");
                Buffer.BlockCopy(map1, 0, result.Flash, CalibrationStart, CalibrationLength);
                result.Log.Add("Map 1: replaced the calibration at 0x40000");
            }
            else
            {
                result.Log.Add("Map 1: kept the calibration already in the image");
            }

            var current1 = new byte[CalibrationLength];
            Buffer.BlockCopy(result.Flash, CalibrationStart, current1, 0, CalibrationLength);

            if (ReadDataVersion(current1) == null)
                throw new InvalidOperationException(
                    "The external flash has no tune in it. Choose a map 1 tune.");

            byte[] second = map2 ?? current1;
            string map2Blocked = Map2BlockedReason(second, current1);
            if (map2Blocked != null)
                throw new InvalidOperationException(map2Blocked);

            Buffer.BlockCopy(second, 0, result.Flash, Map2Start, Map2Length);
            result.Log.Add(map2 != null
                ? "Map 2: stored at 0xE0000"
                : "Map 2: stored a copy of map 1 at 0xE0000");
            CorrectCalTestSums(result, current1);

            result.MapsIdentical = true;
            for (int i = 0; i < Map2Length && result.MapsIdentical; i++)
                if (current1[i] != second[i])
                    result.MapsIdentical = false;

            byte[] code = BuildCode(version, out uint[] lookupStubs, out uint tachStub, out uint runStub, out uint[] nvStubs);
            result.CodeBytes = code.Length;

            if (CarriesVersion(mpc, version))
            {
                result.Log.Add("Code: image already carries this version of the map switch (" +
                               Describe(version.Trigger, version.Presses) + ", " + Describe(version.Scope) +
                               "), left unchanged");
                CorrectRomTestSum(result);
                return result;
            }

            // Any other known version is replaced whole: its code area is
            // cleared first, since the versions differ in length, and every
            // patch site is written again below.
            if (result.WasAlreadyPatched)
            {
                Version? was = VersionOf(mpc);
                for (int i = MpcFreeStart; i < MpcFreeEnd; i++)
                    result.Mpc[i] = 0xFF;
                // the indication sites, whichever the earlier version used
                Write32(result.Mpc, MilStoreAddr, MilStoreInsn);
                Write32(result.Mpc, EconStoreAddr, EconStoreInsn);
                result.WasUpdated = true;
                if (was == null)
                    result.Log.Add("Code: replaced an earlier build of the map switch");
                else
                    result.Log.Add(was.Value.Trigger == version.Trigger && was.Value.Scope == version.Scope && was.Value.Presses == version.Presses
                        ? "Code: replaced the earlier version of the map switch"
                        : "Code: replaced the map switch, was " + Describe(was.Value.Trigger, was.Value.Presses) + ", " + Describe(was.Value.Scope));
            }

            Buffer.BlockCopy(code, 0, result.Mpc, MpcFreeStart, code.Length);
            for (int i = 0; i < LookupEntries.Length; i++)
                Write32(result.Mpc, LookupEntries[i], Branch((uint)LookupEntries[i], lookupStubs[i], false));
            Write32(result.Mpc, TachStoreAddr, Branch(TachStoreAddr, tachStub, true));
            if (version.LampHook)
                Write32(result.Mpc, MilStoreAddr, Branch(MilStoreAddr, runStub, true));
            for (int i = 0; i < 3; i++)
                Write32(result.Mpc, NvDescriptor + 4 * i, nvStubs[i]);

            result.Log.Add("Code: " + LookupEntries.Length + " lookup hooks, gesture/tach routine and " +
                           "stored-data routines, " + code.Length + " bytes at MPC 0x" +
                           MpcFreeStart.ToString("X") + ", trigger: " + Describe(version.Trigger, version.Presses) +
                           ", switches " + Describe(version.Scope));
            CorrectRomTestSum(result);
            return result;
        }

        // ------------------------------------------------------------------
        // The safety monitor's code sum
        // ------------------------------------------------------------------

        /// <summary>
        /// The sum the safety monitor will compute over this pair: a 64-bit
        /// sum of the 32-bit words in <see cref="RomTestRanges"/>. Throws
        /// when the image lists other ranges than the ones this was derived
        /// from.
        /// </summary>
        internal static ulong RomTestSum(byte[] flash, byte[] mpc)
        {
            ulong sum = RomTestSeed;
            for (int i = 0; i < RomTestRanges.Length; i += 2)
            {
                uint start = RomTestRanges[i], end = RomTestRanges[i + 1];
                if (Read32(flash, RomTestRangesOffset + 4 * i) != start ||
                    Read32(flash, RomTestRangesOffset + 4 * i + 4) != end)
                    throw new InvalidOperationException(
                        "The program header does not list the expected ranges for the safety monitor's code sum.");

                // Addresses below the MPC's size are its internal flash;
                // the external flash is seen at 0xFFF00000.
                bool inMpc = end <= MpcLength;
                byte[] image = inMpc ? mpc : flash;
                int offset = inMpc ? (int)start : (int)(start & 0xFFFFF);
                for (uint n = (end - start) / 4; n > 0; n--, offset += 4)
                    sum += Read32(image, offset);
            }
            return sum;
        }

        internal static ulong ReadRomTestSum(byte[] flash)
            => ((ulong)Read32(flash, RomTestSumOffset) << 32) | Read32(flash, RomTestSumOffset + 4);

        /// <summary>
        /// Stores the sum that matches the patched code. The stored value
        /// sits just before the range the program checksum covers, so it
        /// does not disturb that checksum.
        /// </summary>
        private static void CorrectRomTestSum(Result result)
        {
            ulong sum = RomTestSum(result.Flash, result.Mpc);
            if (ReadRomTestSum(result.Flash) == sum)
                return;

            Write32(result.Flash, RomTestSumOffset, (uint)(sum >> 32));
            Write32(result.Flash, RomTestSumOffset + 4, (uint)sum);
            result.Log.Add("Safety monitor: code sum at 0x" + RomTestSumOffset.ToString("X") +
                           " set to " + sum.ToString("X16"));
        }

        // ------------------------------------------------------------------
        // The safety monitor's calibration sum
        // ------------------------------------------------------------------

        /// <summary>
        /// The sum the safety monitor computes over a calibration: the same
        /// 64-bit sum of 32-bit words as the code sum, over cal 0x240-0x7C8.
        /// </summary>
        internal static ulong CalTestSum(byte[] cal)
        {
            ulong sum = RomTestSeed;
            for (int offset = (int)(CalTestRangeStart - CalibrationBase); offset < (int)(CalTestRangeEnd - CalibrationBase); offset += 4)
                sum += Read32(cal, offset);
            return sum;
        }

        internal static ulong ReadCalTestSum(byte[] image, int calibrationOffset = 0)
            => ((ulong)Read32(image, calibrationOffset + CalTestSumOffset) << 32) |
               Read32(image, calibrationOffset + CalTestSumOffset + 4);

        private static void WriteCalTestSum(byte[] image, int calibrationOffset, ulong sum)
        {
            Write32(image, calibrationOffset + CalTestSumOffset, (uint)(sum >> 32));
            Write32(image, calibrationOffset + CalTestSumOffset + 4, (uint)sum);
        }

        /// <summary>
        /// The monitor sums map 1's range through absolute addresses and
        /// reads the value to compare with through r2, i.e. from whichever
        /// map is selected. Map 1's stored value is put right if it is not
        /// (a car would already be resetting on such a tune), and map 2 is
        /// given the same value, whatever its own bytes in that range: with
        /// the full tune selected the monitor still sums map 1.
        /// </summary>
        private static void CorrectCalTestSums(Result result, byte[] map1)
        {
            if (Read32(result.Flash, CalTestRangeOffset) != CalTestRangeStart ||
                Read32(result.Flash, CalTestRangeOffset + 4) != CalTestRangeEnd)
                throw new InvalidOperationException(
                    "The program header does not list the expected range for the safety monitor's calibration sum.");

            ulong sum = CalTestSum(map1);
            if (ReadCalTestSum(map1) != sum)
            {
                WriteCalTestSum(result.Flash, CalibrationStart, sum);
                result.Log.Add("Map 1: safety monitor's calibration sum at 0x" + CalTestSumOffset.ToString("X") +
                               " set to " + sum.ToString("X16"));
            }
            if (ReadCalTestSum(result.Flash, Map2Start) != sum)
            {
                WriteCalTestSum(result.Flash, Map2Start, sum);
                result.Log.Add("Map 2: given map 1's calibration sum, " + sum.ToString("X16") +
                               ", which the safety monitor reads from the selected map");
            }
        }

        /// <summary>
        /// Assembles everything that goes into the MPC free area, in order, and
        /// reports where each piece landed.
        /// </summary>
        private static byte[] BuildCode(Version version,
            out uint[] lookupStubs, out uint tachStub, out uint runStub, out uint[] nvStubs)
        {
            var all = new List<uint>();
            uint Here() => (uint)(MpcFreeStart + 4 * all.Count);

            lookupStubs = new uint[LookupEntries.Length];
            for (int i = 0; i < LookupEntries.Length; i++)
            {
                lookupStubs[i] = Here();
                all.AddRange(LookupStub(Here(), (uint)LookupEntries[i], LookupStockInsns[i]).Words());
            }

            tachStub = Here();
            all.AddRange(TachStub(Here(), version).Words());

            runStub = 0;
            if (version.LampHook)
            {
                runStub = Here();
                all.AddRange(MilStub(Here()).Words());
            }

            nvStubs = new uint[3];
            nvStubs[0] = Here();
            all.AddRange(NvInit(Here(), version).Words());
            nvStubs[1] = Here();
            all.AddRange(NvRestore(Here(), version).Words());
            nvStubs[2] = Here();
            all.AddRange(NvSave(Here()).Words());

            var bytes = new byte[4 * all.Count];
            for (int i = 0; i < all.Count; i++)
                Write32(bytes, 4 * i, all[i]);

            if (MpcFreeStart + bytes.Length > MpcFreeEnd)
                throw new InvalidOperationException("Map switch code does not fit in the MPC free area.");
            return bytes;
        }

        private static int Off(uint ramAddress) => (int)ramAddress - (int)R13;

        private static Asm LookupStub(uint at, uint entry, uint stockInsn)
        {
            var a = new Asm(at);
            a.Emit(Lbz(0, Off(RamFlag), 13));
            a.Emit(AndiDot(0, 0, 1));
            a.Bc(Beq, "run");
            a.Emit(Srwi(0, 3, 17));
            a.Emit(Cmplwi(0, CalibrationRangeTag));
            a.Bc(Bne, "run");
            a.Emit(Addis(3, 3, Map2DeltaHigh));
            a.Label("run");
            a.Emit(stockInsn);
            a.BranchTo(entry + 4);
            return a;
        }

        private static Asm TachStub(uint at, Version version)
        {
            int flag = Off(RamFlag), count = Off(RamHoldCounter);
            int delay = Off(RamStartupDelay);
            bool delayed = version.Startup == StartupIndication.Delayed;
            bool dsc = version.Trigger == Trigger.DscButton;

            // The display counter is a byte, or 16 bits where the
            // indication at ignition-on is too long for one.
            bool wide = version.Startup == StartupIndication.ImmediateLong;
            int disp = Off(wide ? RamWideDisplayCounter : RamDisplayCounter);
            Func<int, int, int, uint> loadDisp = wide ? Lhz : Lbz;
            Func<int, int, int, uint> storeDisp = wide ? Sth : Stb;
            var a = new Asm(at);

            // What a toggle starts with the engine stopped: the tach countdown.
            Action<int> indicate = flagReg =>
            {
                a.Emit(Li(12, DisplayCalls)); a.Emit(storeDisp(12, disp, 13));
            };

            // Only with the engine stopped and the car stationary, unless the
            // trigger is meant to work while driving: switching is one
            // pointer add per lookup (and one register write for the full
            // tune), so at worst one control cycle mixes the two maps.
            if (!version.WhileRunning)
            {
                a.Emit(Lhz(12, VarEngineSpeed, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Bne, "running");
                a.Emit(Lbz(12, VarVehicleSpeed, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Bne, "running");
            }

            if (delayed)
            {
                // The ignition-on indication: count the delay down, and start
                // the display as it reaches zero. It then stays at zero, so
                // this happens once per power-up.
                a.Emit(Lhz(12, delay, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Beq, "gesture");
                a.Emit(Addi(12, 12, -1)); a.Emit(Sth(12, delay, 13));
                a.Emit(Cmpwi(12, 0)); a.Bc(Bne, "gesture");
                a.Emit(Li(12, DisplayCalls)); a.Emit(storeDisp(12, disp, 13));
                a.Label("gesture");
            }

            if (dsc)
                EmitDscGesture(a, flag, count, indicate, version.Watch, version.Scope, version.Presses,
                    version.WhileRunning, version.Lockout);
            else
                EmitPedalGesture(a, flag, count, indicate, version.Scope);

            a.Label("show");
            a.Emit(loadDisp(12, disp, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Beq, "store");
            a.Emit(Addi(12, 12, -1)); a.Emit(storeDisp(12, disp, 13));
            // The tach is only borrowed with the engine stopped: running, the
            // gearbox and DSC read this engine speed too.
            if (version.WhileRunning)
            {
                a.Emit(Lhz(12, VarEngineSpeed, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Bne, "store");
            }
            a.Emit(Lbz(11, flag, 13)); a.Emit(AndiDot(11, 11, 1));
            a.Emit(Li(3, Tach1000)); a.Bc(Beq, "store");
            a.Emit(Li(3, Tach2000));
            a.B("store");

            if (!version.WhileRunning)
            {
                a.Label("running");
                a.Emit(Li(12, 0)); a.Emit(Sth(12, count, 13)); a.Emit(storeDisp(12, disp, 13));
                if (dsc)
                    a.Emit(Stb(12, Off(RamPressCounter), 13));
                // An engine that is turning has made the indication pointless;
                // it is not shown late, after a stall or a stop.
                if (delayed)
                    a.Emit(Sth(12, delay, 13));
            }

            a.Label("store");
            a.Emit(Sth(3, TachVar, 13));
            a.Emit(Blr);
            return a;
        }

        /// <summary>
        /// The DSC button. Its state comes from the DSC module over CAN, one
        /// frame or two after the press, so this counts changes of the state
        /// rather than presses: each press changes it once, and four presses
        /// leave DSC as it was. The count is dropped when the next change
        /// does not come within the window.
        /// </summary>
        /// <summary>
        /// For the full-tune scope: points r2 at the selected map's single
        /// values, from bit 0 of the flag in <paramref name="flagReg"/>.
        /// r2 is set outright, not moved, so it is safe to run any number of
        /// times. Clobbers r0 and cr0.
        /// </summary>
        private static void EmitBaseRegister(Asm a, int flagReg, Scope scope, string label)
        {
            if (scope != Scope.FullTune)
                return;
            a.Emit(Lis(2, R2High)); a.Emit(Ori(2, 2, R2Low));
            a.Emit(AndiDot(0, flagReg, 1)); a.Bc(Beq, label);
            a.Emit(Addis(2, 2, Map2DeltaHigh));
            a.Label(label);
        }

        private static void EmitDscGesture(Asm a, int flag, int count, Action<int> indicate,
            DscWatch watch, Scope scope, int pressesToToggle, bool whileRunning, bool lockout)
        {
            int presses = Off(RamPressCounter);
            int state = watch == DscWatch.Car ? VarDscState : FirstVarDscState;
            int mask = watch == DscWatch.Car ? DscStateMask : FirstDscStateMask;

            a.Emit(Lbz(12, state, 13)); a.Emit(AndiDot(12, 12, mask));
            a.Emit(Lbz(11, flag, 13));
            if (lockout)
            {
                // Just after ignition-on: follow the state, count nothing.
                int locked = Off(RamStartupLockout);
                a.Emit(Lhz(10, locked, 13)); a.Emit(Cmpwi(10, 0)); a.Bc(Beq, "armed");
                a.Emit(Addi(10, 10, -1)); a.Emit(Sth(10, locked, 13));
                a.Emit(AndiDot(11, 11, 0xFFFF & ~mask)); a.Emit(Or(11, 11, 12)); a.Emit(Stb(11, flag, 13));
                a.B("show");
                a.Label("armed");
            }
            a.Emit(AndiDot(10, 11, mask));
            a.Emit(Cmpw(12, 10)); a.Bc(Beq, "steady");

            // The state changed: keep it in the flag (same bit position, clear
            // of bit 0, the map) and count it.
            a.Emit(AndiDot(11, 11, 0xFFFF & ~mask)); a.Emit(Or(11, 11, 12)); a.Emit(Stb(11, flag, 13));
            a.Emit(Lbz(12, presses, 13)); a.Emit(Addi(12, 12, 1)); a.Emit(Stb(12, presses, 13));
            a.Emit(Li(10, DscPressWindowCalls)); a.Emit(Sth(10, count, 13));
            a.Emit(Cmplwi(12, pressesToToggle)); a.Bc(Blt, "show");

            a.Emit(Xori(11, 11, 1)); a.Emit(Stb(11, flag, 13));
            EmitBaseRegister(a, 11, scope, "base");
            a.Emit(Li(12, 0)); a.Emit(Sth(12, count, 13)); a.Emit(Stb(12, presses, 13));
            a.Emit(Li(12, 1)); a.Emit(Stb(12, NvSaveRequest, 13));
            if (whileRunning)
            {
                // Engine stopped: the tach. Running: the check-engine lamp
                // blinks once for map 1, twice for map 2.
                a.Emit(Lhz(12, VarEngineSpeed, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Bne, "blink");
                indicate(11);
                a.B("show");
                a.Label("blink");
                a.Emit(AndiDot(12, 11, 1)); a.Emit(Addi(12, 12, 1)); a.Emit(Stb(12, Off(RamBlinks), 13));
                a.Emit(Li(12, 0)); a.Emit(Stb(12, Off(RamBlinkPhase), 13));
                a.B("show");
            }
            else
            {
                indicate(11);
                a.B("show");
            }

            // No change: let the window run out, and forget the presses when it does.
            a.Label("steady");
            a.Emit(Lhz(12, count, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Beq, "show");
            a.Emit(Addi(12, 12, -1)); a.Emit(Sth(12, count, 13));
            a.Emit(Cmpwi(12, 0)); a.Bc(Bne, "show");
            a.Emit(Stb(12, presses, 13));
        }

        /// <summary>
        /// Hooked into the 0x545 builder after its last store of byte 0. While
        /// blinks remain, the check-engine bit follows the blink phase: on for
        /// the first <see cref="BlinkOnCalls"/> of each <see cref="BlinkPeriodCalls"/>
        /// calls, off for the rest. r0, r10, r12 and cr0 are free at the hook
        /// site (all three are reloaded before their next use). The phase is
        /// counted in r12, never r0: addi with r0 as the source is li, and
        /// pinned the first build's phase at 1, lamp on for good.
        /// </summary>
        private static Asm MilStub(uint at)
        {
            int blinks = Off(RamBlinks), phase = Off(RamBlinkPhase);
            var a = new Asm(at);
            a.Emit(MilStoreInsn);                                      // the displaced store
            a.Emit(Lbz(10, blinks, 13)); a.Emit(Cmpwi(10, 0)); a.Bc(Beq, "done");
            a.Emit(Lbz(12, phase, 13)); a.Emit(Addi(12, 12, 1)); a.Emit(Stb(12, phase, 13));
            a.Emit(Cmplwi(12, BlinkOnCalls)); a.Bc(Bge, "off");
            a.Emit(Lbz(0, MilByte, 13)); a.Emit(Ori(0, 0, MilBit)); a.Emit(Stb(0, MilByte, 13));
            a.B("done");
            a.Label("off");
            a.Emit(Lbz(0, MilByte, 13)); a.Emit(Rlwinm(0, 0, 0, 31, 29)); a.Emit(Stb(0, MilByte, 13)); // clear bit 1
            a.Emit(Cmplwi(12, BlinkPeriodCalls)); a.Bc(Blt, "done");
            a.Emit(Li(12, 0)); a.Emit(Stb(12, phase, 13));
            a.Emit(Addi(10, 10, -1)); a.Emit(Stb(10, blinks, 13));
            a.Label("done");
            a.Emit(Blr);
            return a;
        }

        /// <summary>The earlier gesture: both brake inputs set and the pedal floored, held.</summary>
        private static void EmitPedalGesture(Asm a, int flag, int count, Action<int> indicate, Scope scope)
        {
            a.Emit(Lbz(12, VarBrakeA, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Beq, "idle");
            a.Emit(Lbz(12, VarBrakeB, 13)); a.Emit(Cmpwi(12, 0)); a.Bc(Beq, "idle");
            a.Emit(Lbz(12, VarPedal, 13)); a.Emit(Cmplwi(12, PedalFull)); a.Bc(Blt, "idle");

            a.Emit(Lbz(11, flag, 13));
            a.Emit(AndiDot(0, 11, 2)); a.Bc(Bne, "show");      // already toggled during this hold
            a.Emit(Lhz(12, count, 13)); a.Emit(Addi(12, 12, 1)); a.Emit(Sth(12, count, 13));
            a.Emit(Cmplwi(12, HoldCalls)); a.Bc(Blt, "show");

            a.Emit(Xori(11, 11, 1)); a.Emit(Ori(11, 11, 2)); a.Emit(Stb(11, flag, 13));
            EmitBaseRegister(a, 11, scope, "base");
            a.Emit(Li(12, 0)); a.Emit(Sth(12, count, 13));
            a.Emit(Li(12, 1)); a.Emit(Stb(12, NvSaveRequest, 13));
            indicate(11);
            a.B("show");

            a.Label("idle");
            a.Emit(Li(12, 0)); a.Emit(Sth(12, count, 13));
            a.Emit(Lbz(12, VarPedal, 13)); a.Emit(Cmplwi(12, PedalReleased)); a.Bc(Bge, "show");
            a.Emit(Lbz(11, flag, 13)); a.Emit(AndiDot(11, 11, 1)); a.Emit(Stb(11, flag, 13)); // re-arm
        }

        // Power-up. The display countdown is what makes the tach show the
        // map: the immediate versions start it here, another left it at
        // zero, and the delayed one started the delay that leads to it.

        private static void EmitStartup(Asm a, int zeroRegister, StartupIndication startupIndication)
        {
            switch (startupIndication)
            {
                case StartupIndication.ImmediateLong:
                    a.Emit(Li(zeroRegister, StartupDisplayCalls));
                    a.Emit(Sth(zeroRegister, Off(RamWideDisplayCounter), 13));
                    break;
                case StartupIndication.Immediate:
                    a.Emit(Li(zeroRegister, DisplayCalls));
                    a.Emit(Stb(zeroRegister, Off(RamDisplayCounter), 13));
                    break;
                case StartupIndication.None:
                    a.Emit(Stb(zeroRegister, Off(RamDisplayCounter), 13));
                    break;
                case StartupIndication.Delayed:
                    a.Emit(Stb(zeroRegister, Off(RamDisplayCounter), 13));
                    a.Emit(Li(zeroRegister, StartupDelayCalls));
                    a.Emit(Sth(zeroRegister, Off(RamStartupDelay), 13));
                    break;
            }
        }

        private static Asm NvInit(uint at, Version version)
        {
            var a = new Asm(at);
            a.Emit(Li(12, 0));
            a.Emit(Stb(12, NvVar, 13));
            a.Emit(Stb(12, Off(RamFlag), 13));
            a.Emit(Sth(12, Off(RamHoldCounter), 13));
            if (version.Trigger == Trigger.DscButton)
                a.Emit(Stb(12, Off(RamPressCounter), 13));
            if (version.WhileRunning)
            {
                a.Emit(Stb(12, Off(RamBlinks), 13));
                a.Emit(Stb(12, Off(RamBlinkPhase), 13));
            }
            if (version.Lockout)
            {
                a.Emit(Li(12, StartupLockoutCalls)); a.Emit(Sth(12, Off(RamStartupLockout), 13));
                a.Emit(Li(12, 0));
            }
            EmitBaseRegister(a, 12, version.Scope, "base");   // map 1
            // Generation 0 started the ignition-on indication here too, so a
            // car on map 2 showed map 1 until the restore came a moment
            // later. Now only the restore starts it; without valid stored
            // data there is nothing to show.
            if (version.Gen == 0)
                EmitStartup(a, 12, version.Startup);
            a.Emit(Blr);
            return a;
        }

        private static Asm NvRestore(uint at, Version version)
        {
            var a = new Asm(at);
            a.Emit(Lbz(12, 0, 3));
            a.Emit(Srwi(11, 12, 7)); a.Emit(Stb(11, Off(RamFlag), 13));
            a.Emit(AndiDot(12, 12, 0x7F)); a.Emit(Stb(12, NvVar, 13));
            EmitBaseRegister(a, 11, version.Scope, "base");
            a.Emit(Li(11, 0)); a.Emit(Sth(11, Off(RamHoldCounter), 13));
            if (version.Trigger == Trigger.DscButton)
                a.Emit(Stb(11, Off(RamPressCounter), 13));
            if (version.WhileRunning)
            {
                a.Emit(Stb(11, Off(RamBlinks), 13));
                a.Emit(Stb(11, Off(RamBlinkPhase), 13));
            }
            if (version.Lockout)
            {
                a.Emit(Li(11, StartupLockoutCalls)); a.Emit(Sth(11, Off(RamStartupLockout), 13));
                a.Emit(Li(11, 0));
            }
            EmitStartup(a, 11, version.Startup);
            a.Emit(Blr);
            return a;
        }

        private static Asm NvSave(uint at)
        {
            var a = new Asm(at);
            a.Emit(Lbz(12, NvVar, 13));
            a.Emit(Lbz(11, Off(RamFlag), 13));
            a.Emit(Rlwinm(11, 11, 7, 24, 24));      // bit0 -> bit7
            a.Emit(Or(12, 12, 11));
            a.Emit(Stb(12, 0, 3));
            a.Emit(Blr);
            return a;
        }

        // ------------------------------------------------------------------
        // A minimal PowerPC assembler: just the instructions used above.
        // ------------------------------------------------------------------

        private static uint S16(int v)
        {
            if (v < -0x8000 || v > 0x7FFF)
                throw new InvalidOperationException("Signed 16-bit operand out of range: " + v);
            return (uint)v & 0xFFFF;
        }

        private static uint U16(int v)
        {
            if (v < 0 || v > 0xFFFF)
                throw new InvalidOperationException("Unsigned 16-bit operand out of range: " + v);
            return (uint)v;
        }

        private static uint D(uint op, int rt, int ra, uint imm)
            => (op << 26) | ((uint)rt << 21) | ((uint)ra << 16) | imm;

        internal static uint Lbz(int rt, int d, int ra) => D(34, rt, ra, S16(d));
        internal static uint Lhz(int rt, int d, int ra) => D(40, rt, ra, S16(d));
        internal static uint Stb(int rs, int d, int ra) => D(38, rs, ra, S16(d));
        internal static uint Sth(int rs, int d, int ra) => D(44, rs, ra, S16(d));
        internal static uint Stw(int rs, int d, int ra) => D(36, rs, ra, S16(d));
        internal static uint Li(int rt, int v) => D(14, rt, 0, S16(v));
        internal static uint Addi(int rt, int ra, int v) => D(14, rt, ra, S16(v));
        internal static uint Addis(int rt, int ra, int v) => D(15, rt, ra, S16(v));
        internal static uint Lis(int rt, int v) => Addis(rt, 0, v);
        internal static uint Cmpwi(int ra, int v) => D(11, 0, ra, S16(v));
        internal static uint Cmplwi(int ra, int v) => D(10, 0, ra, U16(v));
        internal static uint Cmpw(int ra, int rb) => (31u << 26) | ((uint)ra << 16) | ((uint)rb << 11);
        internal static uint AndiDot(int ra, int rs, int v) => D(28, rs, ra, U16(v));
        internal static uint Ori(int ra, int rs, int v) => D(24, rs, ra, U16(v));
        internal static uint Xori(int ra, int rs, int v) => D(26, rs, ra, U16(v));
        internal static uint Rlwinm(int ra, int rs, int sh, int mb, int me)
            => (21u << 26) | ((uint)rs << 21) | ((uint)ra << 16) | ((uint)sh << 11) | ((uint)mb << 6) | ((uint)me << 1);
        internal static uint Srwi(int ra, int rs, int n) => Rlwinm(ra, rs, 32 - n, n, 31);
        internal static uint Or(int ra, int rs, int rb)
            => (31u << 26) | ((uint)rs << 21) | ((uint)ra << 16) | ((uint)rb << 11) | (444u << 1);
        internal const uint Blr = 0x4E800020;

        internal static uint Branch(uint from, uint to, bool link)
        {
            long d = (long)to - from;
            if (d < -0x2000000 || d >= 0x2000000 || (d & 3) != 0)
                throw new InvalidOperationException("Branch out of range.");
            return (18u << 26) | ((uint)d & 0x03FFFFFC) | (link ? 1u : 0u);
        }

        // Conditional branch encodings: (BO, BI) on cr0.
        private static readonly (int Bo, int Bi) Beq = (12, 2), Bne = (4, 2), Blt = (12, 0), Bge = (4, 0);

        /// <summary>Two-pass assembler with local labels.</summary>
        private sealed class Asm
        {
            private readonly uint _base;
            private readonly List<object> _items = new List<object>();
            private readonly Dictionary<string, uint> _labels = new Dictionary<string, uint>();

            public Asm(uint at) { _base = at; }

            private uint Here => _base + 4u * (uint)_items.Count;

            public void Label(string name) => _labels[name] = Here;
            public void Emit(uint word) => _items.Add(word);
            public void Bc((int Bo, int Bi) cond, string label) => _items.Add((cond.Bo, cond.Bi, label));
            public void B(string label) => _items.Add(label);
            public void BranchTo(uint address) => _items.Add(new AbsoluteTarget { Address = address });

            private sealed class AbsoluteTarget { public uint Address; }

            public uint[] Words()
            {
                var words = new uint[_items.Count];
                for (int i = 0; i < _items.Count; i++)
                {
                    uint pc = _base + 4u * (uint)i;
                    switch (_items[i])
                    {
                        case uint word:
                            words[i] = word;
                            break;
                        case ValueTuple<int, int, string> bc:
                        {
                            long d = (long)_labels[bc.Item3] - pc;
                            if (d < -0x8000 || d >= 0x8000)
                                throw new InvalidOperationException("Conditional branch out of range.");
                            words[i] = (16u << 26) | ((uint)bc.Item1 << 21) | ((uint)bc.Item2 << 16) |
                                       ((uint)d & 0xFFFC);
                            break;
                        }
                        case string label:
                            words[i] = Branch(pc, _labels[label], false);
                            break;
                        case AbsoluteTarget target:
                            words[i] = Branch(pc, target.Address, false);
                            break;
                    }
                }
                return words;
            }
        }

        private static uint Read32(byte[] data, int offset)
            => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) | data[offset + 3];

        private static void Write32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }
}
