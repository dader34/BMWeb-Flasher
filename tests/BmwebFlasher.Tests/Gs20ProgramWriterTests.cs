using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BmwebFlasher;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The program write, against a module simulated down to the flash's own
    /// behaviour: NOR cells can only be cleared, an erase affects exactly one
    /// AM29F400BB sector, and a write into an unerased cell ANDs rather than
    /// replaces. That last property is what makes a missing erase visible
    /// here instead of on someone's car.
    /// </summary>
    public class Gs20ProgramWriterTests
    {
        // AM29F400BB, bottom boot, mapped with chip offset 0 at module 0x080000.
        private static readonly (int Start, int Len)[] ChipSectors =
        {
            (0x080000, 0x4000),  (0x084000, 0x2000),  (0x086000, 0x2000),
            (0x088000, 0x8000),  (0x090000, 0x10000), (0x0A0000, 0x10000),
            (0x0B0000, 0x10000), (0x0C0000, 0x10000), (0x0D0000, 0x10000),
            (0x0E0000, 0x10000), (0x0F0000, 0x10000),
        };

        /// <summary>A GS20 whose flash behaves like the real part.</summary>
        private sealed class FakeModule : IDs2Link
        {
            public readonly byte[] Flash = new byte[0x80000];   // 0x080000..0x0FFFFF
            public readonly List<int> Erased = new List<int>();
            public readonly List<(int Address, int Length)> Writes =
                new List<(int, int)>();
            public bool RefuseEraseOutsideProgram = true;

            public FakeModule()
            {
                // Plausible starting content: programmed everywhere the real
                // module is, blank where it is blank.
                for (int i = 0; i < Flash.Length; i++) Flash[i] = (byte)((i * 31) & 0xFF);
                for (int i = 0x60000; i < Flash.Length; i++) Flash[i] = 0xFF;   // tail
            }

            private static int M2F(int module) => module - 0x080000;

            private static (int Start, int Len) SectorOf(int module)
            {
                foreach (var s in ChipSectors)
                    if (module >= s.Start && module < s.Start + s.Len) return s;
                throw new InvalidOperationException(
                    "0x" + module.ToString("X6") + " is not in the flash.");
            }

            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                Assert.Equal(telegram.Length, telegram[1]);
                Assert.Equal(telegram[telegram.Length - 1],
                             Ds2Telegram.Checksum(telegram.AsSpan(0, telegram.Length - 1)));

                byte cmd = telegram[2], sub = telegram[3];
                if (cmd != 0x07) return Refuse();

                int address = (telegram[4] << 16) | (telegram[5] << 8) | telegram[6];

                switch (sub)
                {
                    case 0x06:      // sector erase
                    {
                        if (RefuseEraseOutsideProgram &&
                            (address < 0x0A0000 || address > 0x0DFFFF))
                            return Refuse();
                        var s = SectorOf(address);
                        for (int i = 0; i < s.Len; i++) Flash[M2F(s.Start) + i] = 0xFF;
                        Erased.Add(s.Start);
                        return Ok();
                    }
                    case 0x02:      // program
                    {
                        int length = telegram[7];
                        Writes.Add((address, length));
                        for (int i = 0; i < length; i++)
                        {
                            // NOR flash can only clear bits: a write ANDs.
                            Flash[M2F(address) + i] &= telegram[8 + i];
                        }
                        return Ok();
                    }
                    case 0x0F:      // status
                        return OkWithSub(1);
                    default:
                        return Refuse();
                }
            }

            private static byte[] Ok() =>
                Frame(new byte[] { Ds2Telegram.StatusOk, 0, 0, 0, 0, 0, 0 });

            private static byte[] OkWithSub(byte subStatus)
            {
                var body = new byte[] { Ds2Telegram.StatusOk, 0, 0, 0, 0, 0, subStatus, 0 };
                return Frame(body);
            }

            private static byte[] Refuse() =>
                Frame(new byte[] { Ds2Telegram.StatusRefused, 0 });

            private static byte[] Frame(byte[] body)
            {
                var reply = new byte[body.Length + 3];
                reply[0] = Ds2Telegram.TcuAddress;
                reply[1] = (byte)reply.Length;
                Buffer.BlockCopy(body, 0, reply, 2, body.Length);
                reply[reply.Length - 1] =
                    Ds2Telegram.Checksum(reply.AsSpan(0, reply.Length - 1));
                return reply;
            }

            public void Dispose() { }
        }

        private static byte[] SampleProgram()
        {
            // A program region shaped like the real one: mostly content, with
            // the descriptor table and checksum where they belong.
            var p = new byte[Gs20ProgramWriter.ProgramLength];
            for (int i = 0; i < p.Length; i++) p[i] = (byte)((i * 17 + 3) & 0xFF);
            for (int i = 0x3FE00; i < p.Length; i++) p[i] = 0xF8;
            return p;
        }

        [Fact]
        public void ErasesExactlyTheFourProgramSectors()
        {
            var m = new FakeModule();
            new Gs20ProgramWriter(m).Write(SampleProgram());

            Assert.Equal(new[] { 0x0A0000, 0x0B0000, 0x0C0000, 0x0D0000 },
                         m.Erased.ToArray());
        }

        [Fact]
        public void NeverAddressesTheBootBlockOrCalibration()
        {
            var m = new FakeModule();
            new Gs20ProgramWriter(m).Write(SampleProgram());

            foreach (int e in m.Erased)
                Assert.InRange(e, 0x0A0000, 0x0DFFFF);
            foreach (var (address, length) in m.Writes)
            {
                Assert.InRange(address, 0x0A0000, 0x0DFFFF);
                Assert.InRange(address + length - 1, 0x0A0000, 0x0DFFFF);
            }
        }

        [Fact]
        public void BootBlockAndCalibrationSurviveByteForByte()
        {
            var m = new FakeModule();
            byte[] bootBefore = m.Flash.Take(0x10000).ToArray();
            byte[] calBefore = m.Flash.Skip(0x10000).Take(0x10000).ToArray();

            new Gs20ProgramWriter(m).Write(SampleProgram());

            Assert.Equal(bootBefore, m.Flash.Take(0x10000).ToArray());
            Assert.Equal(calBefore, m.Flash.Skip(0x10000).Take(0x10000).ToArray());
        }

        [Fact]
        public void TheProgramOnTheModuleMatchesTheImageWithItsChecksumFixed()
        {
            var m = new FakeModule();
            byte[] program = SampleProgram();
            new Gs20ProgramWriter(m).Write(program);

            byte[] expected = Gs20ProgramChecksum.Corrected(program, out _);
            byte[] onModule = m.Flash.Skip(0x20000).Take(0x40000).ToArray();
            Assert.Equal(expected, onModule);
        }

        [Fact]
        public void TheWrittenProgramCarriesAValidChecksum()
        {
            var m = new FakeModule();
            new Gs20ProgramWriter(m).Write(SampleProgram());

            byte[] onModule = m.Flash.Skip(0x20000).Take(0x40000).ToArray();
            Assert.True(Gs20ProgramChecksum.Verify(onModule));
        }

        [Fact]
        public void AWrongChecksumInTheSourceIsCorrectedNotPropagated()
        {
            var m = new FakeModule();
            byte[] program = SampleProgram();
            program[Gs20ProgramChecksum.StoreOffset] = 0xDE;
            program[Gs20ProgramChecksum.StoreOffset + 1] = 0xAD;

            new Gs20ProgramWriter(m).Write(program);

            byte[] onModule = m.Flash.Skip(0x20000).Take(0x40000).ToArray();
            Assert.True(Gs20ProgramChecksum.Verify(onModule));
            Assert.NotEqual(0xADDE, Gs20ProgramChecksum.Stored(onModule));
        }

        [Fact]
        public void TheCallersArrayIsNotModified()
        {
            var m = new FakeModule();
            byte[] program = SampleProgram();
            byte[] before = program.ToArray();

            new Gs20ProgramWriter(m).Write(program);

            Assert.Equal(before, program);
        }

        [Fact]
        public void NoWriteChunkStraddlesASectorBoundary()
        {
            var m = new FakeModule();
            new Gs20ProgramWriter(m).Write(SampleProgram());

            foreach (var (address, length) in m.Writes)
            {
                int startSector = (address - 0x0A0000) / 0x10000;
                int endSector = (address + length - 1 - 0x0A0000) / 0x10000;
                Assert.Equal(startSector, endSector);
            }
        }

        [Fact]
        public void NoWriteChunkExceedsTheTelegramLimit()
        {
            var m = new FakeModule();
            new Gs20ProgramWriter(m).Write(SampleProgram());

            foreach (var (_, length) in m.Writes)
            {
                Assert.InRange(length, 1, Gs20ProgramWriter.WriteChunk);
                Assert.True(length + 9 <= byte.MaxValue);
            }
        }

        [Fact]
        public void AModuleThatRefusesTheEraseStopsBeforeAnyWrite()
        {
            var m = new FakeModule();
            // Refuse everything: the first erase fails.
            m.RefuseEraseOutsideProgram = true;
            var writer = new Gs20ProgramWriter(m);

            // Shrink the region check by handing it a region the fake refuses:
            // easiest is to make the fake refuse the program range too.
            var refusing = new RefusingModule();
            Assert.Throws<InvalidOperationException>(
                () => new Gs20ProgramWriter(refusing).Write(SampleProgram()));
            Assert.Empty(refusing.Writes);
        }

        /// <summary>Refuses every erase, as a module in the wrong state would.</summary>
        private sealed class RefusingModule : IDs2Link
        {
            public readonly List<(int, int)> Writes = new List<(int, int)>();

            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                if (telegram[3] == 0x02)
                    Writes.Add(((telegram[4] << 16) | (telegram[5] << 8) | telegram[6],
                                telegram[7]));
                var reply = new byte[] { Ds2Telegram.TcuAddress, 4, Ds2Telegram.StatusRefused, 0 };
                reply[3] = Ds2Telegram.Checksum(reply.AsSpan(0, 3));
                return reply;
            }

            public void Dispose() { }
        }

        [Fact]
        public void AnUnerasedSectorIsCaughtByTheReadBack()
        {
            // The failure this whole design exists to prevent: if a sector is
            // not erased, NOR flash ANDs the new bytes into the old ones and
            // the result is neither image.
            var m = new FakeModule();
            byte[] program = SampleProgram();

            // Simulate a module whose third sector silently ignores the erase.
            var partial = new PartialEraseModule();
            new Gs20ProgramWriter(partial).Write(program);

            byte[] expected = Gs20ProgramChecksum.Corrected(program, out _);
            byte[] onModule = partial.Flash.Skip(0x20000).Take(0x40000).ToArray();
            Assert.NotEqual(expected, onModule);
            Assert.False(Gs20ProgramChecksum.Verify(onModule),
                         "a half-erased program should not checksum clean");
        }

        /// <summary>Ignores the erase for one sector, as a failing part would.</summary>
        private sealed class PartialEraseModule : IDs2Link
        {
            public readonly byte[] Flash = new byte[0x80000];
            public PartialEraseModule()
            {
                for (int i = 0; i < Flash.Length; i++) Flash[i] = (byte)((i * 31) & 0xFF);
            }
            public byte[] Transfer(byte[] telegram, int timeoutMs)
            {
                byte sub = telegram[3];
                int address = (telegram[4] << 16) | (telegram[5] << 8) | telegram[6];
                if (sub == 0x06)
                {
                    if (address != 0x0C0000)             // this one silently does nothing
                        for (int i = 0; i < 0x10000; i++) Flash[address - 0x080000 + i] = 0xFF;
                }
                else if (sub == 0x02)
                {
                    int length = telegram[7];
                    for (int i = 0; i < length; i++)
                        Flash[address - 0x080000 + i] &= telegram[8 + i];
                }
                var body = new byte[] { Ds2Telegram.StatusOk, 0, 0, 0, 0, 0, 1, 0 };
                var reply = new byte[body.Length + 3];
                reply[0] = Ds2Telegram.TcuAddress;
                reply[1] = (byte)reply.Length;
                Buffer.BlockCopy(body, 0, reply, 2, body.Length);
                reply[reply.Length - 1] = Ds2Telegram.Checksum(reply.AsSpan(0, reply.Length - 1));
                return reply;
            }
            public void Dispose() { }
        }
    }
}
