using System;

namespace BmwebFlasher
{
    /// <summary>
    /// The GS20 program-region checksum.
    ///
    /// CRC-16/ARC (reflected, polynomial 0xA001, initial value 0) over two
    /// ranges of the program region, stored little-endian at program 0x3FECC.
    /// The ranges are not arbitrary: they are the first two entries of the
    /// module's own descriptor table at program 0x3FE00, which is why the gap
    /// at 0x200-0x27F is skipped.
    ///
    /// The stored word sits 205 bytes past the end of the checksummed data, so
    /// the checksum is not self-referential and a single pass converges.
    ///
    /// Verified against four images including a factory pre-patch read
    /// (0xC72D) and a patched one (0x4CB5), and against single-bit corruption,
    /// which it detects inside the ranges and correctly ignores outside them.
    /// </summary>
    public static class Gs20ProgramChecksum
    {
        /// <summary>Where the program region starts in the module.</summary>
        public const int ProgramAddress = 0x0A0000;

        /// <summary>How long the program region is.</summary>
        public const int ProgramLength = 0x40000;

        /// <summary>Offset of the stored checksum within the program region.</summary>
        public const int StoreOffset = 0x3FECC;

        // Program-relative, inclusive. Taken from the descriptor table.
        private static readonly (int Start, int End)[] Ranges =
        {
            (0x00000, 0x001FF),
            (0x00280, 0x3FDFF),
        };

        /// <summary>CRC-16/ARC over a span, continuing from a running value.</summary>
        private static ushort Crc16(ReadOnlySpan<byte> data, ushort crc)
        {
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }

        /// <summary>The checksum the given program region should carry.</summary>
        public static ushort Compute(ReadOnlySpan<byte> program)
        {
            Require(program);
            ushort crc = 0;
            foreach ((int start, int end) in Ranges)
                crc = Crc16(program.Slice(start, end - start + 1), crc);
            return crc;
        }

        /// <summary>The checksum currently stored in the program region.</summary>
        public static ushort Stored(ReadOnlySpan<byte> program)
        {
            Require(program);
            return (ushort)(program[StoreOffset] | (program[StoreOffset + 1] << 8));
        }

        /// <summary>Whether the stored checksum matches the content.</summary>
        public static bool Verify(ReadOnlySpan<byte> program) =>
            Stored(program) == Compute(program);

        /// <summary>
        /// Writes the correct checksum into a copy and returns it, leaving the
        /// caller's array untouched so a rejected write does not leave a
        /// half-corrected image behind.
        /// </summary>
        public static byte[] Corrected(ReadOnlySpan<byte> program, out ushort checksum)
        {
            Require(program);
            var copy = program.ToArray();
            checksum = Compute(copy);
            copy[StoreOffset] = (byte)checksum;
            copy[StoreOffset + 1] = (byte)(checksum >> 8);
            return copy;
        }

        private static void Require(ReadOnlySpan<byte> program)
        {
            if (program.Length != ProgramLength)
                throw new ArgumentException(
                    "A GS20 program region is 0x" + ProgramLength.ToString("X") +
                    " bytes; this one is 0x" + program.Length.ToString("X") + ".",
                    nameof(program));
        }
    }
}
