using System;
using System.Threading;

namespace BmwebFlasher
{
    /// <summary>
    /// Reads a GS20 calibration over raw DS2.
    ///
    /// This exists because the EDIABAS route does not assemble a correct image:
    /// a dump taken through SPEICHER_LESEN comes back with its 16 KB blocks in
    /// the wrong places, carrying a duplicate header at 0x4000, and repeats an
    /// earlier dump byte for byte even after the calibration on the module has
    /// changed. Reading the module directly avoids all of that, and it is the
    /// same transport the write already uses, so one path is exercised twice.
    ///
    /// The module will also move to a faster line rate for the transfer, which
    /// takes a 64 KB read from minutes to seconds.
    /// </summary>
    public sealed class Gs20CalReader
    {
        /// <summary>Where the calibration lives in the module's address space.</summary>
        public const int CalAddress = 0x090000;

        /// <summary>
        /// Bytes per read telegram. The reference tool uses 123, which keeps the
        /// reply inside the single byte DS2 spends on a telegram's length.
        /// </summary>
        public const int ReadChunk = 123;

        /// <summary>
        /// The module reads within 16 KB pages and wraps to the start of the
        /// region when a request crosses one, so the bytes past the boundary
        /// come back as the calibration header instead of the data that lives
        /// there. Measured on the car: a chunk straddling 0x094000 returns
        /// "FF FF 00 02 96 02 61 31" from that point on, while a chunk starting
        /// exactly on the boundary reads correctly. Every request is therefore
        /// trimmed to end at a page boundary.
        /// </summary>
        private const int PageSize = 0x4000;

        private const int TimeoutMs = 3000;
        private const int Retries = 3;
        private const int RetryDelayMs = 100;

        private readonly IDs2Link _link;
        private readonly Action<string> _note;

        public Gs20CalReader(IDs2Link link, Action<string> note = null)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _note = note ?? (_ => { });
        }

        /// <summary>
        /// Reads the whole calibration. Every chunk is placed at the offset it
        /// was asked for rather than appended, so a short or repeated reply
        /// cannot quietly shift the rest of the image.
        /// </summary>
        public byte[] Read(IProgress<int> progress = null, CancellationToken cancel = default)
        {
            var image = new byte[Gs20Checksum.CalLength];
            int offset = 0;

            while (offset < image.Length)
            {
                cancel.ThrowIfCancellationRequested();

                int length = Math.Min(ReadChunk, image.Length - offset);

                // Stop at the next page boundary rather than reading over it.
                int toPageEnd = PageSize - (offset % PageSize);
                length = Math.Min(length, toPageEnd);

                byte[] chunk = ReadChunk_(CalAddress + offset, length, cancel);

                if (chunk.Length != length)
                    throw new InvalidOperationException(
                        "The transmission returned " + chunk.Length + " bytes at 0x" +
                        (CalAddress + offset).ToString("X6") + " where " + length +
                        " were asked for.");

                Buffer.BlockCopy(chunk, 0, image, offset, length);
                offset += length;
                progress?.Report((int)(offset * 100L / image.Length));
            }

            _note("read " + image.Length + " bytes, checksum 0x" +
                  Gs20Checksum.Stored(image).ToString("X4") +
                  (Gs20Checksum.Verify(image) ? " (valid)" : " (does NOT match the data)"));

            return image;
        }

        /// <summary>One chunk. The read command takes a 32-bit address.</summary>
        private byte[] ReadChunk_(int address, int length, CancellationToken cancel)
        {
            byte[] payload =
            {
                0x06,
                (byte)(address >> 24), (byte)(address >> 16),
                (byte)(address >> 8), (byte)address,
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

                    if (Ds2Telegram.Status(reply) != Ds2Telegram.StatusOk)
                        throw new InvalidOperationException(
                            "The transmission refused a read at 0x" + address.ToString("X6") +
                            ": " + Ds2Telegram.ToHex(reply, 12));

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
