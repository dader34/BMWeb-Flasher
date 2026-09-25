using System;

namespace BmwebFlasher
{
    /// <summary>
    /// The MS45.1 external flash's low region, file 0x00000-0x3FFFF (CPU
    /// 0x2000000-0x203FFFF), and the CRC it is protected by.
    ///
    /// Neither of the tool's two original write paths touches this region: the
    /// tune path writes the calibration (0x40000-0x5CFFF) and the program path
    /// writes the high program plus the internal MPC (0x60000-0xFFF3F, 0x0-0x6FFFF
    /// internal). So a patch whose bytes land below 0x40000 - the EWS-delete code
    /// patch at 0x10A88 is the one that forced this - could be computed into the
    /// image but never written to the DME. This class supplies the piece the
    /// caller was missing: the byte range to erase/write and the checksum to fix.
    ///
    /// Flash geometry (from the DME's own erase-block table in the MPC image):
    /// the external flash is uniform 0x20000-byte sectors from CPU 0x2000000, so
    /// the low region is exactly the first two sectors and erases as one block of
    /// 0x40000 at base 0x2000000 - the same flash_loeschen the adjacent regions
    /// already use, no different erase command and no separate unlock.
    ///
    /// Integrity: the low region is NOT covered by the program checksum/signature
    /// (those start at file 0x60000). It carries its own self-describing CRC-32
    /// descriptor at 0x1F800 - a stored CRC, a segment count, then CPU
    /// start/end pairs - over file 0x00000-0x1EFFF, which contains 0x10A88. That
    /// descriptor is the only integrity structure this class must correct; the
    /// caller does the erase, the write and the read-back.
    /// </summary>
    public static class MS45LowRegion
    {
        /// <summary>Full external flash image size.</summary>
        public const int FullFlashLength = 0x100000;

        /// <summary>First file offset of the low region.</summary>
        public const int RegionStart = 0x00000;

        /// <summary>Last file offset of the low region (inclusive).</summary>
        public const int RegionEnd = 0x3FFFF;

        /// <summary>Length of the low region, 0x40000 bytes (two 0x20000 sectors).</summary>
        public const int RegionLength = RegionEnd - RegionStart + 1;

        /// <summary>CPU base address the low region is mapped at, for erase/write.</summary>
        public const uint CpuBase = 0x2000000;

        /// <summary>CPU erase base: the start of the first low-region sector.</summary>
        public const uint EraseCpuStart = 0x2000000;

        /// <summary>Erase block length: the two low-region sectors together.</summary>
        public const uint EraseBlockLength = 0x40000;

        /// <summary>CPU address of the first byte written (== EraseCpuStart).</summary>
        public const uint FlashCpuStart = 0x2000000;

        /// <summary>CPU address of the last byte written (inclusive).</summary>
        public const uint FlashCpuEnd = 0x203FFFF;

        /// <summary>File offset of the low-region CRC-32 descriptor.</summary>
        public const int ChecksumDescriptorOffset = 0x1F800;

        // The descriptor stores CPU addresses; the low region is mapped at
        // 0xFFF00000 for the purpose of that table (CPU 0xFFF00000 == file 0x0).
        private const uint DescriptorMemBase = 0xFFF00000;

        // A sane low-region descriptor never declares a segment count beyond a
        // handful, and every range must fall inside the region it is meant to
        // cover. These bound the parse so a corrupt or unexpected image is
        // rejected rather than driving a wild read.
        private const uint MaxSegments = 8;

        /// <summary>The 0x40000-byte slice of the low region, to hand to the writer.</summary>
        public static byte[] Slice(byte[] flash)
        {
            RequireFullImage(flash);
            byte[] slice = new byte[RegionLength];
            Buffer.BlockCopy(flash, RegionStart, slice, 0, RegionLength);
            return slice;
        }

        /// <summary>
        /// True when the low region of <paramref name="candidate"/> differs from
        /// the low region of <paramref name="onModule"/> - i.e. there is actually
        /// something to write. Used to decide whether the (brick-capable)
        /// low-region flash is needed at all. A null <paramref name="onModule"/>
        /// (nothing read back to compare against) counts as "differs".
        /// </summary>
        public static bool DiffersFrom(byte[] candidate, byte[] onModule)
        {
            RequireFullImage(candidate);
            if (onModule == null)
                return true;
            if (onModule.Length < RegionEnd + 1)
                return true;
            for (int i = RegionStart; i <= RegionEnd; i++)
                if (candidate[i] != onModule[i])
                    return true;
            return false;
        }

        /// <summary>
        /// Returns a copy of the image with the low-region CRC-32 descriptor at
        /// 0x1F800 recomputed over its declared range. Any edit inside
        /// 0x00000-0x1EFFF (the EWS code patch at 0x10A88, for example) must be in
        /// place before this is called. The caller's array is not modified.
        /// </summary>
        public static byte[] CorrectLowChecksum(byte[] flash)
        {
            RequireFullImage(flash);

            byte[] result = (byte[])flash.Clone();
            var (fileStart, fileEnd) = DescriptorRange(result);

            uint crc = 0xFFFFFFFF;
            // The descriptor's count is 1 on this build; loop anyway so a
            // multi-segment descriptor is handled if a future build uses one.
            // DescriptorRange has already validated every segment, so a single
            // contiguous CRC over [fileStart, fileEnd] reproduces it.
            byte[] region = new byte[fileEnd - fileStart + 1];
            Buffer.BlockCopy(result, fileStart, region, 0, region.Length);
            crc = Checksums_Signatures.Crc32Shared(region, crc);

            // Stored big-endian, like every other checksum this app writes.
            result[ChecksumDescriptorOffset + 0] = (byte)(crc >> 24);
            result[ChecksumDescriptorOffset + 1] = (byte)(crc >> 16);
            result[ChecksumDescriptorOffset + 2] = (byte)(crc >> 8);
            result[ChecksumDescriptorOffset + 3] = (byte)crc;
            return result;
        }

        /// <summary>The CRC-32 currently stored in the descriptor, big-endian.</summary>
        public static uint StoredLowChecksum(byte[] flash)
        {
            RequireFullImage(flash);
            int o = ChecksumDescriptorOffset;
            return (uint)((flash[o] << 24) | (flash[o + 1] << 16) | (flash[o + 2] << 8) | flash[o + 3]);
        }

        /// <summary>
        /// The CRC-32 that <em>should</em> be stored for the current bytes, without
        /// modifying the image. Equal to StoredLowChecksum only when the region and
        /// its checksum are already consistent.
        /// </summary>
        public static uint ComputeLowChecksum(byte[] flash)
        {
            RequireFullImage(flash);
            var (fileStart, fileEnd) = DescriptorRange(flash);
            byte[] region = new byte[fileEnd - fileStart + 1];
            Buffer.BlockCopy(flash, fileStart, region, 0, region.Length);
            return Checksums_Signatures.Crc32Shared(region, 0xFFFFFFFF);
        }

        // Parse the descriptor at 0x1F800: [storedCRC:4][count:4][ (cpuStart:4,
        // cpuEnd:4) * count ]. Returns the covered range as file offsets. Throws
        // if the descriptor is not a single sane range inside the low region -
        // rather than trust an unexpected layout and checksum the wrong bytes.
        private static (int fileStart, int fileEnd) DescriptorRange(byte[] flash)
        {
            int o = ChecksumDescriptorOffset;
            uint count = ReadBe32(flash, o + 4);
            if (count == 0 || count > MaxSegments)
                throw new InvalidOperationException(
                    "MS45 low-region checksum descriptor at 0x1F800 declares an implausible segment count (" +
                    count + "); refusing to guess its coverage.");

            int lo = int.MaxValue, hi = int.MinValue;
            for (uint i = 0; i < count; i++)
            {
                uint cpuStart = ReadBe32(flash, o + 8 + (int)(i * 8));
                uint cpuEnd = ReadBe32(flash, o + 12 + (int)(i * 8));
                if (cpuStart < DescriptorMemBase || cpuEnd < cpuStart)
                    throw new InvalidOperationException(
                        "MS45 low-region checksum descriptor has an out-of-range or reversed segment; refusing to patch.");

                long fileStart = cpuStart - DescriptorMemBase;
                long fileEnd = cpuEnd - DescriptorMemBase;
                if (fileEnd > RegionEnd || fileStart > fileEnd)
                    throw new InvalidOperationException(
                        "MS45 low-region checksum descriptor covers bytes outside the low region; refusing to patch.");

                if (fileStart < lo) lo = (int)fileStart;
                if (fileEnd > hi) hi = (int)fileEnd;
            }
            return (lo, hi);
        }

        private static uint ReadBe32(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        private static void RequireFullImage(byte[] flash)
        {
            if (flash == null)
                throw new ArgumentNullException(nameof(flash));
            if (flash.Length != FullFlashLength)
                throw new InvalidOperationException(
                    "MS45 low-region work needs a full 1 MB external flash image (got 0x" +
                    flash.Length.ToString("X") + " bytes).");
        }
    }
}
