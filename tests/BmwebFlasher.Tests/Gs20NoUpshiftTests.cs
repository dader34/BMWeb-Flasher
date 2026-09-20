using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The ground truth is a stock calibration and a no-upshift one built from
    /// it by hand: applying the patch to the former has to reproduce the latter.
    ///
    /// Those images are BMW-derived and are not in the repo. Point GS20_CAL_DIR
    /// at a folder holding them to run the comparison; without it only the
    /// synthetic tests run.
    /// </summary>
    public class Gs20NoUpshiftTests
    {
        private static string CalDir => Environment.GetEnvironmentVariable("GS20_CAL_DIR");

        private static byte[] Load(string name)
        {
            string dir = CalDir;
            if (string.IsNullOrEmpty(dir)) return null;
            string path = Path.Combine(dir, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        [SkippableFact]
        public void ApplyReproducesTheHandBuiltImage()
        {
            byte[] stock = Load("gs20_7552700_90_partial.bin");
            byte[] expected = Load("gs20_7552700_90_partial_noupshift_all.bin");
            Skip.If(stock == null || expected == null, "Set GS20_CAL_DIR to run this.");

            byte[] patched = Gs20NoUpshift.Apply(stock);

            // The checksum is corrected on the way to the car, not here, so it
            // is the only byte that may differ.
            for (int i = 0; i < patched.Length; i++)
            {
                if (i == Gs20Checksum.StoredOffset || i == Gs20Checksum.StoredOffset + 1) continue;
                Assert.True(patched[i] == expected[i],
                    "byte 0x" + i.ToString("X4") + " is " + patched[i].ToString("X2") +
                    ", expected " + expected[i].ToString("X2"));
            }
        }

        [SkippableFact]
        public void TheEntryCountsAreLeftAlone()
        {
            byte[] stock = Load("gs20_7552700_90_partial.bin");
            Skip.If(stock == null, "Set GS20_CAL_DIR to run this.");

            byte[] patched = Gs20NoUpshift.Apply(stock);

            // One circulating patched file overwrites these, turning a count of
            // seventeen into 8160. The gearbox tolerates it; that is not a
            // reason to reproduce it.
            foreach (int group in new[] { 0x0D44, 0x0E64, 0x11C4, 0x12E4 })
            {
                for (int table = 0; table < 4; table++)
                {
                    int at = group + table * 36;
                    Assert.Equal(17, patched[at]);
                    Assert.Equal(0, patched[at + 1]);
                }
            }
        }

        [SkippableFact]
        public void AStockCalibrationIsRecognisedAsNotYetPatched()
        {
            byte[] stock = Load("gs20_7552700_90_partial.bin");
            Skip.If(stock == null, "Set GS20_CAL_DIR to run this.");

            Assert.True(Gs20NoUpshift.IsApplicable(stock));
            Assert.False(Gs20NoUpshift.IsApplied(stock));
        }

        [SkippableFact]
        public void BothShippedSoftwareLevelsHaveTheSameLayout()
        {
            byte[] older = Load("gs20_7544721_89_partial.bin");
            byte[] newer = Load("gs20_7552700_90_partial.bin");
            Skip.If(older == null || newer == null, "Set GS20_CAL_DIR to run this.");

            Assert.True(Gs20NoUpshift.IsApplicable(older));
            Assert.True(Gs20NoUpshift.IsApplicable(newer));
        }

        [Fact]
        public void ApplyLeavesTheCallersArrayAlone()
        {
            byte[] cal = SyntheticCal();
            byte[] before = (byte[])cal.Clone();

            Gs20NoUpshift.Apply(cal);

            Assert.True(before.AsSpan().SequenceEqual(cal));
        }

        [Fact]
        public void ApplyingTwiceChangesNothingTheSecondTime()
        {
            byte[] once = Gs20NoUpshift.Apply(SyntheticCal());

            Assert.True(Gs20NoUpshift.Apply(once).AsSpan().SequenceEqual(once));
            Assert.True(Gs20NoUpshift.IsApplied(once));
        }

        [Fact]
        public void AnImageWithoutTheTableCountsIsRefused()
        {
            byte[] cal = SyntheticCal();
            cal[0x0D44] = 0x99;   // not a count of seventeen any more

            Assert.False(Gs20NoUpshift.IsApplicable(cal));
            Assert.Throws<ArgumentException>(() => Gs20NoUpshift.Apply(cal));
        }

        [Fact]
        public void AWrongSizedImageIsRefused()
        {
            Assert.False(Gs20NoUpshift.IsApplicable(new byte[0x8000]));
        }

        /// <summary>A calibration with the table counts the patch looks for.</summary>
        private static byte[] SyntheticCal()
        {
            var cal = new byte[Gs20Checksum.CalLength];
            new Random(20260920).NextBytes(cal);

            foreach (int group in new[] { 0x0D44, 0x0E64, 0x11C4, 0x12E4 })
            {
                for (int table = 0; table < 4; table++)
                {
                    int at = group + table * 36;
                    cal[at] = 17;
                    cal[at + 1] = 0;
                }
            }
            return cal;
        }
    }
}
