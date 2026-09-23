using System;
using System.IO;
using System.Linq;
using BmwebFlasher;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The .0DA reader. The cases that matter are the ones that cost a real
    /// transmission an evening: a non-standard record type quietly dropped, and
    /// a calibration that passes its own CRC while missing the tail the module
    /// validates against.
    /// </summary>
    public class Gs20DatenFileTests
    {
        // These reference the author's SP-Daten and module reads. Every test
        // that uses them is skipped when they are absent, so the suite passes
        // on any machine; BMWEB_SPDATEN / BMWEB_MODULE_READ point them
        // elsewhere.
        private static string SpDaten =>
            Environment.GetEnvironmentVariable("BMWEB_SPDATEN")
            ?? "/Users/dannerbaumgartner/Desktop/e46bins/GS20-gearbox/Update/GD20";

        private static string Daten(string name) => Path.Combine(SpDaten, name);

        /// <summary>A minimal well-formed Daten file, built record by record.</summary>
        private static string WriteSynthetic(byte[] image, bool useBmwTailRecord = true,
                                             bool omitTail = false)
        {
            string path = Path.GetTempFileName();
            using var w = new StreamWriter(path);
            w.WriteLine(";;ZL_Referenz:    G2210_0090C0ZZ99");
            w.WriteLine(";;K_F1:  Daten fuer E46 TEST MY06");
            w.WriteLine(Record(4, 0, new byte[] { 0x00, 0x00 }));       // linear 0x000000
            w.WriteLine(Record(2, 0, new byte[] { 0x90, 0x00 }));       // segment 0x090000

            int end = omitTail ? image.Length - 32 : image.Length;
            for (int off = 0; off < end; off += 32)
            {
                int n = Math.Min(32, end - off);
                bool last = off + n >= image.Length;
                int type = (last && useBmwTailRecord) ? 0x10 : 0x00;
                w.WriteLine(Record(type, off, image.Skip(off).Take(n).ToArray()));
            }
            w.WriteLine(":00000001FF");
            return path;
        }

        private static string Record(int type, int address, byte[] data)
        {
            var bytes = new byte[data.Length + 5];
            bytes[0] = (byte)data.Length;
            bytes[1] = (byte)(address >> 8);
            bytes[2] = (byte)address;
            bytes[3] = (byte)type;
            Buffer.BlockCopy(data, 0, bytes, 4, data.Length);
            int sum = 0;
            for (int i = 0; i < bytes.Length - 1; i++) sum += bytes[i];
            bytes[bytes.Length - 1] = (byte)(-sum & 0xFF);
            return ":" + BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static byte[] SampleCalibration()
        {
            var cal = new byte[Gs20Checksum.CalLength];
            for (int i = 0; i < cal.Length; i++) cal[i] = (byte)((i * 29 + 7) & 0xFF);
            Buffer.BlockCopy(Gs20DatenFile.Trailer, 0, cal,
                             cal.Length - Gs20DatenFile.Trailer.Length,
                             Gs20DatenFile.Trailer.Length);
            return cal;
        }

        [Fact]
        public void DecodesAFileThatUsesBmwsOwnTailRecordType()
        {
            byte[] expected = SampleCalibration();
            string path = WriteSynthetic(expected);
            try
            {
                Assert.Equal(expected, Gs20DatenFile.Decode(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DecodesAFileThatUsesOnlyStandardRecords()
        {
            byte[] expected = SampleCalibration();
            string path = WriteSynthetic(expected, useBmwTailRecord: false);
            try
            {
                Assert.Equal(expected, Gs20DatenFile.Decode(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RefusesAFileThatDoesNotCoverTheWholeCalibration()
        {
            string path = WriteSynthetic(SampleCalibration(), omitTail: true);
            try
            {
                var ex = Assert.Throws<InvalidDataException>(
                    () => Gs20DatenFile.Decode(path));
                Assert.Contains("does not cover", ex.Message);
                Assert.Contains("32", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RefusesARecordWithABadChecksum()
        {
            string path = WriteSynthetic(SampleCalibration());
            try
            {
                string[] lines = File.ReadAllLines(path);
                int i = Array.FindIndex(lines, l => l.StartsWith(":20"));
                lines[i] = lines[i].Substring(0, lines[i].Length - 2) + "00";
                File.WriteAllLines(path, lines);

                var ex = Assert.Throws<InvalidDataException>(
                    () => Gs20DatenFile.Decode(path));
                Assert.Contains("record checksum", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RefusesAnUnknownRecordTypeRatherThanSkippingIt()
        {
            string path = WriteSynthetic(SampleCalibration());
            try
            {
                string[] lines = File.ReadAllLines(path);
                int i = Array.FindIndex(lines, l => l.StartsWith(":20"));
                // rebuild the same record under a type nobody has seen
                var bytes = Enumerable.Range(0, (lines[i].Length - 1) / 2)
                    .Select(k => Convert.ToByte(lines[i].Substring(1 + k * 2, 2), 16))
                    .ToArray();
                lines[i] = Record(0x42, (bytes[1] << 8) | bytes[2],
                                  bytes.Skip(4).Take(bytes[0]).ToArray());
                File.WriteAllLines(path, lines);

                var ex = Assert.Throws<InvalidDataException>(
                    () => Gs20DatenFile.Decode(path));
                Assert.Contains("0x42", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RecognisesTheTrailerEveryCalibrationCarries()
        {
            Assert.True(Gs20DatenFile.HasTrailer(SampleCalibration()));

            byte[] truncated = SampleCalibration();
            truncated[truncated.Length - 1] = 0x00;
            Assert.False(Gs20DatenFile.HasTrailer(truncated));

            // the exact shape of the file that cost a transmission an evening:
            // the last 32 bytes blank
            byte[] blanked = SampleCalibration();
            for (int i = blanked.Length - 32; i < blanked.Length; i++) blanked[i] = 0xFF;
            Assert.False(Gs20DatenFile.HasTrailer(blanked));
        }

        [Theory]
        [InlineData("A7557995.0DA", "G2210_0090C0ES10", "E46 M54B30 USA SPORT MY06")]
        [InlineData("A7557985.0DA", "G2210_0090C0DP10", "E46/16 M54B30 USA MY06")]
        public void ReadsTheHeaderOfARealDatenFile(string file, string reference,
                                                   string vehicle)
        {
            Skip.IfNot(File.Exists(Daten(file)), "SP-Daten is not on this machine");
            Assert.Equal(reference, Gs20DatenFile.ReadReference(Daten(file)));
            Assert.Equal(vehicle, Gs20DatenFile.ReadVehicle(Daten(file)));
        }

        [SkippableTheory]
        [InlineData("A7557995.0DA", "G2210_0090C0ES10")]
        [InlineData("A7557985.0DA", "G2210_0090C0DP10")]
        public void DecodesARealDatenFileToAValidCalibration(string file, string reference)
        {
            Skip.IfNot(File.Exists(Daten(file)), "SP-Daten is not on this machine");

            byte[] cal = Gs20DatenFile.Decode(Daten(file));

            Assert.Equal(Gs20Checksum.CalLength, cal.Length);
            Assert.True(Gs20Checksum.Verify(cal), "the decoded calibration should checksum");
            Assert.True(Gs20DatenFile.HasTrailer(cal), "it should carry the trailer");
            Assert.Equal(reference, Gs20Checksum.ReadVersion(cal));
        }

        [SkippableFact]
        public void DecodingMatchesWhatTheModuleActuallyHolds()
        {
            // The strongest check available: a calibration read back off a real
            // transmission, against the same calibration decoded from SP-Daten.
            string read = Environment.GetEnvironmentVariable("BMWEB_MODULE_READ")
                ?? "/Users/dannerbaumgartner/Downloads/" +
                   "TCU_cal_20260923_015604_error_code_fixed.bin";
            Skip.IfNot(File.Exists(read) && File.Exists(Daten("A7557985.0DA")),
                       "the module read or SP-Daten is not on this machine");

            Assert.Equal(File.ReadAllBytes(read), Gs20DatenFile.Decode(Daten("A7557985.0DA")));
        }

        [Fact]
        public void OnlyTreatsDotZeroDAAsADatenFile()
        {
            Assert.True(Gs20DatenFile.IsDatenFile("A7557995.0DA"));
            Assert.True(Gs20DatenFile.IsDatenFile("a7557995.0da"));
            Assert.False(Gs20DatenFile.IsDatenFile("cal.bin"));
            Assert.False(Gs20DatenFile.IsDatenFile("7552700A.0PA"));
            Assert.False(Gs20DatenFile.IsDatenFile(null));
        }
    }
}
