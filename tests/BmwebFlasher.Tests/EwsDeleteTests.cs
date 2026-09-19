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
        public void ApplyReproducesKnownGoodImageByteForByte()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN and MS45_EWS_BIN to run this.");

            byte[] stock = File.ReadAllBytes(StockPath);
            byte[] expected = File.ReadAllBytes(PatchedPath);

            byte[] actual = EwsDelete.Apply(stock);

            Assert.Equal(expected.Length, actual.Length);
            Assert.True(expected.AsSpan().SequenceEqual(actual),
                "Patched output does not match the known-good image.");
        }

        [SkippableFact]
        public void ApplyChangesExactlySeventeenBytes()
        {
            Skip.IfNot(HaveFixtures, "Set MS45_STOCK_BIN and MS45_EWS_BIN to run this.");

            byte[] stock = File.ReadAllBytes(StockPath);
            byte[] actual = EwsDelete.Apply(stock);

            int changed = 0;
            for (int i = 0; i < stock.Length; i++)
            {
                if (stock[i] != actual[i]) changed++;
            }

            // 5 single-byte immediates + 3 four-byte nops, of which the nop at
            // 0xD297C differs in 4 bytes and the two at 0xD2C54/58 in 4 each.
            Assert.Equal(17, changed);
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
    }
}
