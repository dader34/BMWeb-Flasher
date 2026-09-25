using System;
using System.Collections.Generic;

namespace BmwebFlasher
{
    /// <summary>
    /// EWS (immobilizer) delete for the MS45.1 external flash.
    ///
    /// The delete is three edits in the external image:
    ///
    ///   0x48F2C : 0x10 (EWS active) -> 0x00 (EWS off)         calibration flag
    ///   0x48F3E : 0x10 (EWS active) -> 0x00 (EWS off)         calibration flag
    ///   0x10A88 : 88 8D 82 48 -> 38 80 00 22                  code patch
    ///
    /// The two flag bytes disable the EWS-active calibration flags. The code
    /// patch at 0x10A88 replaces `lbz r4,-0x7DB8(r13)` (read the EWS status
    /// byte from RAM) with `li r4,0x22` (load a fixed non-zero constant), so the
    /// status check right after it always takes the pass path. All three are
    /// needed: with only the flags cleared the flash completes but the car still
    /// cranks-no-start, because the dynamic check at 0x10A88 still runs.
    ///
    /// Derived 2026-09-25 by diffing a known-good EWS-deleted image (produced by
    /// BTT MS45 Quickflash and confirmed to start the car) against the donor's
    /// stock external image: after the RSA-signature and checksum blocks are
    /// discounted (the writer recomputes those), the only functional change is
    /// these two bytes. The MPC internal flash is NOT touched by the delete.
    ///
    /// This is MS45.1 program 0044570LO02S only. Both offsets are absolute
    /// positions in the 0x100000 external flash image, and both must read the
    /// expected stock value 0x10 before the delete is applied, so it cannot land
    /// on an image whose layout differs.
    /// </summary>
    public static class EwsDelete
    {
        /// <summary>Expected size of a full external flash image.</summary>
        public const int FullFlashLength = 0x100000;

        /// <summary>
        /// The program version string, at 0x6031C in the external image.
        /// The flag offsets were derived against this version only. Other
        /// MS45.1 programs lay their calibration out differently, so the version
        /// - not just the 0044570 hardware reference - is what gates this.
        /// </summary>
        public const string SupportedProgramVersion = "0044570LO02S";

        private const int ProgramVersionOffset = 0x6031C;

        /// <summary>
        /// The delete edits: (offset, expected stock bytes, replacement bytes).
        /// Two single-byte calibration flags and one 4-byte code patch.
        /// </summary>
        private static readonly (int Offset, byte[] Stock, byte[] Deleted)[] Edits =
        {
            (0x48F2C, new byte[] { 0x10 }, new byte[] { 0x00 }),
            (0x48F3E, new byte[] { 0x10 }, new byte[] { 0x00 }),
            (0x10A88, new byte[] { 0x88, 0x8D, 0x82, 0x48 }, new byte[] { 0x38, 0x80, 0x00, 0x22 }),
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
        /// True when this looks like an unpatched MS45.1 external image the
        /// delete can be applied to: right size, right program version, and both
        /// flag bytes still at their stock 0x10.
        /// </summary>
        public static bool IsApplicable(byte[] flash)
        {
            if (flash == null || flash.Length != FullFlashLength)
                return false;

            if (ReadProgramVersion(flash) != SupportedProgramVersion)
                return false;

            foreach (var (offset, stock, _) in Edits)
                if (!MatchesAt(flash, offset, stock))
                    return false;

            return true;
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
        /// Returns a copy with the EWS delete applied. The caller's array is left
        /// alone. Checksums and the RSA signature are corrected by the writer on
        /// the way to the car, not here.
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
                    "This image is the right program version but the EWS flag bytes are not at " +
                    "their expected stock value, so it may already be modified. Refusing to patch.");
            }

            byte[] patched = (byte[])flash.Clone();
            foreach (var (offset, _, deleted) in Edits)
                Buffer.BlockCopy(deleted, 0, patched, offset, deleted.Length);
            return patched;
        }

        /// <summary>Human-readable list of the edits, for logging before a flash.</summary>
        public static IEnumerable<string> Describe()
        {
            foreach (var (offset, stock, deleted) in Edits)
                yield return string.Format("0x{0:X5}: {1} -> {2}", offset,
                    BitConverter.ToString(stock), BitConverter.ToString(deleted));
        }
    }
}
