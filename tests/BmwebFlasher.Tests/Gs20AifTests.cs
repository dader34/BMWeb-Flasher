using System;
using Xunit;

namespace BmwebFlasher.Tests
{
    public class Gs20AifTests
    {
        // The factory entry read from a 7544721 transmission's AIF area
        // (0x08F000), decoded by BMW's AIF_LESEN as: VIN WBAEV33415KW20131,
        // 18.10.04, index DA, software 7543051, assembly 7543050, approval
        // 1000000, tester 42324, dealer 12321, 0 km, program reference 0089C0.
        private static readonly byte[] FactoryEntry =
        {
            0x20, 0x2C, 0xA3, 0x9F, 0x0C, 0x31, 0x01, 0x15, 0x48, 0x02, 0x00, 0x10, 0xC1, 0x00,
            0x95, 0x04, 0x00,
            0x73, 0x19, 0x0B, 0x03, 0x4A, 0x00,
            0x0F, 0x42, 0x40, 0x00,
            0x73, 0x19, 0x0A, 0x00,
            0x34, 0x32, 0x33, 0x32, 0x34, 0x00,
            0x30, 0x21, 0x00,
            0x00, 0x00,
            0x00, 0x89, 0xC0, 0x00,
        };

        [Fact]
        public void PacksTheRecordExactlyAsTheModuleHoldsIt()
        {
            byte[] record = Gs20Aif.Build(
                "WBAEV33415KW20131", new DateTime(2004, 10, 18), 7543051, "DA",
                1000000, 7543050, "42324", 12321, 0, new byte[] { 0x00, 0x89, 0xC0 });

            Assert.Equal(FactoryEntry, record);
        }

        // What AIF_SCHREIBEN in 10GD20.prg sent for
        // WBAEV33405KW12345 / 300926 / C0 / 7552700 / 123456789 / 7552700 /
        // 12345 / 12345 / 123456 km / program 1, captured off the job itself.
        [Fact]
        public void MatchesWhatBmwsProgrammingJobSends()
        {
            byte[] expected =
            {
                0x20, 0x2C, 0xA3, 0x9F, 0x0C, 0x31, 0x00, 0x15, 0x48, 0x01, 0x08, 0x31, 0x05, 0x00,
                0xF4, 0x9A, 0x00,
                0x73, 0x3E, 0xBC, 0x03, 0x30, 0x00,
                0x5B, 0xCD, 0x15, 0x00,
                0x73, 0x3E, 0xBC, 0x00,
                0x31, 0x32, 0x33, 0x34, 0x35, 0x00,
                0x30, 0x39, 0x00,
                0xCD, 0x00,
                0x00, 0x00, 0x01, 0x00,
            };

            byte[] record = Gs20Aif.Build(
                "WBAEV33405KW12345", new DateTime(2026, 9, 30), 7552700, "C0",
                123456789, 7552700, "12345", 12345, 123456, new byte[] { 0x00, 0x00, 0x01 });

            Assert.Equal(expected, record);
        }

        [Fact]
        public void AFreeSlotIsAnErasedOne()
        {
            Assert.True(Gs20Aif.IsFree(new byte[] { 0xFF }));
            Assert.False(Gs20Aif.IsFree(new byte[] { 0x20 }));
        }
    }
}
