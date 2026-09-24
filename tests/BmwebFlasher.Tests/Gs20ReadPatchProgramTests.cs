using System;
using System.IO;
using System.Linq;
using BmwebFlasher;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The program the app ships for one-click installation of the read
    /// patch. It has to be the program region of the image that was proven
    /// on a bench module -- nothing regenerated, nothing edited since.
    /// </summary>
    public class Gs20ReadPatchProgramTests
    {
        private static string Asset =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "BmwebFlasher", "Assets",
                "gs20_7552700_readpatch_program.bin"));

        private static string ProvenImage =>
            Environment.GetEnvironmentVariable("BMWEB_PATCHED_IMAGE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Desktop", "e46bins", "GS20-gearbox", "patched",
                            "gs20_TEST_subcode8_512k.bin");

        [Fact]
        public void EmbeddedProgramIsAWholeValidPatchedRegion()
        {
            Assert.True(File.Exists(Asset), "asset missing at " + Asset);
            byte[] p = File.ReadAllBytes(Asset);

            Assert.Equal(Gs20ProgramChecksum.ProgramLength, p.Length);
            Assert.True(Gs20ProgramChecksum.Verify(p), "checksum must already be correct");

            // the routine's first instruction, cmpb RL3,#8, at program 0x0D34C
            Assert.Equal(new byte[] { 0x47, 0xF6, 0x08, 0x00 }, p.Skip(0x0D34C).Take(4).ToArray());
            // the hook retargets an existing jmpa to the trampoline at 0xFFFC
            Assert.Equal(new byte[] { 0xEA, 0xE0, 0xFC, 0xFF }, p.Skip(0x29D70).Take(4).ToArray());
            // the trampoline: jmps 0x0A,0xD34C
            Assert.Equal(new byte[] { 0xFA, 0x0A, 0x4C, 0xD3 }, p.Skip(0x2FFFC).Take(4).ToArray());
            // and it declares the release the hook is valid on
            string tail = System.Text.Encoding.ASCII.GetString(p, p.Length - 0x100, 0x100);
            Assert.Contains("G2210_0090C0", tail);
        }

        [SkippableFact]
        public void EmbeddedProgramIsTheProvenImagesProgramRegion()
        {
            Skip.IfNot(File.Exists(ProvenImage), "the proven image is not on this machine");
            byte[] image = File.ReadAllBytes(ProvenImage);
            Assert.Equal(image.Skip(0x20000).Take(0x40000).ToArray(), File.ReadAllBytes(Asset));
        }
    }
}
