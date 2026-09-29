using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BmwebFlasher
{
    /// <summary>
    /// BMW's own MS45 exchange files from SP-Daten: a .0PA carries a program,
    /// a .0DA carries a calibration (Daten).
    ///
    /// Both are Intel HEX text under a commented header, with two habits of
    /// their own:
    ///
    ///   - every 64 KB block opens with an extended SEGMENT record (type 02)
    ///     followed by an extended LINEAR record (type 04), and the two add up:
    ///     address = linear * 0x10000 + segment * 0x10 + record offset;
    ///   - the last data record of each block is marked type 0x10 rather
    ///     than 00. It is ordinary data.
    ///
    /// A .0PA holds the whole MPC internal flash (0x000000-0x06FFFF) and the
    /// external program area (0x2060000-0x20FFF3F, less a gap in its header).
    /// It holds NO calibration and no boot area. A .0DA holds the calibration
    /// partition (0x2040000-0x205CFFF).
    ///
    /// Verified against program 7561382A.0PA (0044570LO02S): its MPC and
    /// program bytes match full reads of a stock DME exactly, and P7561517.0DA
    /// decodes to the matching stock calibration partial.
    /// </summary>
    public static class Ms45ExchangeFile
    {
        public const int FullFlashLength = 0x100000;
        public const int MpcLength = 0x70000;
        public const int CalibrationLength = 0x1D000;

        private const uint ExternalBase = 0x2000000;
        private const int ProgramStart = 0x60000;
        private const int ProgramEnd = 0xFFF40;          // exclusive
        private const int CalibrationStart = 0x40000;

        public sealed class Program
        {
            /// <summary>1 MB external image: program area filled in, everything else 0xFF.</summary>
            public byte[] Flash;
            public byte[] Mpc;
            public string Reference;
        }

        public sealed class Calibration
        {
            /// <summary>The 0x1D000-byte calibration partial.</summary>
            public byte[] Data;
            public string Reference;
        }

        /// <summary>
        /// The project token a program and its calibrations share, such as
        /// 457O0L for program 0044570LO02S. The program carries it at 0x60302
        /// of the external image, a calibration at offset 8. It is what ties
        /// a .0DA to its .0PA: checked against BMW's own assembly table
        /// (MDS451.DAT), every data file listed under a program carries that
        /// program's token, and none carries the other program's.
        /// </summary>
        public static string ProgramProjectToken(byte[] flash) => Token(flash, 0x60302);

        public static string CalibrationProjectToken(byte[] calibration) => Token(calibration, 8);

        private static string Token(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 6) return null;
            var chars = new char[6];
            for (int i = 0; i < 6; i++)
            {
                byte b = data[offset + i];
                if (b < 0x20 || b > 0x7E) return null;
                chars[i] = (char)b;
            }
            return new string(chars);
        }

        /// <summary>True when the calibration was built for the program in this external image.</summary>
        public static bool IsMatchingPair(byte[] flash, byte[] calibration)
        {
            string program = ProgramProjectToken(flash);
            return program != null && program == CalibrationProjectToken(calibration);
        }

        public static bool IsProgramFile(string path) => HasExtension(path, ".0PA");

        public static bool IsDataFile(string path) => HasExtension(path, ".0DA");

        private static bool HasExtension(string path, string extension) =>
            !string.IsNullOrEmpty(path) &&
            string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

        public static Program DecodeProgram(string path) => DecodeProgramText(ReadText(path));

        public static Calibration DecodeCalibration(string path) => DecodeCalibrationText(ReadText(path));

        // The headers are Windows-1252 (German umlauts). Latin-1 reads every
        // byte without needing a code page provider, and the records are ASCII.
        private static string ReadText(string path) => File.ReadAllText(path, Encoding.Latin1);

        public static Program DecodeProgramText(string text)
        {
            Parsed parsed = Parse(text);

            var program = new Program
            {
                Flash = Filled(FullFlashLength),
                Mpc = Filled(MpcLength),
                Reference = parsed.Reference,
            };

            var mpcSeen = new bool[MpcLength];
            var flashSeen = new bool[FullFlashLength];

            foreach (var (address, data) in parsed.Records)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    uint a = address + (uint)i;
                    if (a < MpcLength)
                    {
                        program.Mpc[a] = data[i];
                        mpcSeen[a] = true;
                    }
                    else if (a >= ExternalBase + ProgramStart && a < ExternalBase + ProgramEnd)
                    {
                        program.Flash[a - ExternalBase] = data[i];
                        flashSeen[a - ExternalBase] = true;
                    }
                    else
                    {
                        throw new InvalidDataException(
                            "This is not an MS45 program file: it has data at 0x" + a.ToString("X") +
                            ", outside the MPC and the external program area.");
                    }
                }
            }

            for (int i = 0; i < MpcLength; i++)
                if (!mpcSeen[i])
                    throw new InvalidDataException(
                        "The program file is incomplete: the MPC has no data at 0x" + i.ToString("X") + ".");

            // The header has a gap at 0x600B4-0x600FF; everything from 0x60100
            // up must be there.
            for (int i = ProgramStart + 0x100; i < ProgramEnd; i++)
                if (!flashSeen[i])
                    throw new InvalidDataException(
                        "The program file is incomplete: the external program has no data at 0x" +
                        i.ToString("X") + ".");
            if (!flashSeen[ProgramStart])
                throw new InvalidDataException("The program file has no program header.");

            return program;
        }

        public static Calibration DecodeCalibrationText(string text)
        {
            Parsed parsed = Parse(text);

            var calibration = new Calibration
            {
                Data = Filled(CalibrationLength),
                Reference = parsed.Reference,
            };

            var seen = new bool[CalibrationLength];
            uint start = ExternalBase + CalibrationStart;

            foreach (var (address, data) in parsed.Records)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    uint a = address + (uint)i;
                    if (a < start || a >= start + CalibrationLength)
                        throw new InvalidDataException(
                            "This is not an MS45 data file: it has data at 0x" + a.ToString("X") +
                            ", outside the calibration.");
                    calibration.Data[a - start] = data[i];
                    seen[a - start] = true;
                }
            }

            // The header has a gap at 0x1B4-0x1FF; the calibration proper
            // starts at 0x200.
            for (int i = 0x200; i < CalibrationLength; i++)
                if (!seen[i])
                    throw new InvalidDataException(
                        "The data file is incomplete: no data at calibration offset 0x" + i.ToString("X") + ".");
            if (!seen[0])
                throw new InvalidDataException("The data file has no calibration header.");

            return calibration;
        }

        private static byte[] Filled(int length)
        {
            var data = new byte[length];
            Array.Fill(data, (byte)0xFF);
            return data;
        }

        private sealed class Parsed
        {
            public string Reference;
            public List<(uint Address, byte[] Data)> Records = new List<(uint, byte[])>();
        }

        private static Parsed Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            var parsed = new Parsed();
            uint linear = 0, segment = 0;
            bool ended = false;
            int lineNumber = 0;

            using var reader = new StringReader(text);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '$')
                {
                    // "$REFERENZ 0044570LO02S E": the reference, then a check character.
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0] == "$REFERENZ")
                        parsed.Reference = parts[1];
                    continue;
                }

                if (line[0] != ':') continue;   // header comments

                if (ended)
                    throw new InvalidDataException("Line " + lineNumber + ": data after the end record.");

                byte[] record = Unhex(line, lineNumber);
                if (record.Length < 5 || record.Length != record[0] + 5)
                    throw new InvalidDataException("Line " + lineNumber + ": record length does not match.");

                int sum = 0;
                foreach (byte b in record) sum += b;
                if ((sum & 0xFF) != 0)
                    throw new InvalidDataException("Line " + lineNumber + ": record checksum is wrong.");

                int count = record[0];
                uint offset = (uint)((record[1] << 8) | record[2]);
                byte type = record[3];

                switch (type)
                {
                    case 0x00:
                    case 0x10:
                        var data = new byte[count];
                        Buffer.BlockCopy(record, 4, data, 0, count);
                        parsed.Records.Add((linear + segment + offset, data));
                        break;
                    case 0x01:
                        ended = true;
                        break;
                    case 0x02:
                        if (count != 2)
                            throw new InvalidDataException("Line " + lineNumber + ": bad segment record.");
                        segment = (uint)((record[4] << 8) | record[5]) << 4;
                        break;
                    case 0x04:
                        if (count != 2)
                            throw new InvalidDataException("Line " + lineNumber + ": bad linear address record.");
                        linear = (uint)((record[4] << 8) | record[5]) << 16;
                        break;
                    default:
                        throw new InvalidDataException(
                            "Line " + lineNumber + ": unknown record type 0x" + type.ToString("X2") + ".");
                }
            }

            if (parsed.Records.Count == 0)
                throw new InvalidDataException("The file has no data records.");
            if (!ended)
                throw new InvalidDataException("The file is cut short: it has no end record.");

            return parsed;
        }

        private static byte[] Unhex(string line, int lineNumber)
        {
            int digits = line.Length - 1;
            if (digits < 2 || (digits & 1) != 0)
                throw new InvalidDataException("Line " + lineNumber + ": odd number of hex digits.");

            var bytes = new byte[digits / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(line.AsSpan(1 + 2 * i, 2), NumberStyles.AllowHexSpecifier,
                                   CultureInfo.InvariantCulture, out bytes[i]))
                    throw new InvalidDataException("Line " + lineNumber + ": not hexadecimal.");
            }
            return bytes;
        }
    }
}
