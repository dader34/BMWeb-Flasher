using System;
using System.Collections.Generic;
using System.Threading;

namespace BmwebFlasher
{
    /// <summary>
    /// Writes the GS20 program region (0x0A0000-0x0DFFFF) over raw DS2.
    ///
    /// This is the brick-capable path. What makes it survivable is the flash
    /// layout: the module carries an AM29F400BB, a bottom-boot part whose
    /// sector map puts the boot block in four small sectors below the
    /// calibration, and the program in four whole 64 KB sectors above it.
    ///
    ///     SA0-SA3  0x080000-0x08FFFF  boot block   (never addressed here)
    ///     SA4      0x090000-0x09FFFF  calibration  (never addressed here)
    ///     SA5-SA8  0x0A0000-0x0DFFFF  program      <- this class
    ///     SA9-SA10 0x0E0000-0x0FFFFF  unused, erased
    ///
    /// Sector erase on this part is selected by the address the command
    /// carries: the module's boot-block flash driver writes the AMD unlock
    /// cycles and then puts 0x30 at the target address. So erasing the
    /// program means four erases, one per sector, and the boot block stays
    /// intact because nothing ever names it. A failed program write therefore
    /// leaves a module that still runs its boot code.
    ///
    /// The checksum is corrected before anything is erased, so the image that
    /// reaches the module is always self-consistent.
    /// </summary>
    public sealed class Gs20ProgramWriter
    {
        /// <summary>Where the program region starts.</summary>
        public const int ProgramAddress = Gs20ProgramChecksum.ProgramAddress;

        /// <summary>How long the program region is.</summary>
        public const int ProgramLength = Gs20ProgramChecksum.ProgramLength;

        /// <summary>
        /// The flash sectors the program occupies, as (address, length). Each
        /// needs its own erase; the part has no multi-sector erase command.
        /// </summary>
        public static readonly (int Address, int Length)[] Sectors =
        {
            (0x0A0000, 0x10000),   // SA5
            (0x0B0000, 0x10000),   // SA6
            (0x0C0000, 0x10000),   // SA7
            (0x0D0000, 0x10000),   // SA8
        };

        /// <summary>Data bytes per write telegram, as the calibration writer uses.</summary>
        public const int WriteChunk = Gs20CalWriter.WriteChunk;

        private const int NormalTimeoutMs = 1000;
        private const int EraseTimeoutMs = 60000;
        private const int Retries = 3;
        private const int RetryDelayMs = 100;
        private const int BusyPollDelayMs = 100;
        private const int MaxBusyPolls = 600;

        private readonly IDs2Link _link;
        private readonly Action<string> _note;

        /// <summary>
        /// Which sectors have been erased. Until the first erase, a failure
        /// has changed nothing on the module.
        /// </summary>
        public List<int> ErasedSectors { get; } = new List<int>();

        /// <summary>Whether anything has been erased yet.</summary>
        public bool EraseStarted => ErasedSectors.Count > 0;

        public Gs20ProgramWriter(IDs2Link link, Action<string> note = null)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _note = note ?? (_ => { });
        }

        /// <summary>
        /// Erases the four program sectors and writes the image, correcting
        /// its checksum first. The image must be exactly the program region.
        /// </summary>
        public void Write(byte[] program,
                          IProgress<int> progress = null,
                          CancellationToken cancel = default)
        {
            if (program == null) throw new ArgumentNullException(nameof(program));
            if (program.Length != ProgramLength)
                throw new ArgumentException(
                    "A GS20 program region is 0x" + ProgramLength.ToString("X") +
                    " bytes; this one is 0x" + program.Length.ToString("X") + ".",
                    nameof(program));

            // Correct the checksum before anything is erased. A module that
            // ends up half-written is recoverable; one that ends up fully
            // written with a wrong checksum may not be.
            byte[] image = Gs20ProgramChecksum.Corrected(program, out ushort checksum);
            _note("program checksum 0x" + checksum.ToString("X4") +
                  (Gs20ProgramChecksum.Stored(program) == checksum
                      ? " (already correct)"
                      : " (corrected from 0x" +
                        Gs20ProgramChecksum.Stored(program).ToString("X4") + ")"));

            // Prove the module really will take flash commands before erasing
            // anything. The status request is a query: a module that is not
            // open refuses it here, where refusal costs nothing, rather than
            // at the first erase.
            ConfirmFlashAccepted(cancel);

            // Erase every sector first, then write. Erasing as we go would
            // leave a longer window where an interruption means some sectors
            // are blank and others hold the old program.
            foreach ((int address, int _) in Sectors)
            {
                cancel.ThrowIfCancellationRequested();
                _note("erase 0x" + address.ToString("X6"));
                Erase(address, cancel);
                WaitReady(address, cancel);
                ErasedSectors.Add(address);
            }

            _note("write " + image.Length + " bytes to 0x" + ProgramAddress.ToString("X6"));
            WriteRegion(ProgramAddress, image, progress, cancel);

            Commit(ProgramAddress, cancel);
            _note("program written and committed");
        }

        /// <summary>
        /// The bytes a read-back should return. Blank runs are stepped over
        /// rather than programmed, so an erased cell stays 0xFF; the caller
        /// compares against this rather than against the raw image.
        /// </summary>
        public static byte[] ExpectedOnModule(byte[] program)
        {
            byte[] image = Gs20ProgramChecksum.Corrected(program, out _);
            return image;   // 0xFF written to an erased cell leaves it 0xFF
        }

        private void ConfirmFlashAccepted(CancellationToken cancel)
        {
            byte[] reply;
            try
            {
                reply = Exchange(AddressCommand(0x0F, ProgramAddress), NormalTimeoutMs,
                                 "flash status", cancel, allowBusy: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The transmission did not answer a flash status request, so it is not open " +
                    "for programming. Nothing was erased. (" + ex.Message + ")", ex);
            }
            byte? status = Ds2Telegram.Status(reply);
            if (status != Ds2Telegram.StatusOk && status != Ds2Telegram.StatusBusy)
                throw new InvalidOperationException(
                    "The transmission refused a flash status request, so it is not open for " +
                    "programming. Nothing was erased. Reply: " + Ds2Telegram.ToHex(reply, 12));
        }

        private void Erase(int address, CancellationToken cancel) =>
            Exchange(AddressCommand(0x06, address), EraseTimeoutMs, "erase", cancel);

        /// <summary>
        /// The closing status request. Unlike the polls during the erase, this
        /// one insists the sub-status reports the operation actually finished,
        /// exactly as the calibration writer does.
        /// </summary>
        private void Commit(int address, CancellationToken cancel)
        {
            for (int poll = 0; poll < MaxBusyPolls; poll++)
            {
                cancel.ThrowIfCancellationRequested();
                byte[] reply = Exchange(AddressCommand(0x0F, address), EraseTimeoutMs,
                                        "commit", cancel, allowBusy: true);
                if (Ds2Telegram.Status(reply) == Ds2Telegram.StatusBusy)
                {
                    Thread.Sleep(BusyPollDelayMs);
                    continue;
                }

                byte? sub = Ds2Telegram.SubStatus(reply);
                if (sub == 1) return;
                throw new InvalidOperationException(
                    "The transmission did not confirm the program write: " +
                    (sub.HasValue ? Ds2Telegram.DescribeSubStatus(sub.Value) : "no sub-status") +
                    " (" + Ds2Telegram.ToHex(reply, 12) + ")");
            }
            throw new TimeoutException("The transmission never confirmed the program write.");
        }

        /// <summary>Polls the module until it stops reporting busy.</summary>
        private void WaitReady(int address, CancellationToken cancel)
        {
            for (int poll = 0; poll < MaxBusyPolls; poll++)
            {
                cancel.ThrowIfCancellationRequested();
                byte[] reply = Exchange(AddressCommand(0x0F, address), EraseTimeoutMs,
                                        "status", cancel, allowBusy: true);
                if (Ds2Telegram.Status(reply) != Ds2Telegram.StatusBusy) return;
                Thread.Sleep(BusyPollDelayMs);
            }
            throw new TimeoutException(
                "The transmission stayed busy after erasing 0x" + address.ToString("X6") + ".");
        }

        private static byte[] AddressCommand(byte sub, int address) => new byte[]
        {
            0x07, sub,
            (byte)(address >> 16), (byte)(address >> 8), (byte)address,
            0x00,
        };

        /// <summary>
        /// Programs the image. Pairs of blank bytes are stepped over rather
        /// than written, and a chunk is trimmed past any blank tail, so only
        /// cells that carry data are programmed. This matches what the
        /// calibration writer does and what the reference tool does.
        /// </summary>
        private void WriteRegion(int baseAddress, byte[] image,
                                 IProgress<int> progress, CancellationToken cancel)
        {
            int offset = 0;
            while (offset < image.Length)
            {
                cancel.ThrowIfCancellationRequested();

                while (offset + 1 < image.Length &&
                       image[offset] == 0xFF && image[offset + 1] == 0xFF)
                {
                    offset += 2;
                }
                if (offset >= image.Length) break;

                int length = Math.Min(WriteChunk, image.Length - offset);

                while (length > 2 &&
                       image[offset + length - 2] == 0xFF && image[offset + length - 1] == 0xFF)
                {
                    length -= 2;
                }

                // A chunk must not straddle a sector boundary: the module
                // programs within one sector at a time.
                int sectorEnd = SectorEndOf(baseAddress + offset);
                int toSectorEnd = sectorEnd - (baseAddress + offset) + 1;
                length = Math.Min(length, toSectorEnd);

                WriteChunkAt(baseAddress + offset, image, offset, length, cancel);

                offset += length;
                progress?.Report((int)(offset * 100L / image.Length));
            }
        }

        /// <summary>The last address of the sector a given address falls in.</summary>
        private static int SectorEndOf(int address)
        {
            foreach ((int start, int len) in Sectors)
                if (address >= start && address < start + len) return start + len - 1;
            throw new ArgumentOutOfRangeException(
                nameof(address),
                "0x" + address.ToString("X6") + " is outside the program region.");
        }

        private void WriteChunkAt(int address, byte[] image, int offset, int length,
                                  CancellationToken cancel)
        {
            var payload = new byte[6 + length];
            payload[0] = 0x07;
            payload[1] = 0x02;
            payload[2] = (byte)(address >> 16);
            payload[3] = (byte)(address >> 8);
            payload[4] = (byte)address;
            payload[5] = (byte)length;
            Buffer.BlockCopy(image, offset, payload, 6, length);

            byte[] reply = Exchange(payload, NormalTimeoutMs, "write", cancel);
            byte? status = Ds2Telegram.Status(reply);
            if (status != Ds2Telegram.StatusOk)
                throw new InvalidOperationException(
                    "The transmission refused a write at 0x" + address.ToString("X6") +
                    ": " + Ds2Telegram.ToHex(reply, 12));
        }

        private byte[] Exchange(byte[] payload, int timeoutMs, string what,
                                CancellationToken cancel, bool allowBusy = false)
        {
            byte[] telegram = Ds2Telegram.Build(Ds2Telegram.TcuAddress, payload);
            Exception last = null;
            for (int attempt = 0; attempt < Retries; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    byte[] reply = _link.Transfer(telegram, timeoutMs);
                    byte? status = Ds2Telegram.Status(reply);

                    if (status == Ds2Telegram.StatusOk) return reply;
                    if (allowBusy && status == Ds2Telegram.StatusBusy) return reply;

                    // Anything else is a refusal. Accepting it would let a
                    // rejected erase look like a successful one, and the write
                    // would then go into unerased flash.
                    throw new InvalidOperationException(
                        "The transmission refused the " + what + " request (status " +
                        (status.HasValue ? "0x" + status.Value.ToString("X2") : "missing") +
                        "): " + Ds2Telegram.ToHex(reply, 12));
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    last = ex;
                    if (attempt + 1 < Retries) Thread.Sleep(RetryDelayMs);
                }
            }
            throw new InvalidOperationException(
                "The " + what + " failed after " + Retries + " attempts: " + last?.Message,
                last);
        }
    }
}
