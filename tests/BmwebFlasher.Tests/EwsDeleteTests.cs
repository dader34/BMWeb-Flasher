using System;
using System.IO;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The ground truth for the EWS patch is a pair of real images: a stock
    /// external flash and a known-good patched one. Applying EwsDelete.Apply to
    /// the former must reproduce the latter exactly.
    ///
    /// Those images are BMW-derived and are not in the repo. Point
    /// MS45_STOCK_BIN and MS45_EWS_BIN at local copies to run the comparison;
    /// without them the fixture tests skip and only the synthetic ones run.
    /// </summary>
    public class EwsDeleteTests
    {
        private static string StockPath => Environment.GetEnvironmentVariable("MS45_STOCK_BIN");
        private static string PatchedPath => Environment.GetEnvironmentVariable("MS45_EWS_BIN");

        private static bool HaveFixtures =>
            !string.IsNullOrEmpty(StockPath) && File.Exists(StockPath) &&
            !string.IsNullOrEmpty(PatchedPath) && File.Exists(PatchedPath);

        [SkippableFact]
        public void KnownGoodImageIsRecognisedAsAlreadyDeleted()
        {
            // MS45_EWS_BIN is a fully-written known-good deleted image (e.g. from
            // confirmed to start the car). Its flag bytes must read 0
            // and the class must recognise it as already patched. (A byte-for-byte
            // reproduce test is not used because a signed known-good image also
            // differs in the writer-owned signature/checksum blocks, and it must
            // share the exact same program build as MS45_STOCK_BIN to compare,
            // which is not guaranteed for an arbitrary donor read.)
            Skip.IfNot(!string.IsNullOrEmpty(PatchedPath) && File.Exists(PatchedPath),
                "Set MS45_EWS_BIN to run this.");

            byte[] known = File.ReadAllBytes(PatchedPath);
            Assert.Equal("0044570LO02S", EwsDelete.ReadProgramVersion(known));
            Assert.Equal(0x00, known[0x48F2C]);
            Assert.Equal(0x00, known[0x48F3E]);
            Assert.Equal(0x00, known[0xDB1C7]); // program engine-enable state cleared
            Assert.Equal(0x00, known[0xDB1D3]); // program engine-enable mask cleared
            Assert.True(EwsDelete.IsAlreadyPatched(known));
            Assert.False(EwsDelete.IsApplicable(known));
        }

        [SkippableFact]
        public void ApplyChangesExactlyTheDeleteEdits()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN and MS45_EWS_BIN to run this.");

            byte[] stock = File.ReadAllBytes(StockPath);
            byte[] actual = EwsDelete.Apply(stock);

            int changed = 0;
            for (int i = 0; i < stock.Length; i++)
                if (stock[i] != actual[i]) changed++;

            // Two calibration flags + two program bytes = 4 bytes.
            Assert.Equal(4, changed);
            Assert.Equal(0x00, actual[0x48F2C]);
            Assert.Equal(0x00, actual[0x48F3E]);
            Assert.Equal(0x10, stock[0x48F2C]);
            Assert.Equal(0x10, stock[0x48F3E]);
            // program engine-enable bytes cleared (01/3F -> 00/00)
            Assert.Equal(0x00, actual[0xDB1C7]);
            Assert.Equal(0x00, actual[0xDB1D3]);
            Assert.Equal(0x01, stock[0xDB1C7]);
            Assert.Equal(0x3F, stock[0xDB1D3]);
        }

        [SkippableFact]
        public void ApplyDoesNotMutateItsInput()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN and MS45_EWS_BIN to run this.");

            byte[] stock = File.ReadAllBytes(StockPath);
            byte[] before = (byte[])stock.Clone();

            EwsDelete.Apply(stock);

            Assert.True(before.AsSpan().SequenceEqual(stock), "Apply modified the caller's buffer.");
        }

        [SkippableFact]
        public void PatchedImageIsRecognisedAndIsIdempotent()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN and MS45_EWS_BIN to run this.");

            byte[] patched = File.ReadAllBytes(PatchedPath);

            Assert.True(EwsDelete.IsAlreadyPatched(patched));
            Assert.False(EwsDelete.IsApplicable(patched));

            // Re-applying must be a no-op rather than an error or a double patch.
            byte[] again = EwsDelete.Apply(patched);
            Assert.True(patched.AsSpan().SequenceEqual(again));
        }

        [SkippableFact]
        public void StockImageIsRecognisedAsApplicable()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN to run this.");

            byte[] stock = File.ReadAllBytes(StockPath);

            Assert.True(EwsDelete.IsApplicable(stock));
            Assert.False(EwsDelete.IsAlreadyPatched(stock));
        }

        // --- Synthetic tests, no fixtures needed ----------------------------

        [SkippableFact]
        public void StockImageReportsTheSupportedProgramVersion()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN to run this.");

            byte[] stock = File.ReadAllBytes(StockPath);
            Assert.Equal(EwsDelete.SupportedProgramVersion, EwsDelete.ReadProgramVersion(stock));
        }

        [SkippableFact]
        public void RefusesAnImageWhoseProgramVersionDiffers()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN to run this.");

            // 7549388A.0PA (0044570LN00S) has the same hardware reference but a
            // different global layout, so version — not HW ref — has to gate the
            // patch. Simulate it by rewriting just the version field.
            byte[] other = File.ReadAllBytes(StockPath);
            byte[] label = System.Text.Encoding.ASCII.GetBytes("0044570LN00S");
            Buffer.BlockCopy(label, 0, other, 0x6031C, label.Length);

            Assert.False(EwsDelete.IsApplicable(other));
            var ex = Assert.Throws<InvalidOperationException>(() => EwsDelete.Apply(other));
            Assert.Contains("0044570LN00S", ex.Message);
        }

        [SkippableFact]
        public void ReadProgramVersionIgnoresTheDataReference()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN to run this.");

            byte[] stock = File.ReadAllBytes(StockPath);

            // The parameter block at 0x40010 carries a DIFFERENT reference
            // (LO00S) from the program at 0x6031C (LO02S). DATEN_REFERENZ over
            // the wire reports the former, which is why the gate must read the
            // program field and not trust the DME's reported SW reference.
            string program = EwsDelete.ReadProgramVersion(stock);
            string data = System.Text.Encoding.ASCII.GetString(stock, 0x40010, 12);

            Assert.Equal("0044570LO02S", program);
            Assert.NotEqual(program, data);
        }

        [Fact]
        public void ReadProgramVersionReturnsNullForShortOrNonAsciiInput()
        {
            Assert.Null(EwsDelete.ReadProgramVersion(null));
            Assert.Null(EwsDelete.ReadProgramVersion(new byte[0x100]));

            // Right size but binary garbage in the version field.
            var noisy = new byte[EwsDelete.FullFlashLength];
            for (int i = 0; i < 12; i++) noisy[0x6031C + i] = 0x00;
            Assert.Null(EwsDelete.ReadProgramVersion(noisy));
        }

        [Fact]
        public void RejectsWrongLength()
        {
            var tooSmall = new byte[0x1D000];
            Assert.Throws<InvalidOperationException>(() => EwsDelete.Apply(tooSmall));
            Assert.False(EwsDelete.IsApplicable(tooSmall));
        }

        [Fact]
        public void RejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => EwsDelete.Apply(null));
            Assert.False(EwsDelete.IsApplicable(null));
        }

        [Fact]
        public void RefusesAnImageThatDoesNotMatchThePattern()
        {
            // Right size, but zeroed: the patch sites hold neither the stock
            // instructions nor the patched ones, so this must be refused rather
            // than written to blind.
            var blank = new byte[EwsDelete.FullFlashLength];

            Assert.False(EwsDelete.IsApplicable(blank));
            Assert.False(EwsDelete.IsAlreadyPatched(blank));
            Assert.Throws<InvalidOperationException>(() => EwsDelete.Apply(blank));
        }
    
        // --- Fixture-free synthetic coverage of the two-byte method ---------

        private static byte[] BuildSyntheticStock()
        {
            var img = new byte[0x100000];
            var ver = System.Text.Encoding.ASCII.GetBytes("0044570LO02S");
            Buffer.BlockCopy(ver, 0, img, 0x6031C, ver.Length);
            img[0x48F2C] = 0x10;
            img[0x48F3E] = 0x10;
            img[0xDB1C7] = 0x01;
            img[0xDB1D3] = 0x3F;
            return img;
        }

        [Fact]
        public void SyntheticApplyFlipsBothFlagsAndNothingElse()
        {
            byte[] stock = BuildSyntheticStock();
            Assert.True(EwsDelete.IsApplicable(stock));
            Assert.False(EwsDelete.IsAlreadyPatched(stock));

            byte[] patched = EwsDelete.Apply(stock);
            Assert.Equal(0x00, patched[0x48F2C]);
            Assert.Equal(0x00, patched[0x48F3E]);

            int changed = 0;
            for (int i = 0; i < stock.Length; i++)
                if (stock[i] != patched[i]) changed++;
            Assert.Equal(4, changed);
            Assert.Equal(0x00, patched[0xDB1C7]);
            Assert.Equal(0x00, patched[0xDB1D3]);

            Assert.True(EwsDelete.IsAlreadyPatched(patched));
            Assert.False(EwsDelete.IsApplicable(patched));
        }

        [Fact]
        public void SyntheticApplyIsIdempotent()
        {
            byte[] patched = EwsDelete.Apply(BuildSyntheticStock());
            byte[] again = EwsDelete.Apply(patched);
            Assert.True(patched.AsSpan().SequenceEqual(again));
        }

        [Fact]
        public void SyntheticWrongVersionIsRefused()
        {
            byte[] img = BuildSyntheticStock();
            var label = System.Text.Encoding.ASCII.GetBytes("0044570LN00S");
            Buffer.BlockCopy(label, 0, img, 0x6031C, label.Length);
            Assert.False(EwsDelete.IsApplicable(img));
            Assert.Throws<InvalidOperationException>(() => EwsDelete.Apply(img));
        }

        [Fact]
        public void SyntheticWrongFlagValueIsRefused()
        {
            byte[] img = BuildSyntheticStock();
            img[0x48F2C] = 0x11; // not the expected stock 0x10
            Assert.False(EwsDelete.IsApplicable(img));
            Assert.Throws<InvalidOperationException>(() => EwsDelete.Apply(img));
        }

        // --- Program/tune mismatch detection (tune-only flash guard) ---------

        [Fact]
        public void ProgramBytesAreDeletedRecognisesBothStates()
        {
            Assert.True(EwsDelete.ProgramBytesAreDeleted(0x00, 0x00));
            Assert.False(EwsDelete.ProgramBytesAreDeleted(0x01, 0x3F)); // stock
            Assert.False(EwsDelete.ProgramBytesAreDeleted(0x00, 0x3F)); // half
            Assert.False(EwsDelete.ProgramBytesAreDeleted(0x01, 0x00)); // half
        }

        [Fact]
        public void CalibrationImmobilizerStateIsDetectedInAFullImage()
        {
            byte[] stock = BuildSyntheticStock();
            Assert.True(EwsDelete.CalibrationHasStockImmobilizer(stock, 0x40000));
            Assert.False(EwsDelete.CalibrationHasDeletedImmobilizer(stock, 0x40000));

            byte[] deleted = EwsDelete.Apply(stock);
            Assert.False(EwsDelete.CalibrationHasStockImmobilizer(deleted, 0x40000));
            Assert.True(EwsDelete.CalibrationHasDeletedImmobilizer(deleted, 0x40000));
        }

        [Fact]
        public void CalibrationImmobilizerStateIsDetectedInABareSlice()
        {
            var slice = new byte[0x1D000];
            slice[0x48F2C - 0x40000] = 0x10;
            slice[0x48F3E - 0x40000] = 0x10;
            Assert.True(EwsDelete.CalibrationHasStockImmobilizer(slice, 0));

            byte[] matched = EwsDelete.ApplyCalibrationDelete(slice, 0);
            Assert.Equal(0x00, matched[0x48F2C - 0x40000]);
            Assert.Equal(0x00, matched[0x48F3E - 0x40000]);
            Assert.True(EwsDelete.CalibrationHasDeletedImmobilizer(matched, 0));
            Assert.Equal(0x10, slice[0x48F2C - 0x40000]);
        }

        [Fact]
        public void ApplyCalibrationDeleteChangesOnlyTheTwoFlags()
        {
            byte[] stock = BuildSyntheticStock();
            byte[] matched = EwsDelete.ApplyCalibrationDelete(stock, 0x40000);
            int changed = 0;
            for (int i = 0; i < stock.Length; i++)
                if (stock[i] != matched[i]) changed++;
            Assert.Equal(2, changed);
            Assert.Equal(0x00, matched[0x48F2C]);
            Assert.Equal(0x00, matched[0x48F3E]);
            Assert.Equal(stock[0xDB1C7], matched[0xDB1C7]);
            Assert.Equal(stock[0xDB1D3], matched[0xDB1D3]);
        }

    }
}
