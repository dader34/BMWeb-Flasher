using System;
using System.Threading;

namespace BmwebFlasher
{
    /// <summary>
    /// Reads any region of a GS20 over raw DS2, using the patched firmware's
    /// subcode-8 read.
    ///
    /// Stock firmware answers <c>06</c> from a descriptor table that only
    /// covers the calibration, so a request for the boot block or the program
    /// comes back <c>B0</c>. A patched module carries an extra routine reached
    /// by <c>06 08</c> that takes a segment plus a 16-bit offset and reads it
    /// through DPP1, which puts the whole 24-bit space in range.
    ///
    ///     32 09 06 08 SEG AH AL N xor   ->   32 (N+4) A0 [N bytes] xor
    ///
    /// The routine is unproven on hardware: it has been verified against an
    /// emulator and the C166 instruction set manual, never on a live module.
    /// A module without the patch answers <c>B0</c> to subcode 8, which is what
    /// <see cref="Probe"/> looks for, so calling that first distinguishes
    /// "not patched" from "patched but broken".
    /// </summary>
    public sealed class Gs20FullReader
    {
        /// <summary>Boot block: 64 KB at 0x080000, the region a bad flash destroys.</summary>
        public const int BootAddress = 0x080000;
        public const int BootLength = 0x10000;

        /// <summary>Program: 256 KB at 0x0A0000.</summary>
        public const int ProgramAddress = 0x0A0000;
        public const int ProgramLength = 0x40000;

        /// <summary>The whole 512 KB image, boot through the erased tail.</summary>
        public const int FullAddress = 0x080000;
        public const int FullLength = 0x80000;

        /// <summary>
        /// Bytes per telegram. The reply is [addr][len][status][data][xor], and
        /// DS2 counts the whole telegram in one byte, so the ceiling is 251.
        /// The calibration reader settled on 123 against a live module; the
        /// same figure is used here rather than pushing a routine that has
        /// never run on silicon.
        /// </summary>
        public const int ReadChunk = 123;

        /// <summary>
        /// The routine walks DPP1, which maps one 16 KB page at a time. It
        /// advances the page itself when a request crosses a boundary, but the
        /// page-crossing path is the least-tested part of it, so requests are
        /// trimmed to stop at a boundary and the crossing never happens.
        /// </summary>
        private const int PageSize = 0x4000;

        private const int TimeoutMs = 3000;
        private const int Retries = 3;
        private const int RetryDelayMs = 100;

        private readonly IDs2Link _link;
        private readonly Action<string> _note;

        public Gs20FullReader(IDs2Link link, Action<string> note = null)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _note = note ?? (_ => { });
        }

        /// <summary>
        /// Asks for a few bytes of the boot block and reports what came back.
        /// Cheap, and it separates an unpatched module from a broken routine
        /// before a long read starts.
        /// </summary>
        /// <returns>null when the module answered correctly, otherwise why not.</returns>
        public string Probe(CancellationToken cancel = default)
        {
            byte[] data;
            try
            {
                data = ReadChunk_(BootAddress, 8, cancel);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            if (data.Length != 8)
                return "The module returned " + data.Length + " bytes where 8 were asked for.";

            // A C167 reset vector table is JMPS (0xFA) every four bytes. Stock
            // GS20 boot blocks start FA 0A 00 1E; anything else means the read
            // reached something other than the boot block.
            if (data[0] != 0xFA || data[4] != 0xFA)
                return "The module answered, but 0x080000 reads " +
                       Ds2Telegram.ToHex(data) +
                       " where a boot block starts with FA at 0x00 and 0x04. " +
                       "The read reached the wrong address.";

            _note("subcode 8 answered: 0x080000 = " + Ds2Telegram.ToHex(data));
            return null;
        }

        /// <summary>
        /// Reads a region. Every chunk is placed at the offset it was asked for
        /// rather than appended, so a short reply cannot shift what follows.
        /// </summary>
        public byte[] Read(int address, int length,
                           IProgress<int> progress = null,
                           CancellationToken cancel = default)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

            var image = new byte[length];
            int offset = 0;

            while (offset < length)
            {
                cancel.ThrowIfCancellationRequested();

                int want = Math.Min(ReadChunk, length - offset);

                // Stop at the next 16 KB boundary rather than reading over it.
                int absolute = address + offset;
                int toPageEnd = PageSize - (absolute % PageSize);
                want = Math.Min(want, toPageEnd);

                byte[] chunk = ReadChunk_(absolute, want, cancel);

                if (chunk.Length != want)
                    throw new InvalidOperationException(
                        "The transmission returned " + chunk.Length + " bytes at 0x" +
                        absolute.ToString("X6") + " where " + want + " were asked for.");

                Buffer.BlockCopy(chunk, 0, image, offset, want);
                offset += want;
                progress?.Report((int)(offset * 100L / length));
            }

            _note("read " + image.Length + " bytes from 0x" + address.ToString("X6"));
            return image;
        }

        /// <summary>
        /// One chunk. Unlike the stock <c>06</c> read, which takes a 32-bit
        /// address, subcode 8 takes a segment byte and a 16-bit offset within
        /// it -- the form the routine's DPP1 arithmetic expects.
        /// </summary>
        private byte[] ReadChunk_(int address, int length, CancellationToken cancel)
        {
            if (length < 1 || length > ReadChunk)
                throw new ArgumentOutOfRangeException(nameof(length));

            byte segment = (byte)(address >> 16);
            ushort offset = (ushort)address;

            byte[] payload =
            {
                0x06, 0x08,
                segment,
                (byte)(offset >> 8), (byte)offset,
                (byte)length,
            };
            byte[] telegram = Ds2Telegram.Build(Ds2Telegram.TcuAddress, payload);

            Exception last = null;
            for (int attempt = 0; attempt < Retries; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    byte[] reply = _link.Transfer(telegram, TimeoutMs);

                    byte? status = Ds2Telegram.Status(reply);
                    if (status != Ds2Telegram.StatusOk)
                    {
                        // B0 to subcode 8 is the specific, useful case: the
                        // module is talking, it just has stock firmware.
                        if (status == Ds2Telegram.StatusError)
                            throw new InvalidOperationException(
                                "The transmission refused subcode 8 at 0x" +
                                address.ToString("X6") + " (status B0). This module does " +
                                "not have the patched program -- stock firmware answers " +
                                "only calibration reads.");

                        throw new InvalidOperationException(
                            "The transmission refused a read at 0x" + address.ToString("X6") +
                            ": " + Ds2Telegram.ToHex(reply, 12));
                    }

                    // [address][length][status][data ...][checksum]
                    int available = reply.Length - 4;
                    if (available < length)
                        throw new InvalidOperationException(
                            "A read at 0x" + address.ToString("X6") + " returned only " +
                            Math.Max(available, 0) + " of " + length + " bytes.");

                    var data = new byte[length];
                    Buffer.BlockCopy(reply, 3, data, 0, length);
                    return data;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    last = ex;
                    if (attempt + 1 < Retries) Thread.Sleep(RetryDelayMs);
                }
            }

            throw new InvalidOperationException(
                "A read at 0x" + address.ToString("X6") + " failed after " + Retries +
                " attempts: " + last?.Message, last);
        }
    }
}
