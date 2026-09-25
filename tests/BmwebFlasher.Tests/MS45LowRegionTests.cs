using System;
using System.IO;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The low-region write path is exercised on the DME hardware, which no unit
    /// test can stand in for; what is tested here is the pure logic that path
    /// depends on - the CRC-32 recompute at 0x1F800 and the slice/diff helpers -
    /// so a wrong checksum or a mis-sized region is caught before it ever reaches
    /// a module.
    ///
    /// The strongest test uses the real ground truth: an EWS-deleted image
    /// produced by a known-good tool starts the car, and its low-region CRC is a
    /// fixed value (0x671E33F8) computed over the region that contains the
    /// 0x10A88 code patch. Applying the patch to a stock image and recomputing
    /// must reproduce exactly that value. Those images are BMW-derived and not in
    /// the repo; point MS45_STOCK_BIN / MS45_EWS_BIN at local copies to run the
    /// fixture tests. The synthetic tests need no fixtures.
    /// </summary>
    public class MS45LowRegionTests
    {
        private static string StockPath => Environment.GetEnvironmentVariable("MS45_STOCK_BIN");
        private static string PatchedPath => Environment.GetEnvironmentVariable("MS45_EWS_BIN");

        private static bool HaveStock =>
            !string.IsNullOrEmpty(StockPath) && File.Exists(StockPath);
        private static bool HavePatched =>
            !string.IsNullOrEmpty(PatchedPath) && File.Exists(PatchedPath);

        // Independent reference CRC-32 (poly 0x04C11DB7, MSB-first, no final xor),
        // written out longhand so the test does not lean on the same table the
        // app uses. A mistake in the app's shared table would make these diverge.
        private static uint ReferenceCrc32(byte[] data, uint crc)
        {
            foreach (byte b in data)
            {
                crc ^= (uint)b << 24;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
            return crc;
        }

        // A minimal 1 MB image with a single-segment low-region descriptor at
        // 0x1F800 covering CPU 0xFFF00000..0xFFF1EFFF (file 0x0..0x1EFFF), some
        // arbitrary content in the region, and a deliberately wrong stored CRC.
        private static byte[] SyntheticImage(byte fill = 0x5A)
        {
            var img = new byte[MS45LowRegion.FullFlashLength];
            for (int i = 0; i <= 0x1EFFF; i++)
                img[i] = (byte)((i * 31 + fill) & 0xFF);

            int o = MS45LowRegion.ChecksumDescriptorOffset;
            // stored CRC (wrong on purpose)
            img[o + 0] = 0xDE; img[o + 1] = 0xAD; img[o + 2] = 0xBE; img[o + 3] = 0xEF;
            // segment count = 1
            img[o + 4] = 0; img[o + 5] = 0; img[o + 6] = 0; img[o + 7] = 1;
            // segment start 0xFFF00000
            img[o + 8] = 0xFF; img[o + 9] = 0xF0; img[o + 10] = 0x00; img[o + 11] = 0x00;
            // segment end 0xFFF1EFFF
            img[o + 12] = 0xFF; img[o + 13] = 0xF1; img[o + 14] = 0xEF; img[o + 15] = 0xFF;
            return img;
        }

        [Fact]
        public void CorrectLowChecksum_matches_an_independent_crc32()
        {
            byte[] img = SyntheticImage();

            byte[] region = new byte[0x1F000];
            Buffer.BlockCopy(img, 0, region, 0, region.Length);
            uint expected = ReferenceCrc32(region, 0xFFFFFFFF);

            byte[] fixedImg = MS45LowRegion.CorrectLowChecksum(img);
            Assert.Equal(expected, MS45LowRegion.StoredLowChecksum(fixedImg));
            Assert.Equal(expected, MS45LowRegion.ComputeLowChecksum(img));
        }

        [Fact]
        public void CorrectLowChecksum_does_not_touch_the_callers_array()
        {
            byte[] img = SyntheticImage();
            byte[] before = (byte[])img.Clone();
            MS45LowRegion.CorrectLowChecksum(img);
            Assert.Equal(before, img);
        }

        [Fact]
        public void CorrectLowChecksum_is_idempotent()
        {
            byte[] once = MS45LowRegion.CorrectLowChecksum(SyntheticImage());
            byte[] twice = MS45LowRegion.CorrectLowChecksum(once);
            Assert.Equal(MS45LowRegion.StoredLowChecksum(once),
                         MS45LowRegion.StoredLowChecksum(twice));
        }

        [Fact]
        public void A_byte_change_in_the_region_changes_the_checksum()
        {
            byte[] a = MS45LowRegion.CorrectLowChecksum(SyntheticImage());
            byte[] edited = SyntheticImage();
            edited[0x10A88] ^= 0xFF; // where the EWS code patch lands
            byte[] b = MS45LowRegion.CorrectLowChecksum(edited);
            Assert.NotEqual(MS45LowRegion.StoredLowChecksum(a),
                            MS45LowRegion.StoredLowChecksum(b));
        }

        [Fact]
        public void Slice_is_the_first_0x40000_bytes()
        {
            byte[] img = SyntheticImage();
            img[0] = 0x11; img[0x3FFFF] = 0x22;
            byte[] slice = MS45LowRegion.Slice(img);
            Assert.Equal(0x40000, slice.Length);
            Assert.Equal(0x11, slice[0]);
            Assert.Equal(0x22, slice[0x3FFFF]);
        }

        [Fact]
        public void DiffersFrom_is_false_when_low_regions_match_and_true_otherwise()
        {
            byte[] a = SyntheticImage();
            byte[] same = (byte[])a.Clone();
            Assert.False(MS45LowRegion.DiffersFrom(a, same));

            byte[] diff = (byte[])a.Clone();
            diff[0x10A88] ^= 0xFF;
            Assert.True(MS45LowRegion.DiffersFrom(a, diff));

            // A change above the low region does not count.
            byte[] highOnly = (byte[])a.Clone();
            highOnly[0x80000] ^= 0xFF;
            Assert.False(MS45LowRegion.DiffersFrom(a, highOnly));

            // Nothing to compare against => differs (write, then verify).
            Assert.True(MS45LowRegion.DiffersFrom(a, null));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(0x1D000)]   // a partial (cal-only) read
        [InlineData(0x200000)]  // double size
        public void Rejects_images_that_are_not_a_full_1MB(int length)
        {
            byte[] wrong = new byte[length];
            Assert.Throws<InvalidOperationException>(() => MS45LowRegion.CorrectLowChecksum(wrong));
            Assert.Throws<InvalidOperationException>(() => MS45LowRegion.Slice(wrong));
        }

        [Fact]
        public void Rejects_a_descriptor_that_points_outside_the_low_region()
        {
            byte[] img = SyntheticImage();
            int o = MS45LowRegion.ChecksumDescriptorOffset;
            // segment end 0xFFF80000 -> file 0x80000, past the low region
            img[o + 12] = 0xFF; img[o + 13] = 0xF8; img[o + 14] = 0x00; img[o + 15] = 0x00;
            Assert.Throws<InvalidOperationException>(() => MS45LowRegion.CorrectLowChecksum(img));
        }

        [Fact]
        public void Rejects_a_descriptor_with_an_implausible_segment_count()
        {
            byte[] img = SyntheticImage();
            int o = MS45LowRegion.ChecksumDescriptorOffset;
            img[o + 4] = 0xFF; img[o + 5] = 0xFF; img[o + 6] = 0xFF; img[o + 7] = 0xFF;
            Assert.Throws<InvalidOperationException>(() => MS45LowRegion.CorrectLowChecksum(img));
        }

        // ---- Ground-truth fixture tests --------------------------------------

        [SkippableFact]
        public void Stock_image_low_region_is_already_consistent()
        {
            Skip.IfNot(HaveStock, "Set MS45_STOCK_BIN to run this.");
            byte[] stock = File.ReadAllBytes(StockPath);
            Skip.IfNot(stock.Length == MS45LowRegion.FullFlashLength, "MS45_STOCK_BIN is not a full 1 MB image.");

            // A stock image's stored low-region CRC already matches its bytes.
            Assert.Equal(MS45LowRegion.StoredLowChecksum(stock),
                         MS45LowRegion.ComputeLowChecksum(stock));
        }

        [SkippableFact]
        public void Applying_the_EWS_code_patch_and_recomputing_reproduces_the_known_good_crc()
        {
            Skip.IfNot(HaveStock, "Set MS45_STOCK_BIN to run this.");
            byte[] stock = File.ReadAllBytes(StockPath);
            Skip.IfNot(stock.Length == MS45LowRegion.FullFlashLength, "MS45_STOCK_BIN is not a full 1 MB image.");
            Skip.IfNot(EwsDelete.IsApplicable(stock), "MS45_STOCK_BIN is not an applicable stock image.");

            byte[] patched = EwsDelete.Apply(stock);          // includes the 0x10A88 edit
            byte[] corrected = MS45LowRegion.CorrectLowChecksum(patched);

            // The known-good deleted image (confirmed to start the car) carries
            // this exact low-region CRC over the patched region.
            const uint KnownGoodLowCrc = 0x671E33F8;
            Assert.Equal(KnownGoodLowCrc, MS45LowRegion.StoredLowChecksum(corrected));

            if (HavePatched)
            {
                byte[] known = File.ReadAllBytes(PatchedPath);
                if (known.Length == MS45LowRegion.FullFlashLength)
                    Assert.Equal(MS45LowRegion.StoredLowChecksum(known),
                                 MS45LowRegion.StoredLowChecksum(corrected));
            }
        }

        [SkippableFact]
        public void Known_good_image_low_region_is_self_consistent()
        {
            Skip.IfNot(HavePatched, "Set MS45_EWS_BIN to run this.");
            byte[] known = File.ReadAllBytes(PatchedPath);
            Skip.IfNot(known.Length == MS45LowRegion.FullFlashLength, "MS45_EWS_BIN is not a full 1 MB image.");

            Assert.Equal(MS45LowRegion.StoredLowChecksum(known),
                         MS45LowRegion.ComputeLowChecksum(known));
            Assert.Equal(0x671E33F8u, MS45LowRegion.StoredLowChecksum(known));
        }
    }
}
