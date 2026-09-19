using System;
using System.Collections.Generic;

namespace MS45_Flasher
{
    /// <summary>
    /// EWS (immobilizer) delete patch for the MS45.1 external flash.
    ///
    /// The DME keeps three EWS status bytes in its global data block, addressed
    /// off r13 as r13-0x3C30, -0x3C2F and -0x3C2E. Several routines set those
    /// bytes to 1 to mark "EWS says no". The patch makes every one of those
    /// sites leave the bytes at 0, so the engine-disable path is never armed.
    ///
    /// Two shapes appear in the binary:
    ///
    ///   li  rX,1          <- the immediate byte is flipped 1 -> 0
    ///   stb rX,-0x3C30(r13)
    ///
    ///   stb rX,-0x3C30(r13)   <- the whole store is replaced with a nop,
    ///                            because rX is reused by the surrounding code
    ///                            and its value cannot simply be changed.
    ///
    /// Derived by diffing a stock Original_external.bin against a known-good
    /// patched image; applying these eight edits to stock reproduces that image
    /// byte for byte (see EwsDeleteTests). Every remaining store to these three
    /// bytes elsewhere in the image already writes 0.
    ///
    /// This is MS45.1 (HW ref 0044570) only. The offsets are absolute positions
    /// in the 0x100000 external flash image.
    /// </summary>
    public static class EwsDelete
    {
        /// <summary>Expected size of a full external flash image.</summary>
        public const int FullFlashLength = 0x100000;

        /// <summary>
        /// The program version string, at 0x6031C in the external image.
        /// The patch offsets were derived against this version only.
        ///
        /// The other MS45.1 program in SP-Daten, 7549388A.0PA (0044570LN00S,
        /// 2004), reports the same 0044570 hardware reference but lays its
        /// globals out differently: it has no store to r13-0x3C30 at all and
        /// only one to -0x3C2F, at a completely different address. The patch
        /// does not transfer to it, which is why the version — not just the
        /// hardware reference — is what gates this.
        /// </summary>
        public const string SupportedProgramVersion = "0044570LO02S";

        private const int ProgramVersionOffset = 0x6031C;

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
        /// Sites where a "li rX,1" immediate becomes 0. The offset points at the
        /// low byte of the instruction's immediate field.
        /// </summary>
        private static readonly int[] ImmediateSites =
        {
            0x770A3, 0x773C3, 0x773EB, 0x77417, 0x775FB
        };

        /// <summary>
        /// Sites where a whole 4-byte store instruction becomes a nop.
        /// </summary>
        private static readonly int[] NopSites =
        {
            0xD297C, 0xD2C54, 0xD2C58
        };

        /// <summary>PowerPC nop: ori r0,r0,0.</summary>
        private static readonly byte[] Nop = { 0x60, 0x00, 0x00, 0x00 };

        /// <summary>
        /// True when every patch site still holds its expected stock value, i.e.
        /// this looks like an unpatched MS45.1 external image.
        /// </summary>
        public static bool IsApplicable(byte[] flash)
        {
            if (flash == null || flash.Length != FullFlashLength)
                return false;

            if (ReadProgramVersion(flash) != SupportedProgramVersion)
                return false;

            foreach (int offset in ImmediateSites)
            {
                if (flash[offset] != 0x01)
                    return false;
            }

            foreach (int offset in NopSites)
            {
                // The stock instruction is a stb against r13; the opcode byte
                // (0x99 or 0x9B) plus the r13 operand identify it.
                if (flash[offset] != 0x99 && flash[offset] != 0x9B)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True when every site already carries the patched value.
        /// </summary>
        public static bool IsAlreadyPatched(byte[] flash)
        {
            if (flash == null || flash.Length != FullFlashLength)
                return false;

            foreach (int offset in ImmediateSites)
            {
                if (flash[offset] != 0x00)
                    return false;
            }

            foreach (int offset in NopSites)
            {
                for (int i = 0; i < Nop.Length; i++)
                {
                    if (flash[offset + i] != Nop[i])
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns a patched copy of <paramref name="flash"/>. The input is not
        /// modified. Throws if the image does not match what the patch expects,
        /// rather than writing to offsets whose contents are unknown.
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
                        ", but this image reports " + (version ?? "an unreadable version") + ". " +
                        "Other MS45.1 programs lay their globals out differently, so the patch " +
                        "offsets would land on unrelated code.");
                }

                throw new InvalidOperationException(
                    "This image is the right program version but does not carry the expected " +
                    "instructions at the patch sites, so it may already be modified. " +
                    "Refusing to patch, because writing these offsets blind could brick the DME.");
            }

            byte[] patched = (byte[])flash.Clone();

            foreach (int offset in ImmediateSites)
                patched[offset] = 0x00;

            foreach (int offset in NopSites)
                Buffer.BlockCopy(Nop, 0, patched, offset, Nop.Length);

            return patched;
        }

        /// <summary>
        /// Human-readable list of the edits, for logging before a flash.
        /// </summary>
        public static IEnumerable<string> Describe()
        {
            foreach (int offset in ImmediateSites)
                yield return string.Format("0x{0:X5}: li immediate 1 -> 0", offset);
            foreach (int offset in NopSites)
                yield return string.Format("0x{0:X5}: stb -> nop", offset);
        }
    }
}
