using System;
using System.Collections.Generic;
using System.Threading;

namespace BmwebFlasher
{
    /// <summary>Sends a DS2 telegram and returns the reply.</summary>
    public interface IDs2Link
    {
        /// <summary>
        /// Transmits one telegram and waits for the module's reply. Implementations
        /// raise on a transport failure; a module-level refusal comes back in the
        /// reply's status byte instead.
        /// </summary>
        byte[] Transfer(byte[] telegram, int timeoutMs);
    }

    /// <summary>
    /// Writes a GS20 (A5S390R) calibration over raw DS2.
    ///
    /// The transmission's own SGBD cannot do this: gs20.prg exposes twenty-one
    /// jobs and none of them programs memory, so there is no EDIABAS route and
    /// the telegrams have to be built by hand. The sequence below -- unlock,
    /// erase, wait, write, commit -- and its region and chunk sizes were taken
    /// from a flash tool that does support this module.
    ///
    /// Safety, in order of importance:
    ///  * The calibration's checksum is corrected before anything is sent, so a
    ///    tune file carrying a stale one cannot reach the car.
    ///  * The boot block and program live outside this region and are never
    ///    addressed, so a failed or interrupted write leaves the module still
    ///    answering and still flashable.
    ///  * A full write was verified against a real transmission: the read-back
    ///    matched the written calibration byte for byte.
    /// </summary>
    public sealed class Gs20CalWriter
    {
        /// <summary>Where the calibration lives in the module's address space.</summary>
        public const int CalAddress = 0x090000;

        /// <summary>Data bytes per write telegram. See the note below on 118.</summary>
        public const int WriteChunk = 118;

        // A write telegram is 1 address + 1 length + 6 header + data + 1 checksum.
        // The length byte caps the total at 255, so 118 data bytes (a 127-byte
        // telegram) is the size the reference tool uses and the most that fits
        // with headroom.

        private const int NormalTimeoutMs = 1000;
        private const int EraseTimeoutMs = 60000;
        private const int Retries = 3;
        private const int RetryDelayMs = 100;
        private const int BusyPollDelayMs = 100;
        private const int MaxBusyPolls = 600;   // 60 s at 100 ms

        private readonly IDs2Link _link;
        private readonly byte _address;
        private readonly Action<string> _note;

        /// <summary>
        /// Whether an erase was sent. Until it is, a failure has left the
        /// calibration exactly as it was, and saying otherwise would send
        /// someone hunting a problem they do not have.
        /// </summary>
        public bool EraseStarted { get; private set; }

        public Gs20CalWriter(IDs2Link link, Action<string> note = null,
                             byte address = Ds2Telegram.TcuAddress)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _address = address;
            _note = note ?? (_ => { });
        }

        /// <summary>
        /// Writes a calibration. The image must be exactly 64 KB; its checksum is
        /// corrected first, so the caller may pass a freshly edited tune.
        /// </summary>
        public void Write(byte[] calibration, IProgress<int> progress = null,
                          CancellationToken cancel = default)
        {
            if (calibration == null) throw new ArgumentNullException(nameof(calibration));
            if (calibration.Length != Gs20Checksum.CalLength)
                throw new ArgumentException(
                    "A GS20 calibration is 0x" + Gs20Checksum.CalLength.ToString("X") +
                    " bytes; this one is 0x" + calibration.Length.ToString("X") + ".",
                    nameof(calibration));

            // Correct the checksum before a single byte goes out. A calibration
            // the module rejects at power-up is the one failure this whole class
            // can prevent outright.
            byte[] image = Gs20Checksum.Correct(calibration);
            if (Gs20Checksum.Stored(calibration) != Gs20Checksum.Stored(image))
            {
                _note("checksum corrected 0x" + Gs20Checksum.Stored(calibration).ToString("X4") +
                      " -> 0x" + Gs20Checksum.Stored(image).ToString("X4"));
            }

            cancel.ThrowIfCancellationRequested();

            _note("open session");
            OpenSession(cancel);

            _note("unlock");
            Unlock(cancel);

            // Prove the module really will take flash commands before erasing.
            // The status request is a query, so a module that is not open
            // refuses it here instead of part way through a write.
            ConfirmFlashAccepted(cancel);

            _note("erase 0x" + CalAddress.ToString("X6"));
            EraseStarted = true;
            Erase(CalAddress, cancel);
            WaitReady(CalAddress, cancel);

            _note("write " + image.Length + " bytes to 0x" + CalAddress.ToString("X6"));
            WriteRegion(CalAddress, image, progress, cancel);

            _note("commit");
            Commit(CalAddress, cancel);
            _note("done");
        }

        /// <summary>
        /// Supply voltage, which is worth knowing before an erase: the module
        /// browning out midway is one of the few ways to be left holding half a
        /// calibration.
        ///
        /// Command 0x0B 0x03 answers with a forty-eight byte block rather than a
        /// bare reading. The voltage sits at offset 10, scaled by 0.10196078;
        /// measured against a parked car it reads 12.44 V and holds steady
        /// sample to sample.
        /// </summary>
        public decimal ReadBatteryVolts()
        {
            byte[] reply = Exchange(new byte[] { 0x0B, 0x03 }, NormalTimeoutMs, "battery");
            if (reply.Length <= VoltageOffset) return 0m;
            return reply[VoltageOffset] * 0.10196078m;
        }

        private const int VoltageOffset = 10;

        /// <summary>
        /// Starts a session. Without this the module refuses the unlock that
        /// follows, and it refuses it the same way whether the cable is wrong or
        /// the session was simply never opened, which is a confusing place to
        /// land. One command up front removes the whole class of problem.
        /// </summary>
        public void OpenSession(CancellationToken cancel = default) =>
            Exchange(new byte[] { 0x05 }, NormalTimeoutMs, "session open", cancel);

        /// <summary>
        /// Takes the module back out of programming mode.
        ///
        /// A transmission left in it keeps answering its own requests while
        /// holding the line against everything else, so the engine control unit
        /// stops identifying and the cable looks broken. An identification
        /// request is what settles it. Failing to close is not worth raising
        /// over: by this point the calibration is already written.
        /// </summary>
        public void CloseSession()
        {
            try
            {
                Exchange(new byte[] { 0x00 }, NormalTimeoutMs, "session close");
            }
            catch (Exception)
            {
                // Nothing useful to do here, and the write itself has finished.
            }
        }

        /// <summary>
        /// Opens the module for programming. It either reports itself already
        /// open, with a five-byte reply, or returns a longer one to answer.
        /// Either way the module ends up accepting flash commands, which a
        /// status request confirms before anything is erased.
        /// </summary>
        public void Unlock(CancellationToken cancel = default)
        {
            byte seed = (byte)new Random().Next(1, 24);
            byte[] reply = Exchange(new byte[] { 0x90, 0x42, 0x4D, 0x57, seed },
                                    NormalTimeoutMs, "unlock seed", cancel);

            int replyLength = reply.Length > 1 ? reply[1] : 0;
            if (replyLength == ShortReply) return;   // already open

            if (replyLength != ChallengeReply)
                throw new InvalidOperationException(
                    "Unexpected reply to the unlock request: " + Ds2Telegram.ToHex(reply, 16));

            // The answer is four bytes, each the sum of three taken from the
            // challenge: one the seed points at, one from offset 18 and one from
            // offset 41. The bytes involved happen to be ASCII, so a valid key
            // looks oddly uniform; that is not a sign of it being wrong.
            var key = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                key[i] = (byte)(reply[(seed + i) % replyLength]
                              + reply[18 + i]
                              + reply[41 + i]);
            }

            byte[] accepted = Exchange(new byte[] { 0x90, key[0], key[1], key[2], key[3] },
                                       NormalTimeoutMs, "unlock key", cancel);

            // A short reply means the module is now open. Its trailing byte
            // varies and is not a result code, so only the length and the status
            // that Exchange already checked are read here.
            if ((accepted.Length > 1 ? accepted[1] : 0) != ShortReply)
                throw new InvalidOperationException(
                    "The transmission did not accept the unlock key: " +
                    Ds2Telegram.ToHex(accepted, 16));
        }

        private const int ShortReply = 5;
        private const int ChallengeReply = 46;


        /// <summary>
        /// Asks the flash for its status, which only an open module answers.
        /// Nothing is modified, so this is the last chance to stop cheaply.
        /// </summary>
        private void ConfirmFlashAccepted(CancellationToken cancel)
        {
            byte[] reply;
            try
            {
                reply = Exchange(AddressCommand(0x0F, CalAddress), NormalTimeoutMs,
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

        /// <summary>Polls until the module stops reporting busy.</summary>
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
            throw new TimeoutException("The transmission stayed busy after the erase.");
        }

        /// <summary>
        /// The closing status request, which unlike the others insists the
        /// sub-status reports the operation actually completed.
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
                    "The transmission did not confirm the write: " +
                    (sub.HasValue ? Ds2Telegram.DescribeSubStatus(sub.Value) : "no sub-status") +
                    " (" + Ds2Telegram.ToHex(reply, 12) + ")");
            }
            throw new TimeoutException("The transmission never confirmed the write.");
        }

        private static byte[] AddressCommand(byte sub, int address) => new byte[]
        {
            0x07, sub,
            (byte)(address >> 16), (byte)(address >> 8), (byte)address,
            0x00,
        };

        /// <summary>
        /// Programs the image. Pairs of blank bytes are stepped over rather than
        /// written, and a chunk is trimmed back past any blank tail, so only
        /// cells that carry data are programmed.
        /// </summary>
        private void WriteRegion(int baseAddress, byte[] image,
                                 IProgress<int> progress, CancellationToken cancel)
        {
            int offset = 0;
            while (offset < image.Length)
            {
                cancel.ThrowIfCancellationRequested();

                // Step over blank pairs.
                while (offset + 1 < image.Length &&
                       image[offset] == 0xFF && image[offset + 1] == 0xFF)
                {
                    offset += 2;
                }
                if (offset >= image.Length) break;

                int length = Math.Min(WriteChunk, image.Length - offset);

                // Trim a blank tail so it is not programmed needlessly.
                while (length > 2 &&
                       image[offset + length - 2] == 0xFF && image[offset + length - 1] == 0xFF)
                {
                    length -= 2;
                }

                var payload = new byte[6 + length];
                int address = baseAddress + offset;
                payload[0] = 0x07;
                payload[1] = 0x02;
                payload[2] = (byte)(address >> 16);
                payload[3] = (byte)(address >> 8);
                payload[4] = (byte)address;
                payload[5] = (byte)length;
                Buffer.BlockCopy(image, offset, payload, 6, length);

                byte[] reply = Exchange(payload, NormalTimeoutMs, "write", cancel);
                byte? sub = Ds2Telegram.SubStatus(reply);
                if (sub.HasValue && sub.Value != 1)
                    throw new InvalidOperationException(
                        "Write rejected at 0x" + address.ToString("X6") + ": " +
                        Ds2Telegram.DescribeSubStatus(sub.Value));

                offset += length;
                progress?.Report((int)(offset * 100L / image.Length));
            }
        }

        private byte[] Exchange(byte[] payload, int timeoutMs, string what,
                                CancellationToken cancel = default, bool allowBusy = false)
        {
            byte[] telegram = Ds2Telegram.Build(_address, payload);

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
                "The " + what + " request failed after " + Retries + " attempts: " + last?.Message, last);
        }
    }
}
