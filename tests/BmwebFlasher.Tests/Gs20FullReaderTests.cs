using System;
using System.Collections.Generic;
using System.Linq;
using BmwebFlasher;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The subcode-8 read. The routine it talks to has never run on hardware,
    /// so these tests pin the wire format to what the patched firmware expects:
    ///
    ///     32 09 06 08 SEG AH AL N xor   ->   32 (N+4) A0 [N bytes] xor
    ///
    /// Getting the request shape wrong is the one failure that would look like
    /// "the patch does not work" while actually being a bug on this side.
    /// </summary>
    public class Gs20FullReaderTests
    {
        /// <summary>
        /// A module carrying the patched program. Decodes subcode 8 exactly as
        /// the routine does -- segment byte plus a big-endian 16-bit offset --
        /// and serves from a synthetic 512 KB flash.
        /// </summary>
        private sealed class PatchedModule : IDs2Link
        {
            public readonly byte[] Flash = new byte[0x80000];   // 0x080000..0x0FFFFF
            public readonly List<byte[]> Sent = new List<byte[]>();

            public PatchedModule()
            {
                // Recognisable content, plus a believable boot vector table.
                for (int i = 0; i < Flash.Length; i++) Flash[i] = (byte)((i * 31) & 0xFF);
                for (int i = 0; i < 128; i++)
                {
                    Flash[i * 4 + 0] = 0xFA;
                    Flash[i * 4 + 1] = 0x0A;
                }
            }

            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                Sent.Add(telegram);

                // Well-formed framing is a precondition for the module looking at it.
                Assert.Equal(telegram.Length, telegram[1]);
                Assert.Equal(telegram[telegram.Length - 1],
                             Ds2Telegram.Checksum(telegram.AsSpan(0, telegram.Length - 1)));

                if (telegram[2] != 0x06 || telegram[3] != 0x08)
                    return new byte[] { Ds2Telegram.TcuAddress, 4, Ds2Telegram.StatusError, 0 };

                int segment = telegram[4];
                int offset = (telegram[5] << 8) | telegram[6];
                int length = telegram[7];
                int address = (segment << 16) | offset;
                int index = address - 0x080000;

                if (index < 0 || index + length > Flash.Length)
                    return new byte[] { Ds2Telegram.TcuAddress, 4, Ds2Telegram.StatusError, 0 };

                var reply = new byte[length + 4];
                reply[0] = Ds2Telegram.TcuAddress;
                reply[1] = (byte)(length + 4);
                reply[2] = Ds2Telegram.StatusOk;
                Buffer.BlockCopy(Flash, index, reply, 3, length);
                reply[reply.Length - 1] =
                    Ds2Telegram.Checksum(reply.AsSpan(0, reply.Length - 1));
                return reply;
            }

            public void Dispose() { }
        }

        /// <summary>Stock firmware: answers B0 to anything but a calibration read.</summary>
        private sealed class StockModule : IDs2Link
        {
            public byte[] Transfer(byte[] telegram, int timeoutMs) =>
                new byte[] { Ds2Telegram.TcuAddress, 4, Ds2Telegram.StatusError, 0 };

            public void Dispose() { }
        }

        [Fact]
        public void RequestUsesTheDocumentedSubcode8Shape()
        {
            var module = new PatchedModule();
            new Gs20FullReader(module).Read(0x0A1234, 16);

            byte[] first = module.Sent[0];

            // 32 09 06 08 SEG AH AL N xor
            Assert.Equal(9, first.Length);
            Assert.Equal(Ds2Telegram.TcuAddress, first[0]);
            Assert.Equal(9, first[1]);
            Assert.Equal(0x06, first[2]);
            Assert.Equal(0x08, first[3]);
            Assert.Equal(0x0A, first[4]);           // segment
            Assert.Equal(0x12, first[5]);           // offset high
            Assert.Equal(0x34, first[6]);           // offset low
            Assert.Equal(16, first[7]);             // length
            Assert.Equal(Ds2Telegram.Checksum(first.AsSpan(0, 8)), first[8]);
        }

        [Theory]
        [InlineData(0x080000, 0x100)]   // boot block
        [InlineData(0x090000, 0x100)]   // calibration
        [InlineData(0x0A0000, 0x100)]   // program
        [InlineData(0x0CFF00, 0x40)]    // high program
        public void ReadsMatchTheModulesFlash(int address, int length)
        {
            var module = new PatchedModule();
            byte[] got = new Gs20FullReader(module).Read(address, length);

            Assert.Equal(length, got.Length);
            Assert.Equal(module.Flash.Skip(address - 0x080000).Take(length).ToArray(), got);
        }

        [Fact]
        public void ReadsTheWholeBootBlockCorrectly()
        {
            var module = new PatchedModule();
            byte[] got = new Gs20FullReader(module)
                .Read(Gs20FullReader.BootAddress, Gs20FullReader.BootLength);

            Assert.Equal(Gs20FullReader.BootLength, got.Length);
            Assert.Equal(module.Flash.Take(Gs20FullReader.BootLength).ToArray(), got);
        }

        [Fact]
        public void NoRequestCrossesA16KPageBoundary()
        {
            var module = new PatchedModule();
            new Gs20FullReader(module).Read(0x08 * 0x10000, 0x9000);

            foreach (byte[] t in module.Sent)
            {
                int address = (t[4] << 16) | (t[5] << 8) | t[6];
                int length = t[7];
                int startPage = address / 0x4000;
                int endPage = (address + length - 1) / 0x4000;
                Assert.Equal(startPage, endPage);
            }
        }

        [Fact]
        public void NoRequestExceedsTheTelegramLengthCeiling()
        {
            var module = new PatchedModule();
            new Gs20FullReader(module).Read(0x0A0000, 0x800);

            foreach (byte[] t in module.Sent)
            {
                Assert.True(t[7] <= Gs20FullReader.ReadChunk);
                Assert.True(t[7] >= 1);
                // The reply must still fit DS2's single length byte.
                Assert.True(t[7] + 4 <= byte.MaxValue);
            }
        }

        [Fact]
        public void ProbeAcceptsAPatchedModule()
        {
            Assert.Null(new Gs20FullReader(new PatchedModule()).Probe());
        }

        [Fact]
        public void ProbeExplainsAStockModuleRatherThanJustFailing()
        {
            string problem = new Gs20FullReader(new StockModule()).Probe();

            Assert.NotNull(problem);
            Assert.Contains("not have the patched program", problem);
        }

        [Fact]
        public void ProbeRejectsAModuleWhoseBootBlockIsNotAVectorTable()
        {
            var module = new PatchedModule();
            module.Flash[0] = 0x00;     // no JMPS where the reset vector belongs

            string problem = new Gs20FullReader(module).Probe();

            Assert.NotNull(problem);
            Assert.Contains("wrong address", problem);
        }

        [Fact]
        public void AStockModuleFailsTheReadWithAnExplanation()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new Gs20FullReader(new StockModule()).Read(0x080000, 16));

            Assert.Contains("not have the patched program", ex.Message);
        }

        [Fact]
        public void ProgressReachesOneHundred()
        {
            var module = new PatchedModule();
            int last = -1;
            var progress = new Progress<int>(p => last = p);

            // Progress<T> posts to the sync context, so drive it synchronously.
            var reader = new Gs20FullReader(module);
            var seen = new List<int>();
            reader.Read(0x080000, 0x400, new SyncProgress(seen.Add));

            Assert.NotEmpty(seen);
            Assert.Equal(100, seen[seen.Count - 1]);
            GC.KeepAlive(progress);
            GC.KeepAlive(last);
        }

        [Fact]
        public void ReadReportsEveryChunkThroughTheNoteCallback()
        {
            var notes = new List<string>();
            var module = new PatchedModule();

            new Gs20FullReader(module, notes.Add).Read(0x080000, 0x200);

            // The note is what the UI writes into the session log, so it has to
            // say what was read and from where.
            Assert.Contains(notes, n => n.Contains("512") && n.Contains("080000"));
        }

        [Fact]
        public void ProbeReportsWhatItSawThroughTheNoteCallback()
        {
            var notes = new List<string>();

            new Gs20FullReader(new PatchedModule(), notes.Add).Probe();

            // The probe's reply is the first evidence the patch is live, so it
            // belongs in the log verbatim.
            Assert.Contains(notes, n => n.Contains("subcode 8 answered") &&
                                        n.Contains("FA"));
        }

        [Fact]
        public void AFailedReadNamesTheAddressItFailedAt()
        {
            // A module that answers the probe but then refuses: the failure
            // message has to say where it stopped, or a partial read is
            // impossible to diagnose from the log alone.
            var module = new FlakyModule(failFromAddress: 0x080100);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new Gs20FullReader(module).Read(0x080000, 0x400));

            // Chunks are 123 bytes, so the first refused request starts at the
            // chunk boundary at or past the failure point, not exactly on it.
            // What matters is that the message names a real address in range.
            Assert.Matches(@"A read at 0x08[0-9A-F]{4} failed", ex.Message);
            Assert.Contains("after 3 attempts", ex.Message);
        }

        /// <summary>Answers normally until the given address, then refuses.</summary>
        private sealed class FlakyModule : IDs2Link
        {
            private readonly PatchedModule _good = new PatchedModule();
            private readonly int _failFrom;

            public FlakyModule(int failFromAddress) { _failFrom = failFromAddress; }

            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                int address = (telegram[4] << 16) | (telegram[5] << 8) | telegram[6];
                if (address >= _failFrom)
                    return new byte[] { Ds2Telegram.TcuAddress, 4, Ds2Telegram.StatusRefused, 0 };
                return _good.Transfer(telegram, timeoutMs);
            }

            public void Dispose() { }
        }

        /// <summary>Reports on the calling thread, so a test can assert on it.</summary>
        private sealed class SyncProgress : IProgress<int>
        {
            private readonly Action<int> _report;
            public SyncProgress(Action<int> report) { _report = report; }
            public void Report(int value) => _report(value);
        }
    }
}
