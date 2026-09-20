using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace BmwebFlasher
{
    /// <summary>
    /// Speaks DS2 straight down the cable, without EDIABAS.
    ///
    /// The transmission's SGBD has no job that programs memory, so a calibration
    /// write has to build its own telegrams; that is the only reason this exists.
    /// Everything else in the app should keep going through EdiabasLib.
    ///
    /// The K line is half duplex: whatever we transmit comes back to us first.
    /// Each exchange therefore reads and discards its own echo before waiting on
    /// the module's reply. A reply is read by taking its length byte and then
    /// waiting for exactly that many bytes, which is what makes a read
    /// deterministic instead of a guess at how long to wait.
    /// </summary>
    public sealed class Ds2SerialLink : IDs2Link, IDisposable
    {
        // DS2 on the K line starts at 9600 8E1. A module can be asked to move to
        // a faster rate for a bulk transfer; see SwitchBaud.
        public const int DefaultBaud = 9600;

        /// <summary>The quickest rate the transmission offers, thirteen times 9600.</summary>
        public const int FastBaud = 125000;

        /// <summary>
        /// A pause between telegrams.
        ///
        /// Thirty milliseconds looks wasteful next to a round trip, and reads do
        /// survive at two: a 64 KB transfer halves, from around forty seconds to
        /// twenty, and still verifies. Writes do not. At two milliseconds the
        /// baud change is not acted on, and the module answers the request that
        /// follows with its identification string instead of a real reply, which
        /// reads as a module that will not unlock. Restoring thirty makes the
        /// baud change work again.
        ///
        /// Reads are not worth a second timing rule for: one value that works
        /// everywhere is easier to trust than two that differ by path.
        /// </summary>
        private const int InterTelegramDelayMs = 30;

        private readonly SerialPort _port;
        private readonly Action<string> _trace;

        public Ds2SerialLink(string portName, Action<string> trace = null)
        {
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("No serial port was given.", nameof(portName));

            _trace = trace ?? (_ => { });
            _port = new SerialPort(portName, DefaultBaud, Parity.Even, 8, StopBits.One)
            {
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
            };
            _port.Open();
            DiscardStaleBytes();
        }

        /// <summary>The rate the link is currently running at.</summary>
        public int Baud => _port.BaudRate;

        /// <summary>
        /// Asks the module to move to another rate, then follows it.
        ///
        /// The request is acknowledged at the current rate and only then does
        /// either side change, so the order matters: send, read the reply, and
        /// switch afterwards. A module that refuses is left where it was.
        ///
        /// The command is 0x91 followed by the rate as a 24-bit big-endian
        /// number, which is why 9600 reads as 00 25 80 and 125000 as 01 E8 48.
        /// The trailing byte is a per-module flag; the transmission wants 1.
        /// </summary>
        public void SwitchBaud(int baud, byte moduleFlag = 1)
        {
            if (baud == _port.BaudRate) return;

            byte[] payload =
            {
                0x91,
                (byte)(baud >> 16), (byte)(baud >> 8), (byte)baud,
                moduleFlag,
            };

            int previous = _port.BaudRate;

            // The request may be acted on even when its acknowledgement never
            // reaches us, which would leave the module at the new rate while we
            // stayed at the old one and every later telegram came back as
            // nonsense. So a lost acknowledgement is not treated as "nothing
            // happened": both possibilities are tried and the one the module
            // actually answers on is kept.
            bool acknowledged;
            try
            {
                byte[] reply = Transfer(Ds2Telegram.Build(Ds2Telegram.TcuAddress, payload), 2000);
                acknowledged = Ds2Telegram.Status(reply) == Ds2Telegram.StatusOk;
            }
            catch (Exception)
            {
                acknowledged = false;
            }

            _port.BaseStream.Flush();
            Thread.Sleep(InterTelegramDelayMs);

            if (Settle(baud))
            {
                _trace("baud now " + baud);
                return;
            }

            if (Settle(previous))
            {
                _trace("baud stayed at " + previous);
                throw new InvalidOperationException(
                    "The module did not change to " + baud + " baud.");
            }

            throw new InvalidOperationException(
                "The module stopped answering after a baud change" +
                (acknowledged ? " it had acknowledged" : "") +
                ". Cycle the ignition and reconnect.");
        }

        /// <summary>
        /// Moves the port to a rate and checks the module answers there. Used
        /// after a baud change to find which end of the change actually took.
        /// </summary>
        private bool Settle(int baud)
        {
            _port.BaudRate = baud;
            DiscardStaleBytes();
            Thread.Sleep(InterTelegramDelayMs);

            try
            {
                // A plain ident is harmless and answers on any healthy link.
                byte[] reply = Transfer(Ds2Telegram.Build(Ds2Telegram.TcuAddress,
                                                          new byte[] { 0x00 }), 1500);
                return Ds2Telegram.Status(reply) == Ds2Telegram.StatusOk;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public byte[] Transfer(byte[] telegram, int timeoutMs)
        {
            if (telegram == null) throw new ArgumentNullException(nameof(telegram));

            DiscardStaleBytes();

            _port.Write(telegram, 0, telegram.Length);

            // Our own transmission loops back first; drop exactly that much.
            ReadExactly(new byte[telegram.Length], telegram.Length, timeoutMs, "echo");

            // A reply leads with the module address and its total length.
            var header = new byte[2];
            ReadExactly(header, 2, timeoutMs, "reply header");

            int total = header[1];
            if (total < 3 || total > byte.MaxValue)
                throw new InvalidOperationException(
                    "The reply announced an impossible length of " + total + " bytes.");

            var reply = new byte[total];
            reply[0] = header[0];
            reply[1] = header[1];
            ReadExactly(reply, total - 2, timeoutMs, "reply body", offset: 2);

            byte expected = Ds2Telegram.Checksum(reply.AsSpan(0, total - 1));
            if (reply[total - 1] != expected)
                throw new InvalidOperationException(
                    "The reply's checksum is wrong (expected 0x" + expected.ToString("X2") +
                    ", got 0x" + reply[total - 1].ToString("X2") + "): " +
                    Ds2Telegram.ToHex(reply, 16));

            _trace(Ds2Telegram.ToHex(telegram, 12) + "  ->  " + Ds2Telegram.ToHex(reply, 12));

            // The module needs a breath between telegrams.
            Thread.Sleep(InterTelegramDelayMs);
            return reply;
        }

        /// <summary>
        /// Reads a fixed number of bytes or gives up. SerialPort.Read returns
        /// whatever has arrived so far, so this loops until the count is met.
        /// </summary>
        private void ReadExactly(byte[] buffer, int count, int timeoutMs, string what, int offset = 0)
        {
            var clock = Stopwatch.StartNew();
            int got = 0;
            while (got < count)
            {
                int remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0)
                    throw new TimeoutException(
                        "Timed out waiting for the " + what + " (" + got + " of " + count +
                        " bytes after " + timeoutMs + " ms). Check the cable and the ignition.");

                _port.ReadTimeout = Math.Max(remaining, 1);
                try
                {
                    int read = _port.Read(buffer, offset + got, count - got);
                    if (read <= 0) continue;
                    got += read;
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        "Timed out waiting for the " + what + " (" + got + " of " + count +
                        " bytes after " + timeoutMs + " ms). Check the cable and the ignition.");
                }
            }
        }

        private void DiscardStaleBytes()
        {
            try
            {
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
            }
            catch (Exception)
            {
                // A driver that will not flush is not a reason to abandon the
                // exchange; the length-driven read below still frames correctly.
            }
        }

        public void Dispose()
        {
            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch (Exception)
            {
                // Closing a port that the OS has already taken away is not worth
                // failing a flash over.
            }
            _port.Dispose();
        }
    }
}
