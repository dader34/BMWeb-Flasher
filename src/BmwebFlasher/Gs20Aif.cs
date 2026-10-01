using System;
using System.Globalization;
using System.Linq;

namespace BmwebFlasher
{
    /// <summary>
    /// The transmission's programming-log entry (AIF), as BMW's GD20
    /// programming SGBD packs it: 46 bytes, decoded from AIF_SCHREIBEN by
    /// running the job against a fake interface and reading what it sent.
    ///
    ///   0x00  VIN, 17 chars at 6 bits each (0-9 = 0..9, A-Z = 10..35),
    ///         MSB first after two padding bits: 13 bytes, then 00
    ///   0x0E  date, (day &lt;&lt; 11) | (month &lt;&lt; 7) | (year % 100), two bytes BE, then 00
    ///   0x11  software number, three bytes BE
    ///   0x14  change index, two chars packed (c1 &lt;&lt; 6) | c2 as the SGBD does
    ///         (letters 10..35, digits kept as their ASCII code), two bytes BE, then 00
    ///   0x17  approval number, three bytes BE, then 00
    ///   0x1B  assembly (ZB) number, three bytes BE, then 00
    ///   0x1F  tester serial, five ASCII chars, then 00
    ///   0x25  dealer number, two bytes BE, then 00
    ///   0x28  odometer in 600 km steps, one byte, then 00
    ///   0x2A  program reference, three bytes, then 00
    /// </summary>
    public static class Gs20Aif
    {
        public const int RecordLength = 0x2E;
        public const int Slots = 14;

        public static byte[] Build(string vin, DateTime date, int softwareNr, string changeIndex,
                                   int approvalNr, int assemblyNr, string testerSerial, int dealerNr,
                                   long km, byte[] programRef)
        {
            if (vin == null || vin.Length != 17 || !vin.All(char.IsLetterOrDigit))
                throw new ArgumentException("The VIN must be 17 letters or digits.", nameof(vin));
            if (changeIndex == null || changeIndex.Length != 2)
                throw new ArgumentException("The change index is two characters.", nameof(changeIndex));
            if (testerSerial == null || testerSerial.Length != 5)
                throw new ArgumentException("The tester serial is five characters.", nameof(testerSerial));
            if (programRef == null || programRef.Length != 3)
                throw new ArgumentException("The program reference is three bytes.", nameof(programRef));

            var r = new byte[RecordLength];

            // VIN: a 104-bit stream, two zero bits then 17 six-bit values.
            ulong acc = 0; int bits = 2; int o = 0;
            foreach (char c in vin.ToUpperInvariant())
            {
                acc = (acc << 6) | (ulong)Alnum(c); bits += 6;
                while (bits >= 8) { bits -= 8; r[o++] = (byte)(acc >> bits); }
            }
            // 2 + 102 = 104 bits exactly, so nothing is left in acc.

            int packedDate = (date.Day << 11) | (date.Month << 7) | (date.Year % 100);
            r[0x0E] = (byte)(packedDate >> 8); r[0x0F] = (byte)packedDate;

            Put24(r, 0x11, softwareNr);
            int idx = (IndexChar(changeIndex[0]) << 6) | IndexChar(changeIndex[1]);
            r[0x14] = (byte)(idx >> 8); r[0x15] = (byte)idx;

            Put24(r, 0x17, approvalNr);
            Put24(r, 0x1B, assemblyNr);
            for (int i = 0; i < 5; i++) r[0x1F + i] = (byte)testerSerial[i];
            r[0x25] = (byte)(dealerNr >> 8); r[0x26] = (byte)dealerNr;
            long steps = km / 600;
            r[0x28] = (byte)Math.Min(255, Math.Max(0, steps));
            Array.Copy(programRef, 0, r, 0x2A, 3);
            return r;
        }

        /// <summary>
        /// The BMW part numbers behind a calibration's reference suffix (the
        /// "ER10" of G2210_0090C0ER10): the data number of the .0DA and the
        /// assembly (ZUSB) it is delivered under, which is what BMW's tools
        /// put in the entry. The data number is always one above the
        /// assembly for these releases. Null when the suffix is not known.
        /// </summary>
        public static (int DataNr, int AssemblyNr)? PartNumbers(string calibrationReference)
        {
            if (string.IsNullOrEmpty(calibrationReference) || calibrationReference.Length < 4) return null;
            switch (calibrationReference.Substring(calibrationReference.Length - 4).ToUpperInvariant())
            {
                case "ER10": return (7558009, 7558008);   // E46 325i USA auto, A7558009.0DA
                case "ES10": return (7557995, 7557994);   // E46 330i ZHP USA, A7557995.0DA
                case "DP10": return (7557985, 7557984);   // E46/16 330i USA, A7557985.0DA
                default: return null;
            }
        }

        /// <summary>A slot is free while its first byte is still erased.</summary>
        public static bool IsFree(byte[] slot) => slot != null && slot.Length > 0 && slot[0] == 0xFF;

        private static int Alnum(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            throw new ArgumentException("Not a VIN character: " + c);
        }

        // What the SGBD does with the change index: letters become 10..35,
        // digits stay as their ASCII code.
        private static int IndexChar(char c)
        {
            c = char.ToUpperInvariant(c);
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            return c;
        }

        private static void Put24(byte[] r, int at, int value)
        {
            r[at] = (byte)(value >> 16); r[at + 1] = (byte)(value >> 8); r[at + 2] = (byte)value;
        }

        /// <summary>Parses a decimal field, 0 when it is not one.</summary>
        public static int Number(string s)
        {
            return int.TryParse((s ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0
                ? v : 0;
        }
    }
}
