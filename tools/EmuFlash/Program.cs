// Drives the DME through EDIABAS jobs from the command line: the same calls
// the app makes, without the window. Written to exercise the emulated DME
// (ms45-emu, tools/kline.py) but it is plain EDIABAS and would talk to a car.
//
//   EmuFlash <port> <ecupath> ident
//   EmuFlash <port> <ecupath> job <NAME> [<string arg>]
//   EmuFlash <port> <ecupath> read <segment> <start> <length> <out.bin>
//   EmuFlash <port> <ecupath> flash-program <flash.bin> <mpc.bin>
//   EmuFlash <port> <ecupath> flash-data <flash.bin>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EdiabasLib;

namespace BmwebFlasher.Tools
{
    static class Program
    {
        static int Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (args.Length < 3) { Console.Error.WriteLine("usage: EmuFlash <port> <ecupath> <command> ..."); return 2; }
            string port = args[0], ecuPath = args[1], cmd = args[2];
            using EdiabasNet ediabas = Start(port, ecuPath);
            switch (cmd)
            {
                case "ident":
                    return Job(ediabas, "IDENT", "") && Job(ediabas, "DIAGNOSEPROTOKOLL_LESEN", "") ? 0 : 1;
                case "job":
                    return Job(ediabas, args[3], args.Length > 4 ? args[4] : "") ? 0 : 1;
                case "read":
                    return ReadMemory(ediabas, args[3], Convert.ToUInt32(args[4], 16), Convert.ToUInt32(args[5], 16), args[6]) ? 0 : 1;
                case "flash-program":
                    return FlashProgram(ediabas, File.ReadAllBytes(args[3]), File.ReadAllBytes(args[4])) ? 0 : 1;
                case "flash-data":
                    return FlashData(ediabas, File.ReadAllBytes(args[3])) ? 0 : 1;
            }
            Console.Error.WriteLine("unknown command " + cmd);
            return 2;
        }

        static EdiabasNet Start(string port, string ecuPath)
        {
            var ediabas = new EdiabasNet();
            var edInterface = new EdInterfaceObd { ComPort = port };
            ediabas.EdInterfaceClass = edInterface;
            ediabas.SetConfigProperty("EcuPath", ecuPath);
            string trace = Environment.GetEnvironmentVariable("EMUFLASH_TRACE");
            if (!string.IsNullOrEmpty(trace))
            {
                ediabas.SetConfigProperty("TracePath", trace);
                ediabas.SetConfigProperty("IfhTrace", "3");
                ediabas.SetConfigProperty("ApiTrace", "3");
            }
            ediabas.ResultsRequests = string.Empty;
            ediabas.ResolveSgbdFile("ms450ds0.prg");
            return ediabas;
        }

        // ---- the app's sequences ------------------------------------------------
        static bool FlashProgram(EdiabasNet ediabas, byte[] flash, byte[] mpc)
        {
            var cs = new Checksums_Signatures();
            if (!SecurityAccess(ediabas)) return false;
            byte[] toFlash = cs.CorrectProgramChecksums(flash, mpc);
            byte[] signed = cs.SignMS45Program(toFlash, mpc);
            byte[] program = signed.Skip(0x60000).Take(0x9FF40).ToArray();

            Console.WriteLine("erase program 0x2060000 len 0xA0000");
            if (!Erase(ediabas, 0xA0000, 0x2060000)) return false;
            Console.WriteLine("write external program");
            if (!FlashBlock(ediabas, program, 0x2060000, 0x20FFF3F)) return false;
            Console.WriteLine("write internal MPC");
            if (!FlashBlock(ediabas, mpc, 0, 0x6FFFF)) return false;

            byte[] cal = flash.Skip(0x40000).Take(0x1D000).ToArray();
            cal = cs.SignMS45Parameters(cs.CorrectParameterChecksums(cal));
            Console.WriteLine("erase calibration 0x2040000 len 0x20000");
            if (!Erase(ediabas, 0x20000, 0x2040000)) return false;
            Console.WriteLine("write calibration");
            if (!FlashBlock(ediabas, cal, 0x2040000, 0x205CFFF)) return false;

            if (!Finish(ediabas, "Programm", false)) return false;
            return Finish(ediabas, "Daten", true);
        }

        static bool FlashData(EdiabasNet ediabas, byte[] flash)
        {
            var cs = new Checksums_Signatures();
            if (!SecurityAccess(ediabas)) return false;
            byte[] cal = flash.Skip(0x40000).Take(0x1D000).ToArray();
            cal = cs.SignMS45Parameters(cs.CorrectParameterChecksums(cal));
            if (!Erase(ediabas, 0x20000, 0x2040000)) return false;
            if (!FlashBlock(ediabas, cal, 0x2040000, 0x205CFFF)) return false;
            return Finish(ediabas, "Daten", true);
        }

        static bool SecurityAccess(EdiabasNet ediabas)
        {
            var cs = new Checksums_Signatures();
            if (!Job(ediabas, "seriennummer_lesen", "")) return false;
            byte[] serialReply = Result<byte[]>("_TEL_ANTWORT", ediabas);
            byte[] serial = serialReply.Skip(serialReply.Length - 5).Take(4).ToArray();
            byte[] userId = new byte[4];
            new Random().NextBytes(userId);
            if (!Job(ediabas, "authentisierung_zufallszahl_lesen", "3;0x" + BitConverter.ToUInt32(userId.Reverse().ToArray(), 0).ToString("X"))) return false;
            byte[] seed = Result<byte[]>("ZUFALLSZAHL", ediabas);
            if (!Job(ediabas, "authentisierung_start", cs.GetSecurityAccessMessage(userId, serial, seed))) return false;
            if (!Job(ediabas, "diagnose_mode", "ECUPM;PC115200")) return false;
            if (!Job(ediabas, "SET_PARAMETER", ";115200")) return false;
            if (!Job(ediabas, "ACCESS_TIMING_PARAMETER", "00;120;24;240;00")) return false;
            return Job(ediabas, "SET_PARAMETER", ";115200;;15");
        }

        static bool Erase(EdiabasNet ediabas, uint length, uint start)
        {
            byte[] cmd = new byte[22];
            cmd[0] = 1; cmd[4] = 0xFE;
            BitConverter.GetBytes(start).CopyTo(cmd, 17);
            BitConverter.GetBytes(length).CopyTo(cmd, 13);
            return Job(ediabas, "flash_loeschen", cmd);
        }

        static bool FlashBlock(EdiabasNet ediabas, byte[] data, uint start, uint end)
        {
            uint origin = start, length = end - start + 1;
            byte[] addressSet = new byte[22];
            addressSet[0] = 1; addressSet[21] = 3;
            BitConverter.GetBytes(start).CopyTo(addressSet, 17);
            BitConverter.GetBytes(length).CopyTo(addressSet, 13);
            byte[] header = new byte[21];
            int seg = 0xFD;
            header[0] = 1; header[13] = (byte)seg;
            if (!Job(ediabas, "flash_schreiben_adresse", addressSet)) return false;
            int n = 0;
            while (length > 0)
            {
                if (length < seg) { seg = (int)length; header[13] = (byte)seg; }
                BitConverter.GetBytes(start).CopyTo(header, 17);
                byte[] arg = header.Concat(data.Skip((int)(start - origin)).Take(seg)).Concat(new byte[] { 3 }).ToArray();
                if (!Job(ediabas, "flash_schreiben", arg, quiet: true))
                {
                    Console.WriteLine("flash_schreiben failed at 0x" + start.ToString("X"));
                    Job(ediabas, "STEUERGERAETE_RESET", "");
                    return false;
                }
                start += (uint)seg; length -= (uint)seg;
                if (++n % 100 == 0) Console.WriteLine("  at 0x" + start.ToString("X"));
            }
            return Job(ediabas, "flash_schreiben_ende", addressSet);
        }

        static bool Finish(EdiabasNet ediabas, string area, bool reset)
        {
            if (!Job(ediabas, "diagnose_mode", "DEFAULT;PC9600")) return false;
            if (!Job(ediabas, "SET_PARAMETER", ";9600")) return false;
            if (!Job(ediabas, "FLASH_PROGRAMMIER_STATUS_LESEN", "")) return false;
            if (!Job(ediabas, "FLASH_SIGNATUR_PRUEFEN", area + ";64")) { Job(ediabas, "STEUERGERAETE_RESET", ""); return false; }
            if (!Job(ediabas, "FLASH_PROGRAMMIER_STATUS_LESEN", "")) return false;
            return !reset || Job(ediabas, "STEUERGERAETE_RESET", "");
        }

        static bool ReadMemory(EdiabasNet ediabas, string segment, uint start, uint length, string path)
        {
            var all = new List<byte>();
            uint pos = start;
            while (pos < start + length)
            {
                uint n = Math.Min(0x80, start + length - pos);
                if (!Job(ediabas, "speicher_lesen_ascii", segment + ";" + pos + ";" + n, quiet: true)) return false;
                byte[] reply = Result<byte[]>("_TEL_ANTWORT", ediabas);
                all.AddRange(reply.Skip(reply.Length - 1 - (int)n).Take((int)n));
                pos += n;
            }
            File.WriteAllBytes(path, all.ToArray());
            Console.WriteLine("read " + all.Count + " bytes to " + path);
            return true;
        }

        // ---- plumbing -----------------------------------------------------------
        static bool Job(EdiabasNet ediabas, string job, string arg, bool quiet = false)
        {
            ediabas.ArgString = arg;                      // shares ArgBinary's buffer: set one only
            return Run(ediabas, job, arg, quiet);
        }

        static bool Job(EdiabasNet ediabas, string job, byte[] arg, bool quiet = false)
        {
            ediabas.ArgBinary = arg;
            return Run(ediabas, job, arg.Length + " bytes", quiet);
        }

        static bool Run(EdiabasNet ediabas, string job, string argText, bool quiet)
        {
            try { ediabas.ExecuteJob(job); }
            catch (Exception ex)
            {
                Console.WriteLine(job + "(" + argText + "): EXCEPTION " + ex.Message);
                return false;
            }
            string status = Result<string>("JOB_STATUS", ediabas) ?? "";
            if (!quiet || status != "OKAY")
            {
                Console.WriteLine(job + "(" + argText + ") -> " + status);
                foreach (var set in ediabas.ResultSets ?? new List<Dictionary<string, EdiabasNet.ResultData>>())
                    foreach (var kv in set)
                        if (kv.Value.OpData is string s && kv.Key != "JOB_STATUS") Console.WriteLine("    " + kv.Key + " = " + s);
                        else if (kv.Value.OpData is byte[] b && (b.Length <= 64 || !quiet)) Console.WriteLine("    " + kv.Key + " = " + BitConverter.ToString(b, 0, Math.Min(b.Length, 64)).Replace("-", " "));
            }
            return status == "OKAY";
        }

        static T Result<T>(string name, EdiabasNet ediabas) where T : class
        {
            if (ediabas.ResultSets == null) return null;
            T result = null;
            foreach (var set in ediabas.ResultSets)
                foreach (var kv in set)
                    if (kv.Value.Name == name && kv.Value.OpData is T t) result = t;
            return result;
        }
    }
}
