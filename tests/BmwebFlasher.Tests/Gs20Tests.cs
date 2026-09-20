using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The checksum's ground truth is a set of calibrations the transmission
    /// itself corrected: each was flashed and read back, so its stored value is
    /// the module's own answer for that data.
    ///
    /// Those images are BMW-derived and are not in the repo. Point GS20_CAL_DIR
    /// at a folder holding them to run the fixture tests; without it only the
    /// synthetic ones run.
    /// </summary>
    public class Gs20ChecksumTests
    {
        private static string CalDir => Environment.GetEnvironmentVariable("GS20_CAL_DIR");

        private static bool HaveFixtures =>
            !string.IsNullOrEmpty(CalDir) && Directory.Exists(CalDir);

        /// <summary>
        /// Calibrations whose stored checksum came back from the transmission,
        /// with the value it wrote. Read back off the car after a flash.
        /// </summary>
        public static IEnumerable<object[]> CorrectedCalibrations => new[]
        {
            new object[] { "gs20_7552700_90_partial_after.bin", (ushort)0x35A8 },
            new object[] { "gs20_7552700_90_partial1bytediffafter.bin", (ushort)0x4CB6 },
            new object[] { "gs20_7552700_90_partial_2.bin", (ushort)0xC54E },
            // Factory calibrations, both shipped software levels. The 7544721
            // ("89") files never informed the derivation, so they are the real
            // out-of-sample check.
            new object[] { "gs20_7552700_90_partial.bin", (ushort)0x58C3 },
            new object[] { "gs20_7544721_89_partial.bin", (ushort)0xF646 },
            new object[] { "gs20_7544721_89_partial_afteredits.bin", (ushort)0x35B4 },
            new object[] { "gs20_7544721_89_partial_new.bin", (ushort)0xAC41 },
        };

        [SkippableTheory]
        [MemberData(nameof(CorrectedCalibrations))]
        public void ComputeMatchesWhatTheTransmissionStored(string name, ushort expected)
        {
            Skip.IfNot(HaveFixtures, "Set GS20_CAL_DIR to run this.");
            string path = Path.Combine(CalDir, name);
            Skip.IfNot(File.Exists(path), name + " is not in GS20_CAL_DIR.");

            byte[] cal = File.ReadAllBytes(path);

            Assert.Equal(expected, Gs20Checksum.Stored(cal));
            Assert.Equal(expected, Gs20Checksum.Compute(cal));
            Assert.True(Gs20Checksum.Verify(cal));
        }

        [SkippableFact]
        public void CorrectingAnAlreadyValidCalibrationChangesNothing()
        {
            Skip.IfNot(HaveFixtures, "Set GS20_CAL_DIR to run this.");
            string path = Path.Combine(CalDir, "gs20_7552700_90_partial_after.bin");
            Skip.IfNot(File.Exists(path), "Reference calibration is not in GS20_CAL_DIR.");

            byte[] cal = File.ReadAllBytes(path);

            Assert.True(Gs20Checksum.Correct(cal).AsSpan().SequenceEqual(cal));
        }

        [Fact]
        public void CorrectLeavesTheCallersArrayAlone()
        {
            byte[] cal = SyntheticCal();
            byte[] before = (byte[])cal.Clone();

            Gs20Checksum.Correct(cal);

            Assert.True(before.AsSpan().SequenceEqual(cal));
        }

        [Fact]
        public void ACorrectedCalibrationVerifies()
        {
            byte[] corrected = Gs20Checksum.Correct(SyntheticCal());

            Assert.True(Gs20Checksum.Verify(corrected));
        }

        [Fact]
        public void ChangingDataInsideTheRangeChangesTheChecksum()
        {
            byte[] cal = Gs20Checksum.Correct(SyntheticCal());
            ushort before = Gs20Checksum.Compute(cal);

            cal[0x2000] ^= 0x01;

            Assert.NotEqual(before, Gs20Checksum.Compute(cal));
        }

        [Fact]
        public void ChangingDataOutsideTheRangeDoesNot()
        {
            byte[] cal = Gs20Checksum.Correct(SyntheticCal());
            ushort before = Gs20Checksum.Compute(cal);

            // The version block past 0xFFC8 sits outside the checksummed range.
            cal[0xFFF0] ^= 0x01;

            Assert.Equal(before, Gs20Checksum.Compute(cal));
        }

        [Fact]
        public void TheChecksumFieldItselfIsOutsideTheRange()
        {
            byte[] cal = Gs20Checksum.Correct(SyntheticCal());
            ushort before = Gs20Checksum.Compute(cal);

            // Were the stored bytes inside the range, correcting one would change
            // the answer and no fixed point could exist.
            cal[Gs20Checksum.StoredOffset] ^= 0xFF;

            Assert.Equal(before, Gs20Checksum.Compute(cal));
        }

        [Fact]
        public void AWrongSizedImageIsRejected()
        {
            Assert.Throws<ArgumentException>(() => Gs20Checksum.Compute(new byte[0x8000]));
        }

        internal static byte[] SyntheticCal()
        {
            var cal = new byte[Gs20Checksum.CalLength];
            var random = new Random(20260919);
            random.NextBytes(cal);
            return cal;
        }
    }

    /// <summary>
    /// The write sequence is checked by replaying its telegrams into a stand-in
    /// module: starting from erased memory, applying every write must leave the
    /// exact image we meant to send.
    /// </summary>
    public class Gs20CalWriterTests
    {
        /// <summary>
        /// Answers like a healthy module and rebuilds what its flash would hold.
        /// </summary>
        private sealed class FakeModule : IDs2Link
        {
            public byte[] Memory = Enumerable.Repeat((byte)0xFF, Gs20Checksum.CalLength).ToArray();
            public readonly List<byte[]> Sent = new List<byte[]>();

            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                Sent.Add(telegram);

                // Every telegram must be well formed before a module would look at it.
                Assert.Equal(telegram.Length, telegram[1]);
                Assert.Equal(telegram[telegram.Length - 1],
                             Ds2Telegram.Checksum(telegram.AsSpan(0, telegram.Length - 1)));

                byte command = telegram[2];
                byte sub = telegram.Length > 3 ? telegram[3] : (byte)0;

                if (command == 0x07 && sub == 0x02)
                {
                    int address = (telegram[4] << 16) | (telegram[5] << 8) | telegram[6];
                    int length = telegram[7];
                    Buffer.BlockCopy(telegram, 8, Memory,
                                     address - Gs20CalWriter.CalAddress, length);
                }

                if (command == 0x90)
                    return new byte[] { Ds2Telegram.TcuAddress, 5, Ds2Telegram.StatusOk, 0, 0 };
                if (command == 0x0B)
                    return new byte[] { Ds2Telegram.TcuAddress, 6, Ds2Telegram.StatusOk, 0, 124, 0 };

                var reply = new byte[12];
                reply[0] = Ds2Telegram.TcuAddress;
                reply[1] = (byte)reply.Length;
                reply[2] = Ds2Telegram.StatusOk;
                reply[8] = 1;
                return reply;
            }
        }

        [Fact]
        public void TheModuleEndsUpHoldingExactlyTheCorrectedImage()
        {
            byte[] cal = Gs20ChecksumTests.SyntheticCal();
            byte[] expected = Gs20Checksum.Correct(cal);
            var module = new FakeModule();

            new Gs20CalWriter(module).Write(cal);

            Assert.True(expected.AsSpan().SequenceEqual(module.Memory));
            Assert.True(Gs20Checksum.Verify(module.Memory));
        }

        [Fact]
        public void BlankRegionsAreSkippedYetStillReadBackBlank()
        {
            byte[] cal = Gs20ChecksumTests.SyntheticCal();
            for (int i = 0x1000; i < 0x3000; i++) cal[i] = 0xFF;
            byte[] expected = Gs20Checksum.Correct(cal);
            var module = new FakeModule();

            new Gs20CalWriter(module).Write(cal);

            // Erase already leaves those cells blank, so skipping them is safe.
            Assert.True(expected.AsSpan().SequenceEqual(module.Memory));
            int writesCoveringTheGap = module.Sent.Count(t =>
            {
                if (t[2] != 0x07 || t[3] != 0x02) return false;
                int address = (t[4] << 16) | (t[5] << 8) | t[6];
                int offset = address - Gs20CalWriter.CalAddress;
                return offset >= 0x1000 && offset < 0x3000;
            });
            Assert.Equal(0, writesCoveringTheGap);
        }

        [Fact]
        public void AStaleChecksumIsCorrectedBeforeAnythingIsSent()
        {
            byte[] cal = Gs20ChecksumTests.SyntheticCal();
            cal[Gs20Checksum.StoredOffset] = 0xDE;
            cal[Gs20Checksum.StoredOffset + 1] = 0xAD;
            var module = new FakeModule();

            new Gs20CalWriter(module).Write(cal);

            Assert.True(Gs20Checksum.Verify(module.Memory));
        }

        [Fact]
        public void EveryWriteTelegramFitsTheProtocolsLengthByte()
        {
            var module = new FakeModule();

            new Gs20CalWriter(module).Write(Gs20ChecksumTests.SyntheticCal());

            foreach (byte[] telegram in module.Sent)
            {
                Assert.True(telegram.Length <= byte.MaxValue);
                if (telegram[2] == 0x07 && telegram[3] == 0x02)
                    Assert.True(telegram[7] <= Gs20CalWriter.WriteChunk);
            }
        }

        [Fact]
        public void TheSequenceErasesBeforeItWritesAndCommitsAfter()
        {
            var module = new FakeModule();

            new Gs20CalWriter(module).Write(Gs20ChecksumTests.SyntheticCal());

            var kinds = module.Sent
                .Select(t => t[2] == 0x07 ? "07" + t[3].ToString("X2") : t[2].ToString("X2"))
                .ToList();

            int erase = kinds.IndexOf("0706");
            int firstWrite = kinds.IndexOf("0702");
            int lastWrite = kinds.LastIndexOf("0702");
            int commit = kinds.LastIndexOf("070F");

            Assert.True(erase >= 0 && firstWrite > erase);
            Assert.True(commit > lastWrite);
        }

        /// <summary>
        /// A module that does not report itself already open must stop the write
        /// rather than have a key guessed for it. On this car the reply in that
        /// case is the identification string, and deriving a key from it sent
        /// something the module refused; only its refusal of the following erase
        /// kept the calibration intact.
        /// </summary>
        [Fact]
        public void AnUnrecognisedUnlockReplyStopsBeforeTheErase()
        {
            var module = new UnlockRefusingModule();

            Assert.Throws<InvalidOperationException>(() =>
                new Gs20CalWriter(module).Write(Gs20ChecksumTests.SyntheticCal()));

            Assert.DoesNotContain(module.Sent, t => t[2] == 0x07 && t[3] == 0x06);   // erase
            Assert.DoesNotContain(module.Sent, t => t[2] == 0x07 && t[3] == 0x02);   // write
        }

        /// <summary>Answers the unlock with an identification string, as the car does.</summary>
        private sealed class UnlockRefusingModule : IDs2Link
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                Sent.Add(telegram);

                if (telegram[2] == 0x0B)
                {
                    var battery = new byte[48];
                    battery[0] = Ds2Telegram.TcuAddress;
                    battery[1] = (byte)battery.Length;
                    battery[2] = Ds2Telegram.StatusOk;
                    battery[10] = 0x7A;
                    return battery;
                }

                if (telegram[2] == 0x90)
                {
                    // Forty-six bytes of ASCII: part number, hardware, software.
                    var ident = new byte[46];
                    ident[0] = Ds2Telegram.TcuAddress;
                    ident[1] = (byte)ident.Length;
                    ident[2] = Ds2Telegram.StatusOk;
                    byte[] text = System.Text.Encoding.ASCII.GetBytes("755270029");
                    Buffer.BlockCopy(text, 0, ident, 3, text.Length);
                    return ident;
                }

                var reply = new byte[12];
                reply[0] = Ds2Telegram.TcuAddress;
                reply[1] = (byte)reply.Length;
                reply[2] = Ds2Telegram.StatusOk;
                reply[8] = 1;
                return reply;
            }
        }

        /// <summary>
        /// Leaving the module in programming mode is not a quiet failure: it
        /// holds the line, so the engine control unit stops identifying and the
        /// cable looks broken. Closing has to happen however the write ended.
        /// </summary>
        [Fact]
        public void ClosingTheSessionAsksTheModuleToIdentify()
        {
            var module = new FakeModule();

            new Gs20CalWriter(module).CloseSession();

            Assert.Contains(module.Sent, t => t[2] == 0x00);
        }

        [Fact]
        public void TheSessionIsOpenedBeforeAnythingElseIsAsked()
        {
            var module = new FakeModule();

            new Gs20CalWriter(module).Write(Gs20ChecksumTests.SyntheticCal());

            // 0x05 first: without it the module refuses the unlock that follows.
            Assert.Equal(0x05, module.Sent[0][2]);
        }

        [Fact]
        public void AWrongSizedImageIsRejectedBeforeAnyTelegram()
        {
            var module = new FakeModule();

            Assert.Throws<ArgumentException>(() =>
                new Gs20CalWriter(module).Write(new byte[0x8000]));
            Assert.Empty(module.Sent);
        }

        [Fact]
        public void TheWholeCalibrationIsCoveredInChunksOfTheAgreedSize()
        {
            var module = new FakeModule();

            new Gs20CalWriter(module).Write(Gs20ChecksumTests.SyntheticCal());

            // 64 KB at 118 bytes a telegram is about 556 writes, plus the
            // battery, unlock, erase, status and commit either side of them.
            int writes = module.Sent.Count(t => t[2] == 0x07 && t[3] == 0x02);
            Assert.InRange(writes, 550, 560);
            Assert.Contains(module.Sent, t => t[2] == 0x07 && t[3] == 0x06);   // erase
            Assert.Contains(module.Sent, t => t[2] == 0x07 && t[3] == 0x0F);   // status/commit
        }

    }

    public class Ds2TelegramTests
    {
        [Fact]
        public void AFrameCarriesItsOwnLengthAndChecksum()
        {
            byte[] telegram = Ds2Telegram.Build(Ds2Telegram.TcuAddress, new byte[] { 0x0B, 0x03 });

            Assert.Equal(new byte[] { 0x32, 0x05, 0x0B, 0x03 }, telegram.Take(4).ToArray());
            Assert.Equal(telegram.Length, telegram[1]);
            Assert.Equal(telegram[telegram.Length - 1],
                         Ds2Telegram.Checksum(telegram.AsSpan(0, telegram.Length - 1)));
        }

        [Fact]
        public void APayloadTooLongForTheLengthByteIsRejected()
        {
            Assert.Throws<ArgumentException>(() =>
                Ds2Telegram.Build(Ds2Telegram.TcuAddress, new byte[300]));
        }
    }
}

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The version a calibration names for itself, which the write gate compares
    /// against what the transmission reported.
    /// </summary>
    public class Gs20VersionTests
    {
        [Fact]
        public void TheVersionIsReadFromTheBlockPastTheChecksummedRange()
        {
            byte[] cal = Gs20ChecksumTests.SyntheticCal();
            byte[] name = System.Text.Encoding.ASCII.GetBytes("G2210_0090C0ER10");
            // Blank the tail first so the synthetic noise cannot bleed in.
            for (int i = 0xFFC8; i < cal.Length; i++) cal[i] = 0x00;
            Buffer.BlockCopy(name, 0, cal, 0xFFC8, name.Length);

            Assert.Equal("G2210_0090C0ER10", Gs20Checksum.ReadVersion(cal));
        }

        [Fact]
        public void AnImageWithNoVersionBlockReportsNone()
        {
            byte[] cal = new byte[Gs20Checksum.CalLength];

            Assert.Null(Gs20Checksum.ReadVersion(cal));
        }

        [Theory]
        // The release is what a transmission reports as its software number.
        // The trailing "C0" is a variant marker the module never reports, so
        // comparing against it was what made a correct 90 calibration on a 90
        // transmission look like a mismatch.
        [InlineData("G2210_0090C0ER10", "90")]
        [InlineData("G2210_0089C0ER10", "89")]
        [InlineData("G2210_0100C0ER10", "100")]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("no-underscore", null)]
        [InlineData("G2210_ABCDC0ER10", null)]
        public void TheReleaseIsTheDigitsAfterTheUnderscore(string version, string expected)
        {
            Assert.Equal(expected, Gs20Checksum.ReadRelease(version));
        }

        [SkippableTheory]
        [InlineData("gs20_7552700_90_partial_after.bin", "G2210_0090C0ER10")]
        [InlineData("gs20_7544721_89_partial.bin", "G2210_0089C0ER10")]
        public void RealCalibrationsNameTheirOwnSoftware(string name, string expected)
        {
            string dir = Environment.GetEnvironmentVariable("GS20_CAL_DIR");
            Skip.IfNot(!string.IsNullOrEmpty(dir) && Directory.Exists(dir), "Set GS20_CAL_DIR.");
            string path = Path.Combine(dir, name);
            Skip.IfNot(File.Exists(path), name + " is not in GS20_CAL_DIR.");

            Assert.Equal(expected, Gs20Checksum.ReadVersion(File.ReadAllBytes(path)));
        }
    }
}

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The supply voltage read, whose offset was found by measuring a real
    /// transmission rather than inferred: command 0x0B 0x03 answers with a
    /// 48-byte block and the reading sits at offset 10.
    /// </summary>
    public class Gs20BatteryTests
    {
        private sealed class VoltageModule : IDs2Link
        {
            private readonly byte _raw;
            public VoltageModule(byte raw) { _raw = raw; }

            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                // The block a real GS20 returned, trimmed to what matters.
                var reply = new byte[48];
                reply[0] = Ds2Telegram.TcuAddress;
                reply[1] = (byte)reply.Length;
                reply[2] = Ds2Telegram.StatusOk;
                reply[10] = _raw;
                return reply;
            }
        }

        [Fact]
        public void TheVoltageIsReadFromOffsetTen()
        {
            // 0x7A is what a parked car measured, repeatedly, at 12.44 V.
            decimal volts = new Gs20CalWriter(new VoltageModule(0x7A)).ReadBatteryVolts();

            Assert.InRange(volts, 12.4m, 12.5m);
        }

        [Fact]
        public void AFlatBatteryReadsLowRatherThanPlausible()
        {
            decimal volts = new Gs20CalWriter(new VoltageModule(0x64)).ReadBatteryVolts();

            Assert.InRange(volts, 10.1m, 10.3m);
        }
    }
}
