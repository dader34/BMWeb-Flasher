using System;

namespace BmwebFlasher
{
    /// <summary>
    /// Removes automatic upshifts from a GS20 calibration, so the gearbox holds
    /// the selected gear instead of changing up on its own.
    ///
    /// The upshift points live in sixteen tables of seventeen entries, arranged
    /// as four groups of four at 0x0D44, 0x0E64, 0x11C4 and 0x12E4. Each table
    /// is preceded by a two byte count of 17, so a table occupies thirty-six
    /// bytes and the groups are contiguous. Raising every entry to 0x1FE0 puts
    /// the shift point beyond any speed the car reaches, and the gearbox never
    /// gets there.
    ///
    /// Two flag bytes at 0x3098 and 0x309A are cleared as well. They read 1 on a
    /// stock calibration and 0 on every no-upshift one.
    ///
    /// The entry values are rewritten but the counts are not. One circulating
    /// patched file overwrites the counts too, turning a 17 into 8160; the
    /// gearbox appears to tolerate it, but there is no reason to copy it.
    ///
    /// Derived by diffing stock against no-upshift calibrations at both shipped
    /// software levels, 7544721 and 7552700. The table layout is identical in
    /// both, which is why this is written against the structure rather than as
    /// a list of byte edits.
    /// </summary>
    public static class Gs20NoUpshift
    {
        /// <summary>Where each group of four upshift tables begins.</summary>
        private static readonly int[] TableGroups = { 0x0D44, 0x0E64, 0x11C4, 0x12E4 };

        private const int TablesPerGroup = 4;
        private const int EntriesPerTable = 17;
        private const int CountBytes = 2;
        private const int TableBytes = CountBytes + EntriesPerTable * 2;

        /// <summary>
        /// A shift point far beyond anything the car reaches. Entries are stored
        /// low byte first, the same way round as the calibration's checksum.
        /// </summary>
        private const ushort Unreachable = 0x1FE0;

        /// <summary>Bytes that read 1 on a stock calibration and 0 once patched.</summary>
        private static readonly int[] FlagOffsets = { 0x3098, 0x309A };

        /// <summary>
        /// Whether this calibration has the layout the patch expects: the right
        /// size, and a count of seventeen in front of all sixteen tables.
        /// </summary>
        public static bool IsApplicable(byte[] cal)
        {
            if (cal == null || cal.Length != Gs20Checksum.CalLength) return false;

            foreach (int group in TableGroups)
            {
                for (int table = 0; table < TablesPerGroup; table++)
                {
                    int at = group + table * TableBytes;
                    if (at + TableBytes > cal.Length) return false;
                    if (cal[at] != EntriesPerTable || cal[at + 1] != 0) return false;
                }
            }

            foreach (int offset in FlagOffsets)
                if (offset >= cal.Length) return false;

            return true;
        }

        /// <summary>Whether the patch has already been applied.</summary>
        public static bool IsApplied(byte[] cal)
        {
            if (!IsApplicable(cal)) return false;

            foreach (int group in TableGroups)
            {
                for (int table = 0; table < TablesPerGroup; table++)
                {
                    int at = group + table * TableBytes + CountBytes;
                    for (int entry = 0; entry < EntriesPerTable; entry++)
                    {
                        int i = at + entry * 2;
                        ushort value = (ushort)(cal[i] | (cal[i + 1] << 8));
                        if (value != Unreachable) return false;
                    }
                }
            }

            foreach (int offset in FlagOffsets)
                if (cal[offset] != 0) return false;

            return true;
        }

        /// <summary>
        /// Returns a copy with automatic upshifts removed. The caller's array is
        /// left alone. The checksum is not corrected here; the writer does that
        /// on the way to the car, so an edited calibration cannot reach it with
        /// a stale one either way.
        /// </summary>
        public static byte[] Apply(byte[] cal)
        {
            if (cal == null) throw new ArgumentNullException(nameof(cal));
            if (!IsApplicable(cal))
                throw new ArgumentException(
                    "This calibration does not have the upshift tables the patch expects.",
                    nameof(cal));

            byte[] patched = (byte[])cal.Clone();

            foreach (int group in TableGroups)
            {
                for (int table = 0; table < TablesPerGroup; table++)
                {
                    // Step over the count: it says how many entries follow and
                    // is not one of them.
                    int at = group + table * TableBytes + CountBytes;
                    for (int entry = 0; entry < EntriesPerTable; entry++)
                    {
                        int i = at + entry * 2;
                        patched[i] = (byte)(Unreachable & 0xFF);
                        patched[i + 1] = (byte)(Unreachable >> 8);
                    }
                }
            }

            foreach (int offset in FlagOffsets)
                patched[offset] = 0;

            return patched;
        }
    }
}
