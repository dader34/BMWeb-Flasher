using System;
using System.Collections.Generic;

namespace BmwebFlasher
{
    /// <summary>
    /// EWS (immobilizer) delete for the MS45.1 external flash, program 0044570LO02S.
    ///
    /// The delete is four functional bytes, split across two flash partitions:
    ///
    ///   calibration (Daten, 0x40000-0x5CFFF):
    ///     0x48F2C : 0x10 -> 0x00     fmy_id_imob_2.c_err_clas (immobilizer error class)
    ///     0x48F3E : 0x10 -> 0x00     fmy_id_imob_3.c_err_clas (immobilizer error class)
    ///
    ///   program (0x60000-0xFFF3F):
    ///     0xDB1C7 : 0x01 -> 0x00     immobilizer engine-enable state byte
    ///     0xDB1D3 : 0x3F -> 0x00     immobilizer engine-enable cylinder mask (0x3F = 6 cyl)
    ///
    /// Both partitions must be written for the car to start. Verified 2026-09-27
    /// by diffing two same-session full reads from the same car, same program
    /// build: a flash that left the program bytes at their stock 0x01/0x3F
    /// cranked-no-start and logged EWS fault P1665 ("Manipulation ueber
    /// Wechselcode"); a flash of the same car with the program bytes at 0x00/0x00
    /// started and ran. The two calibration flags were 0x00/0x00 in both reads,
    /// so they are necessary but not sufficient on their own - the program bytes
    /// are the part a program-only edit must not miss.
    ///
    /// NOT part of the delete (verified, do not touch here):
    ///   - 0x10A88 (low region): a service-0x22 read-permission lock, unrelated to
    ///     EWS, and unwritable over the diagnostic path anyway (the bootloader
    ///     rejects a low-region erase with NRC 0x22). An earlier version of this
    ///     file wrongly listed it as an EWS edit; it is removed.
    ///   - the MPC internal flash is not touched by the delete.
    ///
    /// To REMOVE an EWS delete, flash a stock .0PA/.0DA (immobilizer active by
    /// default) - there is no separate restore path here.
    ///
    /// Gated on the exact program version: other MS45.1 programs lay calibration
    /// and code out differently, and every offset must read its expected stock
    /// value before the delete is applied, so it cannot land on a mismatched image.
    /// </summary>
    public static class EwsDelete
    {
        /// <summary>Expected size of a full external flash image.</summary>
        public const int FullFlashLength = 0x100000;

        /// <summary>First file offset of the calibration (Daten) partition.</summary>
        public const int CalibrationStart = 0x40000;

        /// <summary>First file offset of the program partition.</summary>
        public const int ProgramStart = 0x60000;

        /// <summary>
        /// The program version string, at 0x6031C in the external image.
        /// The offsets were derived against this version only.
        /// </summary>
        public const string SupportedProgramVersion = "0044570LO02S";

        private const int ProgramVersionOffset = 0x6031C;

        /// <summary>
        /// The delete edits: (offset, expected stock bytes, deleted bytes).
        /// Two calibration flags and two program bytes.
        /// </summary>
        private static readonly (int Offset, byte[] Stock, byte[] Deleted)[] Edits =
        {
            (0x48F2C, new byte[] { 0x10 }, new byte[] { 0x00 }), // cal imob_2 error class
            (0x48F3E, new byte[] { 0x10 }, new byte[] { 0x00 }), // cal imob_3 error class
            (0xDB1C7, new byte[] { 0x01 }, new byte[] { 0x00 }), // prog engine-enable state
            (0xDB1D3, new byte[] { 0x3F }, new byte[] { 0x00 }), // prog engine-enable mask
        };

        /// <summary>
        /// Reads the program version string from a full external image, or null
        /// if the image is too small or the field is not printable ASCII.
        /// </summary>
        public static string ReadProgramVersion(byte[] flash)
        {
            if (flash == null || flash.Length < ProgramVersionOffset + 12)
                return null;

            var chars = new char[12];
            for (int i = 0; i < 12; i++)
            {
                byte b = flash[ProgramVersionOffset + i];
                if (b < 0x20 || b > 0x7E)
                    return null;
                chars[i] = (char)b;
            }
            return new string(chars);
        }

        /// <summary>
        /// The state of one half of the delete. The program half is the two
        /// engine-enable bytes, the calibration half the two error-class
        /// flags; within a half the two bytes always change together.
        /// </summary>
        private enum Half { Stock, Deleted, Unknown }

        private static Half StateOf(byte[] flash, bool program)
        {
            bool stock = true, deleted = true;
            foreach (var (offset, stockBytes, deletedBytes) in Edits)
            {
                if ((offset >= ProgramStart) != program) continue;
                stock &= MatchesAt(flash, offset, stockBytes);
                deleted &= MatchesAt(flash, offset, deletedBytes);
            }
            return stock ? Half.Stock : deleted ? Half.Deleted : Half.Unknown;
        }

        /// <summary>
        /// True when this is an MS45.1 image the delete can be applied to: right
        /// size, right program version, each half either stock or already
        /// deleted, and at least one half still stock.
        ///
        /// The halves are judged separately because they are flashed and tuned
        /// separately: a stock program is often paired with a tune whose flags
        /// were cleared earlier, and the delete then has only the program left
        /// to do. A half holding anything else is refused, since the image is
        /// then not what these offsets were derived from.
        /// </summary>
        public static bool IsApplicable(byte[] flash)
        {
            if (flash == null || flash.Length != FullFlashLength)
                return false;

            if (ReadProgramVersion(flash) != SupportedProgramVersion)
                return false;

            Half program = StateOf(flash, program: true);
            Half calibration = StateOf(flash, program: false);
            if (program == Half.Unknown || calibration == Half.Unknown)
                return false;

            return program == Half.Stock || calibration == Half.Stock;
        }

        /// <summary>True when every edit already holds its deleted value.</summary>
        public static bool IsAlreadyPatched(byte[] flash)
        {
            if (flash == null || flash.Length != FullFlashLength)
                return false;

            if (ReadProgramVersion(flash) != SupportedProgramVersion)
                return false;

            foreach (var (offset, _, deleted) in Edits)
                if (!MatchesAt(flash, offset, deleted))
                    return false;

            return true;
        }

        private static bool MatchesAt(byte[] flash, int offset, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (flash[offset + i] != expected[i])
                    return false;
            return true;
        }

        /// <summary>
        /// Returns a copy with the EWS delete applied across both partitions; a
        /// half that is already deleted is left as it is. The caller's array is
        /// left alone. Checksums and RSA signatures for both the
        /// program and the calibration are recomputed by the writer, not here - the
        /// caller must sign/checksum both partitions after this.
        /// </summary>
        public static byte[] Apply(byte[] flash)
        {
            if (flash == null)
                throw new ArgumentNullException(nameof(flash));

            if (flash.Length != FullFlashLength)
            {
                throw new InvalidOperationException(
                    "EWS delete needs a full 1 MB external flash image (got 0x" +
                    flash.Length.ToString("X") + " bytes).");
            }

            if (IsAlreadyPatched(flash))
                return (byte[])flash.Clone();

            if (!IsApplicable(flash))
            {
                string version = ReadProgramVersion(flash);
                if (version != SupportedProgramVersion)
                {
                    throw new InvalidOperationException(
                        "EWS delete is only verified for program version " + SupportedProgramVersion +
                        ", but this image reports " + (version ?? "an unreadable version") + ".");
                }

                throw new InvalidOperationException(
                    "This image is the right program version but its EWS bytes are not at their " +
                    "expected stock values, so it may already be modified. Refusing to patch.");
            }

            byte[] patched = (byte[])flash.Clone();
            foreach (var (offset, _, deleted) in Edits)
                Buffer.BlockCopy(deleted, 0, patched, offset, deleted.Length);
            return patched;
        }

        /// <summary>
        /// The program bytes that a live ECU read can be checked against to tell
        /// whether the program currently on the module is EWS-deleted. Exposed so a
        /// tune-only flash can detect a program/calibration EWS mismatch (a deleted
        /// program with a stock-immobilizer tune re-enables EWS and no-starts).
        /// </summary>
        public const int ProgramStateOffset = 0xDB1C7;
        public const int ProgramMaskOffset = 0xDB1D3;

        /// <summary>
        /// True when the two program engine-enable bytes read back as their
        /// deleted values (both 0x00). The caller supplies the bytes it read from
        /// the ECU at <see cref="ProgramStateOffset"/> and <see cref="ProgramMaskOffset"/>.
        /// </summary>
        public static bool ProgramBytesAreDeleted(byte stateByte, byte maskByte)
            => stateByte == 0x00 && maskByte == 0x00;

        /// <summary>File offset of the immobilizer error-class flags in the cal.</summary>
        public const int CalFlag2Offset = 0x48F2C;
        public const int CalFlag3Offset = 0x48F3E;

        /// <summary>
        /// True when a calibration slice (a 0x1D000-byte Daten partition starting
        /// at file 0x40000, or a full image) still carries the STOCK immobilizer
        /// flags (0x10) - i.e. flashing it would re-enable EWS.
        /// <paramref name="calFileOffset"/> is where the 0x40000 partition sits in
        /// the given buffer (0 for a bare 0x1D000 slice, 0x40000 for a full image).
        /// </summary>
        public static bool CalibrationHasStockImmobilizer(byte[] cal, int calFileOffset)
        {
            int f2 = CalFlag2Offset - CalibrationStart + calFileOffset;
            int f3 = CalFlag3Offset - CalibrationStart + calFileOffset;
            if (cal == null || f3 >= cal.Length) return false;
            return cal[f2] == 0x10 && cal[f3] == 0x10;
        }

        /// <summary>
        /// True when the cal already carries the deleted immobilizer flags (0x00).
        /// </summary>
        public static bool CalibrationHasDeletedImmobilizer(byte[] cal, int calFileOffset)
        {
            int f2 = CalFlag2Offset - CalibrationStart + calFileOffset;
            int f3 = CalFlag3Offset - CalibrationStart + calFileOffset;
            if (cal == null || f3 >= cal.Length) return false;
            return cal[f2] == 0x00 && cal[f3] == 0x00;
        }

        /// <summary>
        /// Clears the two immobilizer flags in a calibration buffer (the cal half
        /// of the EWS delete), leaving the caller's array alone. Use when the
        /// program on the ECU is already EWS-deleted so the tune matches it.
        /// <paramref name="calFileOffset"/> as in CalibrationHasStockImmobilizer.
        /// Checksums/signature must be recomputed by the caller afterward.
        /// </summary>
        public static byte[] ApplyCalibrationDelete(byte[] cal, int calFileOffset)
        {
            if (cal == null)
                throw new ArgumentNullException(nameof(cal));
            int f2 = CalFlag2Offset - CalibrationStart + calFileOffset;
            int f3 = CalFlag3Offset - CalibrationStart + calFileOffset;
            if (f3 >= cal.Length)
                throw new InvalidOperationException("Calibration buffer too small for the EWS flag offsets.");
            byte[] copy = (byte[])cal.Clone();
            copy[f2] = 0x00;
            copy[f3] = 0x00;
            return copy;
        }

        /// <summary>Human-readable list of the edits, for logging before a flash.</summary>
        public static IEnumerable<string> Describe()
        {
            foreach (var (offset, stock, deleted) in Edits)
            {
                string part = offset >= ProgramStart ? "prog" : "cal";
                yield return string.Format("{0} 0x{1:X5}: {2} -> {3}", part, offset,
                    BitConverter.ToString(stock), BitConverter.ToString(deleted));
            }
        }
    }
}
