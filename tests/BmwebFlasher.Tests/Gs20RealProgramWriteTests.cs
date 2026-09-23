using System;
using System.IO;
using System.Linq;
using BmwebFlasher;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// A full dry-run write of se93's real 256 KB program through the writer,
    /// against a flash model that behaves like the AM29F400BB. Skipped when
    /// the dump is not on this machine.
    /// </summary>
    public class Gs20RealProgramWriteTests
    {
        // A 512 KB read from a real module. Skipped when it is not present.
        private static string DumpPath =>
            Environment.GetEnvironmentVariable("BMWEB_FULL_READ")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "e46bins", "GS20-gearbox",
                "TCU_full_080000_20260922_232222.bin");

        private sealed class Am29F400BB : IDs2Link
        {
            private static readonly (int Start, int Len)[] Sectors =
            {
                (0x080000,0x4000),(0x084000,0x2000),(0x086000,0x2000),(0x088000,0x8000),
                (0x090000,0x10000),(0x0A0000,0x10000),(0x0B0000,0x10000),
                (0x0C0000,0x10000),(0x0D0000,0x10000),(0x0E0000,0x10000),(0x0F0000,0x10000),
            };
            public readonly byte[] Flash;
            public int EraseCount, WriteCount, BytesWritten;
            public Am29F400BB(byte[] initial) { Flash = initial.ToArray(); }

            public byte[] Transfer(byte[] t, int timeoutMs)
            {
                int addr = (t[4] << 16) | (t[5] << 8) | t[6];
                if (t[3] == 0x06)
                {
                    var s = Sectors.First(x => addr >= x.Start && addr < x.Start + x.Len);
                    for (int i = 0; i < s.Len; i++) Flash[s.Start - 0x80000 + i] = 0xFF;
                    EraseCount++;
                }
                else if (t[3] == 0x02)
                {
                    int len = t[7];
                    for (int i = 0; i < len; i++) Flash[addr - 0x80000 + i] &= t[8 + i];
                    WriteCount++; BytesWritten += len;
                }
                var body = new byte[] { Ds2Telegram.StatusOk,0,0,0,0,0,1,0 };
                var r = new byte[body.Length + 3];
                r[0] = Ds2Telegram.TcuAddress; r[1] = (byte)r.Length;
                Buffer.BlockCopy(body, 0, r, 2, body.Length);
                r[^1] = Ds2Telegram.Checksum(r.AsSpan(0, r.Length - 1));
                return r;
            }
            public void Dispose() { }
        }

        [SkippableFact]
        public void WritingSe93sRealProgramReproducesItExactly()
        {
            Skip.IfNot(File.Exists(DumpPath), "se93's dump is not on this machine");
            byte[] full = File.ReadAllBytes(DumpPath);
            Assert.Equal(0x80000, full.Length);

            byte[] program = full.Skip(0x20000).Take(0x40000).ToArray();
            var module = new Am29F400BB(full);

            var log = new System.Collections.Generic.List<string>();
            new Gs20ProgramWriter(module, log.Add).Write(program);

            // the program region came back byte-for-byte
            byte[] onModule = module.Flash.Skip(0x20000).Take(0x40000).ToArray();
            Assert.Equal(program, onModule);

            // and nothing outside it moved
            Assert.Equal(full.Take(0x20000).ToArray(), module.Flash.Take(0x20000).ToArray());
            Assert.Equal(full.Skip(0x60000).ToArray(), module.Flash.Skip(0x60000).ToArray());

            Assert.Equal(4, module.EraseCount);
            Assert.True(Gs20ProgramChecksum.Verify(onModule));
        }
    }
}
