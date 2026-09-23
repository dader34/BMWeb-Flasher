using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BmwebFlasher
{
    /// <summary>
    /// Decodes a BMW <c>.0DA</c> calibration file (the "Austausch-Datei" that
    /// ships in SP-Daten under EC-APPS/NFS/DATA/GD20) into the raw 64 KB image
    /// the module actually holds.
    ///
    /// The format is Intel HEX with two wrinkles that matter:
    ///
    ///  * The file addresses the calibration at its real location, 0x090000,
    ///    reached through a type-4 linear base plus a type-2 segment base. Both
    ///    appear, and the segment base is an offset within the linear one
    ///    rather than a replacement for it.
    ///
    ///  * The final 32-byte block uses **record type 0x10**, which is not in
    ///    the Intel HEX standard. Skipping it silently loses the version-string
    ///    tail and the four-byte trailer the module validates against, and the
    ///    calibration's own CRC covers only [0x2A,0xFFC8) so the damage does
    ///    not show up as a checksum failure. A calibration written without
    ///    those bytes is accepted telegram by telegram and then refused at the
    ///    commit, which leaves the transmission declining further writes until
    ///    it is power-cycled. Hence the explicit handling and the tail check.
    ///
    /// Verified against two calibrations read back off real modules
    /// (G2210_0090C0DP10 and G2210_0090C0ES10): the decode is byte-for-byte
    /// identical to what the transmission reports.
    /// </summary>
    public static class Gs20DatenFile
    {
        /// <summary>Where the calibration lives in the module's address space.</summary>
        public const int CalAddress = 0x090000;

        /// <summary>
        /// The last four bytes of every GS20 calibration. Constant across all
        /// 219 calibrations in SP-Daten and in every module read taken so far,
        /// spanning three program families, petrol and diesel, 1999-2006.
        /// </summary>
        public static readonly byte[] Trailer = { 0xC7, 0xA3, 0x8C, 0x44 };

        /// <summary>Whether a path looks like a Daten file rather than a raw image.</summary>
        public static bool IsDatenFile(string path) =>
            path != null &&
            Path.GetExtension(path).Equals(".0DA", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The <c>ZL_Referenz</c> the file declares in its header, e.g.
        /// "G2210_0090C0ES10", or null when it carries none.
        /// </summary>
        public static string ReadReference(string path)
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith(":", StringComparison.Ordinal)) break;
                Match m = Regex.Match(line, @"ZL_Referenz:\s*(\S+)");
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// The vehicle the file is for, from the <c>K_F1</c> header line, e.g.
        /// "E46 M54B30 USA SPORT MY06". Null when absent.
        /// </summary>
        public static string ReadVehicle(string path)
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith(":", StringComparison.Ordinal)) break;
                Match m = Regex.Match(line, @";;K_F1:\s*(.+?)\s*$");
                if (m.Success)
                    return m.Groups[1].Value
                            .Replace("Daten fuer ", string.Empty)
                            .Replace("Daten  fuer ", string.Empty)
                            .Trim();
            }
            return null;
        }

        /// <summary>
        /// Decodes the file to a 64 KB calibration image.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// When a record is malformed, a record's own checksum fails, or the
        /// result does not cover the whole calibration. Refusing here is the
        /// point: a partially decoded calibration looks valid.
        /// </exception>
        public static byte[] Decode(string path) =>
            Decode(path, CalAddress, Gs20Checksum.CalLength);

        /// <summary>Where the program lives in the module's address space.</summary>
        public const int ProgramAddress = 0x0A0000;

        /// <summary>Whether a path looks like a program Daten file (.0PA).</summary>
        public static bool IsProgramFile(string path) =>
            path != null &&
            Path.GetExtension(path).Equals(".0PA", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Decodes a <c>.0PA</c> program file to the 256 KB program region.
        /// Same format as the calibration file, addressed at 0x0A0000; each
        /// 64 KB page ends in a type-0x10 record, which is why earlier extracts
        /// of these files came out 32 bytes short per page.
        /// </summary>
        public static byte[] DecodeProgram(string path) =>
            Decode(path, ProgramAddress, Gs20ProgramChecksum.ProgramLength);

        private static byte[] Decode(string path, int region, int size)
        {
            var image = new byte[size];
            var covered = new bool[size];
            int linearBase = 0, segmentBase = 0, lineNumber = 0;

            foreach (string raw in File.ReadLines(path))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || !line.StartsWith(":", StringComparison.Ordinal))
                    continue;

                byte[] record = ParseRecord(line, lineNumber);
                int length = record[0];
                int address = (record[1] << 8) | record[2];
                int type = record[3];

                switch (type)
                {
                    case 0x04:                     // extended linear address
                        linearBase = ((record[4] << 8) | record[5]) << 16;
                        segmentBase = 0;
                        break;

                    case 0x02:                     // extended segment address
                        segmentBase = ((record[4] << 8) | record[5]) << 4;
                        break;

                    case 0x00:                     // data
                    case 0x10:                     // data, BMW's own record type
                        int start = linearBase + segmentBase + address;
                        for (int i = 0; i < length; i++)
                        {
                            int offset = start + i - region;
                            if (offset < 0 || offset >= image.Length) continue;
                            image[offset] = record[4 + i];
                            covered[offset] = true;
                        }
                        break;

                    case 0x01:                     // end of file
                        break;

                    default:
                        // Unknown record types are the failure this class
                        // exists to prevent, so they are loud rather than
                        // skipped.
                        throw new InvalidDataException(
                            "Line " + lineNumber + " uses record type 0x" +
                            type.ToString("X2") + ", which this reader does not " +
                            "understand. Decoding it as if it were absent would " +
                            "produce a calibration the transmission refuses.");
                }
            }

            int missing = 0;
            int firstMissing = -1;
            for (int i = 0; i < covered.Length; i++)
            {
                if (covered[i]) continue;
                if (firstMissing < 0) firstMissing = i;
                missing++;
            }
            if (missing > 0)
                throw new InvalidDataException(
                    "The file does not cover the whole calibration: " + missing +
                    " of " + image.Length + " bytes are missing, starting at 0x" +
                    firstMissing.ToString("X4") + ".");

            return image;
        }

        /// <summary>
        /// Whether an image ends with the trailer every GS20 calibration
        /// carries. A calibration without it is written successfully and then
        /// refused at the commit.
        /// </summary>
        public static bool HasTrailer(ReadOnlySpan<byte> calibration)
        {
            if (calibration.Length < Trailer.Length) return false;
            return calibration.Slice(calibration.Length - Trailer.Length)
                              .SequenceEqual(Trailer);
        }

        private static byte[] ParseRecord(string line, int lineNumber)
        {
            string hex = line.Substring(1);
            if (hex.Length < 10 || hex.Length % 2 != 0)
                throw new InvalidDataException(
                    "Line " + lineNumber + " is not a well-formed record.");

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out bytes[i]))
                    throw new InvalidDataException(
                        "Line " + lineNumber + " contains a non-hexadecimal byte.");
            }

            if (bytes[0] + 5 != bytes.Length)
                throw new InvalidDataException(
                    "Line " + lineNumber + " declares " + bytes[0] +
                    " data bytes but carries " + Math.Max(bytes.Length - 5, 0) + ".");

            int sum = 0;
            for (int i = 0; i < bytes.Length - 1; i++) sum += bytes[i];
            byte expected = (byte)(-sum & 0xFF);
            if (bytes[bytes.Length - 1] != expected)
                throw new InvalidDataException(
                    "Line " + lineNumber + " has a bad record checksum (0x" +
                    bytes[bytes.Length - 1].ToString("X2") + ", expected 0x" +
                    expected.ToString("X2") + ").");

            return bytes;
        }
    }
}
