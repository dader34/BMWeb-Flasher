using System;
using System.IO;
using System.Text;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The synthetic tests write files in the same shape as BMW's: a segment
    /// record and a linear record opening each 64 KB block, and the block's
    /// last data record marked type 0x10.
    ///
    /// The fixture tests use real SP-Daten files, which are not in the repo.
    /// MS45_0PA with MS45_0PA_FLASH / MS45_0PA_MPC (a stock full read pair of
    /// the same program), and MS45_0DA with MS45_0DA_BIN (the matching stock
    /// calibration partial), run the comparisons.
    /// </summary>
    public class Ms45ExchangeFileTests
    {
        private static string Record(int offset, int type, byte[] data)
        {
            var bytes = new byte[data.Length + 5];
            bytes[0] = (byte)data.Length;
            bytes[1] = (byte)(offset >> 8);
            bytes[2] = (byte)offset;
            bytes[3] = (byte)type;
            data.CopyTo(bytes, 4);
            int sum = 0;
            for (int i = 0; i < bytes.Length - 1; i++) sum += bytes[i];
            bytes[bytes.Length - 1] = (byte)(-sum);
            return ":" + Convert.ToHexString(bytes);
        }

        /// <summary>Writes [start, end) of an address space whose byte at a is fill(a).</summary>
        private static void Write(StringBuilder sb, uint start, uint end, Func<uint, byte> fill)
        {
            uint a = start;
            while (a < end)
            {
                uint block = a & 0xFFFF0000;
                uint linear = block >> 16 & 0xFF00;            // 0x0200 for the external flash
                uint segment = (block - (linear << 16)) >> 4;
                sb.AppendLine(Record(0, 2, new[] { (byte)(segment >> 8), (byte)segment }));
                sb.AppendLine(Record(0, 4, new[] { (byte)(linear >> 8), (byte)linear }));

                uint blockEnd = Math.Min(end, block + 0x10000);
                while (a < blockEnd)
                {
                    int n = (int)Math.Min(16, blockEnd - a);
                    var data = new byte[n];
                    for (int i = 0; i < n; i++) data[i] = fill(a + (uint)i);
                    bool last = a + n == block + 0x10000;
                    sb.AppendLine(Record((int)(a & 0xFFFF), last ? 0x10 : 0, data));
                    a += (uint)n;
                }
            }
        }

        private static byte Pattern(uint a) => (byte)(a * 7 + (a >> 8) * 3 + (a >> 16));

        private static string ProgramText(bool dropMpcTail = false, bool end = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine(";Austausch-Datei Programm");
            sb.AppendLine(";;ZL_Referenz:      0044570LO02S");
            Write(sb, 0, dropMpcTail ? 0x6FFF0u : 0x70000u, Pattern);
            Write(sb, 0x2060000, 0x20600B4, Pattern);
            Write(sb, 0x2060100, 0x20FFF40, Pattern);
            if (end) sb.AppendLine(":00000001FF");
            sb.AppendLine("$REFERENZ 0044570LO02S E");
            sb.AppendLine("$CHECKSUMME E485 U");
            return sb.ToString();
        }

        private static string DataText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(";Austausch-Datei Daten");
            Write(sb, 0x2040000, 0x20401B4, Pattern);
            Write(sb, 0x2040200, 0x205D000, Pattern);
            sb.AppendLine(":00000001FF");
            sb.AppendLine("$REFERENZ 0044570LO00S01J2R 5");
            return sb.ToString();
        }

        [Fact]
        public void ProgramDecodesToBothChips()
        {
            Ms45ExchangeFile.Program p = Ms45ExchangeFile.DecodeProgramText(ProgramText());

            Assert.Equal("0044570LO02S", p.Reference);
            Assert.Equal(0x70000, p.Mpc.Length);
            Assert.Equal(0x100000, p.Flash.Length);

            for (uint a = 0; a < 0x70000; a++)
                Assert.True(p.Mpc[a] == Pattern(a), "MPC 0x" + a.ToString("X"));
            for (uint a = 0x60100; a < 0xFFF40; a++)
                Assert.True(p.Flash[a] == Pattern(0x2000000 + a), "flash 0x" + a.ToString("X"));
            Assert.Equal(Pattern(0x2060000), p.Flash[0x60000]);
        }

        [Fact]
        public void ProgramLeavesBootTuneAndGapsBlank()
        {
            Ms45ExchangeFile.Program p = Ms45ExchangeFile.DecodeProgramText(ProgramText());

            for (int a = 0; a < 0x60000; a++)
                Assert.True(p.Flash[a] == 0xFF, "flash 0x" + a.ToString("X"));
            for (int a = 0x600B4; a < 0x60100; a++)
                Assert.True(p.Flash[a] == 0xFF, "flash 0x" + a.ToString("X"));
            for (int a = 0xFFF40; a < 0x100000; a++)
                Assert.True(p.Flash[a] == 0xFF, "flash 0x" + a.ToString("X"));
        }

        [Fact]
        public void DataDecodesToACalibrationPartial()
        {
            Ms45ExchangeFile.Calibration c = Ms45ExchangeFile.DecodeCalibrationText(DataText());

            Assert.Equal("0044570LO00S01J2R", c.Reference);
            Assert.Equal(0x1D000, c.Data.Length);
            for (uint a = 0x200; a < 0x1D000; a++)
                Assert.True(c.Data[a] == Pattern(0x2040000 + a), "cal 0x" + a.ToString("X"));
            Assert.Equal(0xFF, c.Data[0x1C0]);
        }

        [Fact]
        public void ACorruptRecordIsRefused()
        {
            string text = ProgramText();
            int at = text.IndexOf(":10", text.Length / 2, StringComparison.Ordinal);
            char flipped = text[at + 12] == '0' ? '1' : '0';
            text = text.Substring(0, at + 12) + flipped + text.Substring(at + 13);

            Assert.Throws<InvalidDataException>(() => Ms45ExchangeFile.DecodeProgramText(text));
        }

        [Fact]
        public void AnIncompleteProgramIsRefused()
        {
            Assert.Throws<InvalidDataException>(
                () => Ms45ExchangeFile.DecodeProgramText(ProgramText(dropMpcTail: true)));
        }

        [Fact]
        public void ATruncatedFileIsRefused()
        {
            Assert.Throws<InvalidDataException>(
                () => Ms45ExchangeFile.DecodeProgramText(ProgramText(end: false)));
        }

        [Fact]
        public void TheWrongKindOfFileIsRefused()
        {
            Assert.Throws<InvalidDataException>(() => Ms45ExchangeFile.DecodeProgramText(DataText()));
            Assert.Throws<InvalidDataException>(() => Ms45ExchangeFile.DecodeCalibrationText(ProgramText()));
        }

        [Fact]
        public void KindIsToldFromTheExtension()
        {
            Assert.True(Ms45ExchangeFile.IsProgramFile("/x/7561382A.0PA"));
            Assert.True(Ms45ExchangeFile.IsProgramFile("7561382a.0pa"));
            Assert.True(Ms45ExchangeFile.IsDataFile("P7561517.0DA"));
            Assert.False(Ms45ExchangeFile.IsProgramFile("P7561517.0DA"));
            Assert.False(Ms45ExchangeFile.IsDataFile("tune.bin"));
            Assert.False(Ms45ExchangeFile.IsDataFile(null));
        }

        [Fact]
        public void APairIsMatchedByItsProjectToken()
        {
            var flash = new byte[0x100000];
            Encoding.ASCII.GetBytes("457O0L10457O0L").CopyTo(flash, 0x60302);

            var mine = new byte[0x1D000];
            Encoding.ASCII.GetBytes("LO006J2R457O0L00").CopyTo(mine, 0);
            var other = new byte[0x1D000];
            Encoding.ASCII.GetBytes("LN00FJ1N457N0L00").CopyTo(other, 0);

            Assert.Equal("457O0L", Ms45ExchangeFile.ProgramProjectToken(flash));
            Assert.Equal("457O0L", Ms45ExchangeFile.CalibrationProjectToken(mine));
            Assert.True(Ms45ExchangeFile.IsMatchingPair(flash, mine));
            Assert.False(Ms45ExchangeFile.IsMatchingPair(flash, other));
            Assert.False(Ms45ExchangeFile.IsMatchingPair(flash, new byte[0x1D000]));
            Assert.False(Ms45ExchangeFile.IsMatchingPair(new byte[0x100000], mine));
        }

        [SkippableFact]
        public void RealFilesPairOnlyWithTheirOwnProgram()
        {
            // MS45_0DA belongs to MS45_0PA; MS45_0DA_OTHER to a different program.
            string pa = Env("MS45_0PA"), da = Env("MS45_0DA"), other = Env("MS45_0DA_OTHER");
            Skip.If(pa == null || da == null || other == null,
                "Set MS45_0PA, MS45_0DA and MS45_0DA_OTHER to run this.");

            byte[] flash = Ms45ExchangeFile.DecodeProgram(pa).Flash;
            Assert.True(Ms45ExchangeFile.IsMatchingPair(flash, Ms45ExchangeFile.DecodeCalibration(da).Data));
            Assert.False(Ms45ExchangeFile.IsMatchingPair(flash, Ms45ExchangeFile.DecodeCalibration(other).Data));
        }

        // --- Fixture tests ------------------------------------------------

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(v) && File.Exists(v) ? v : null;
        }

        [SkippableFact]
        public void RealProgramFileMatchesAStockRead()
        {
            string pa = Env("MS45_0PA"), flash = Env("MS45_0PA_FLASH"), mpc = Env("MS45_0PA_MPC");
            Skip.If(pa == null || flash == null || mpc == null,
                "Set MS45_0PA, MS45_0PA_FLASH and MS45_0PA_MPC to run this.");

            Ms45ExchangeFile.Program p = Ms45ExchangeFile.DecodeProgram(pa);
            byte[] readFlash = File.ReadAllBytes(flash);

            Assert.Equal(File.ReadAllBytes(mpc), p.Mpc);
            for (int a = 0x60100; a < 0xFFF40; a++)
                Assert.True(p.Flash[a] == readFlash[a], "flash 0x" + a.ToString("X"));
            for (int a = 0x60000; a < 0x600B4; a++)
                Assert.True(p.Flash[a] == readFlash[a], "flash 0x" + a.ToString("X"));
        }

        [SkippableFact]
        public void RealDataFileMatchesAStockPartial()
        {
            string da = Env("MS45_0DA"), bin = Env("MS45_0DA_BIN");
            Skip.If(da == null || bin == null, "Set MS45_0DA and MS45_0DA_BIN to run this.");

            Ms45ExchangeFile.Calibration c = Ms45ExchangeFile.DecodeCalibration(da);
            Assert.Equal(File.ReadAllBytes(bin), c.Data);
        }
    }
}
