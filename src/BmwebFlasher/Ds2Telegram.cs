using System;
using System.Text;

namespace BmwebFlasher
{
    /// <summary>
    /// A DS2 telegram: <c>[address][length][payload...][checksum]</c>, where the
    /// length counts the whole telegram and the checksum is a XOR of every byte
    /// before it. The length lives in a single byte, which is what caps a write
    /// payload at 118 data bytes.
    ///
    /// This type only builds and parses bytes. It never touches a port, so the
    /// framing can be tested on its own.
    /// </summary>
    public static class Ds2Telegram
    {
        /// <summary>DS2 address of the transmission.</summary>
        public const byte TcuAddress = 0x32;

        /// <summary>DS2 address of the engine control unit.</summary>
        public const byte DmeAddress = 0x12;

        // Status byte, at offset 2 of a response.
        public const byte StatusOk = 0xA0;
        public const byte StatusBusy = 0xA1;
        public const byte StatusRefused = 0xA2;
        public const byte StatusError = 0xB0;

        /// <summary>Wraps a payload in the address/length/checksum frame.</summary>
        public static byte[] Build(byte address, ReadOnlySpan<byte> payload)
        {
            // address + length + payload + checksum
            int total = payload.Length + 3;
            if (total > byte.MaxValue)
                throw new ArgumentException(
                    "A DS2 telegram carries its length in one byte; this payload needs " +
                    total + " bytes.", nameof(payload));

            var telegram = new byte[total];
            telegram[0] = address;
            telegram[1] = (byte)total;
            payload.CopyTo(telegram.AsSpan(2));
            telegram[total - 1] = Checksum(telegram.AsSpan(0, total - 1));
            return telegram;
        }

        /// <summary>XOR of every byte, which is how DS2 checks a telegram.</summary>
        public static byte Checksum(ReadOnlySpan<byte> bytes)
        {
            byte sum = 0;
            foreach (byte b in bytes) sum ^= b;
            return sum;
        }

        /// <summary>The status byte a response carries, or null if it is too short.</summary>
        public static byte? Status(ReadOnlySpan<byte> response) =>
            response.Length > 2 ? response[2] : (byte?)null;

        /// <summary>
        /// The sub-status a write or commit reports at offset 8. One means the
        /// operation finished; the rest are distinct flash faults.
        /// </summary>
        public static byte? SubStatus(ReadOnlySpan<byte> response) =>
            response.Length > 8 ? response[8] : (byte?)null;

        /// <summary>What a flash sub-status means, for a log or a dialog.</summary>
        public static string DescribeSubStatus(byte subStatus) => subStatus switch
        {
            1 => "complete",
            2 => "flash fault 2 (write rejected)",
            9 => "flash fault 9",
            10 => "flash fault 10",
            11 => "flash fault 11",
            12 => "flash fault 12",
            13 => "flash fault 13",
            14 => "flash fault 14",
            15 => "flash fault 15",
            _ => "unknown sub-status 0x" + subStatus.ToString("X2"),
        };

        /// <summary>Hex, for the log.</summary>
        public static string ToHex(ReadOnlySpan<byte> bytes, int limit = int.MaxValue)
        {
            var text = new StringBuilder();
            int shown = Math.Min(bytes.Length, limit);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(bytes[i].ToString("X2"));
            }
            if (shown < bytes.Length)
                text.Append(" ... (" + (bytes.Length - shown) + " more)");
            return text.ToString();
        }
    }
}
