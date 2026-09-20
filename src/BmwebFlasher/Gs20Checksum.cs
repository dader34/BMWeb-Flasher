using System;

namespace BmwebFlasher
{
    /// <summary>
    /// The GS20 (A5S390R) calibration checksum.
    ///
    /// The transmission verifies this at power-up. A calibration whose stored
    /// value does not match its data is rejected: the box falls back to a limp
    /// program (fixed gear, no adaptation) and stores a calibration fault. The
    /// boot block and program are untouched by a calibration write, so the TCU
    /// still identifies and can be re-flashed -- a wrong checksum is a reflash,
    /// not a dead module. Nothing here ever reaches the car without
    /// <see cref="Verify"/> agreeing first.
    ///
    /// Algorithm (recovered from the car, 2026-09-19): CRC-16 in its reflected
    /// (LSB-first) form over polynomial 0xA001, initial value zero, across
    /// [0x2A, 0xFFC8), stored byte-swapped at 0x0D:0x0E. The range endpoints are
    /// the 42 and 65480 that appear verbatim in the flash tool's own segment
    /// table; the reflected direction is what that table is fed with.
    ///
    /// Validated against eight calibrations spanning both shipped software
    /// levels: the 7552700 ("90") reads taken off this car, its untouched
    /// factory calibration, and four 7544721 ("89") files including a raw car
    /// read -- the 89 set never informed the derivation.
    /// </summary>
    public static class Gs20Checksum
    {
        /// <summary>Calibration image size this checksum is defined over.</summary>
        public const int CalLength = 0x10000;

        /// <summary>High byte of the stored checksum.</summary>
        public const int StoredOffset = 0x0D;

        private const int RangeStart = 0x2A;
        private const int RangeEnd = 0xFFC8;   // exclusive
        private const ushort Polynomial = 0xA001;
        private const ushort InitialValue = 0x0000;

        private static readonly ushort[] Table = BuildTable();

        private static ushort[] BuildTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < table.Length; i++)
            {
                ushort value = (ushort)i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (ushort)((value & 1) != 0
                        ? (value >> 1) ^ Polynomial
                        : value >> 1);
                }
                table[i] = value;
            }
            return table;
        }

        /// <summary>
        /// The value that belongs at 0x0D:0x0E for this calibration, in the
        /// order it is stored (high byte first).
        /// </summary>
        public static ushort Compute(byte[] cal)
        {
            if (cal == null) throw new ArgumentNullException(nameof(cal));
            if (cal.Length < RangeEnd)
                throw new ArgumentException(
                    "A GS20 calibration is 0x" + CalLength.ToString("X") +
                    " bytes; this one is 0x" + cal.Length.ToString("X") + ".",
                    nameof(cal));

            ushort crc = InitialValue;
            for (int i = RangeStart; i < RangeEnd; i++)
                crc = (ushort)((crc >> 8) ^ Table[(crc ^ cal[i]) & 0xFF]);

            // Stored byte-swapped relative to the running value.
            return (ushort)(((crc & 0xFF) << 8) | (crc >> 8));
        }

        /// <summary>The checksum currently stored in the image.</summary>
        public static ushort Stored(byte[] cal)
        {
            if (cal == null) throw new ArgumentNullException(nameof(cal));
            if (cal.Length <= StoredOffset + 1)
                throw new ArgumentException("Calibration is too short to hold a checksum.", nameof(cal));

            return (ushort)((cal[StoredOffset] << 8) | cal[StoredOffset + 1]);
        }

        /// <summary>True when the stored checksum matches the data.</summary>
        public static bool Verify(byte[] cal) => Stored(cal) == Compute(cal);

        /// <summary>
        /// The calibration's own version string, which it repeats three times in
        /// the block past the checksummed range, for example "G2210_0090C0ER10".
        /// Returns null when the image carries nothing recognisable.
        /// </summary>
        public static string ReadVersion(byte[] cal)
        {
            if (cal == null || cal.Length < CalLength) return null;

            // The version block sits after the checksummed range.
            var text = new System.Text.StringBuilder();
            for (int i = RangeEnd; i < CalLength; i++)
            {
                byte b = cal[i];
                bool printable = b >= 0x20 && b < 0x7F;
                if (printable) text.Append((char)b);
                else if (text.Length > 0) break;
            }

            string version = text.ToString().Trim();
            return version.Length >= 8 ? version.Substring(0, Math.Min(16, version.Length)) : null;
        }

        /// <summary>
        /// The release digits a calibration names for itself: the four digits
        /// after the underscore of a string like "G2210_0090C0ER10", which is
        /// the same number a transmission reports as its software reference.
        /// The "C0" that follows is a variant marker the module never reports,
        /// so it plays no part. Returns null when nothing can be read.
        /// </summary>
        public static string ReadRelease(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            int underscore = version.IndexOf('_');
            if (underscore < 0 || underscore + 5 > version.Length) return null;

            string release = version.Substring(underscore + 1, 4);
            foreach (char c in release)
                if (c < '0' || c > '9') return null;

            string trimmed = release.TrimStart('0');
            return trimmed.Length > 0 ? trimmed : "0";
        }

        /// <summary>The release of the calibration in this image.</summary>
        public static string ReadRelease(byte[] cal) => ReadRelease(ReadVersion(cal));

        /// <summary>
        /// Returns a copy with the checksum corrected. The input is left alone
        /// so a caller can still show what changed.
        /// </summary>
        public static byte[] Correct(byte[] cal)
        {
            if (cal == null) throw new ArgumentNullException(nameof(cal));

            byte[] corrected = (byte[])cal.Clone();
            ushort value = Compute(corrected);
            corrected[StoredOffset] = (byte)(value >> 8);
            corrected[StoredOffset + 1] = (byte)(value & 0xFF);
            return corrected;
        }
    }
}
