using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BmwebFlasher
{
    /// <summary>
    /// A telegram/job trace for read and (especially) flash operations. When a
    /// program flash stalls, knowing which job it stalled on and the last bytes
    /// on the wire is the difference between "re-flash and recover" and a brick,
    /// so this captures every job the app runs: its name, argument, resulting
    /// JOB_STATUS, and the request/response telegrams EDIABAS exposes as
    /// _TEL_AUFTRAG / _TEL_ANTWORT.
    ///
    /// A session is a single logical operation (one flash, one read). Start()
    /// opens a timestamped file; every ExecuteJob writes one entry; Stop()
    /// closes it. Logging is best-effort and never throws into the caller.
    /// </summary>
    public static class FlashLog
    {
        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static string _path;

        // What the history keeps of a session: when it began, what it was,
        // its first note (the module and options) and the last status line.
        private static DateTime _started;
        private static string _operation, _firstNote, _lastStatus;
        private static string _emulator;         // the emulated module the session ran on, or null for a car
        // The outcome so far: "ok" once a success line was seen, "failed"
        // once a failure was, "ended" otherwise. Judged as the statuses
        // arrive rather than from the last one, because the re-identify
        // after a flash reports neutral lines ("Programming status: ...")
        // that used to turn a finished flash into "ended".
        private static string _result;
        // The folder the session's written images were copied to, if any.
        private static string _filesDir;

        /// <summary>Directory where session logs are written.</summary>
        public static string LogDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bmweb-flasher", "logs");

        /// <summary>The current session's log file, or null when not logging.</summary>
        public static string CurrentPath { get { lock (Gate) return _path; } }

        public static bool IsActive { get { lock (Gate) return _writer != null; } }

        /// <summary>
        /// Begins a log session named after the operation (e.g. "flash-program").
        /// Returns the file path, or null if the log could not be opened.
        /// </summary>
        public static string Start(string operation)
        {
            lock (Gate)
            {
                Stop();
                _started = DateTime.Now;
                _operation = operation;
                _firstNote = null;
                _lastStatus = null;
                _result = "ended";
                _filesDir = null;
                try
                {
                    Directory.CreateDirectory(LogDir);
                    string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" +
                                  Sanitize(operation) + ".log";
                    _path = Path.Combine(LogDir, name);
                    _writer = new StreamWriter(_path, append: false) { AutoFlush = true };
                    _writer.WriteLine("# BMWeb Flasher log");
                    _writer.WriteLine("# operation: " + operation);
                    _writer.WriteLine("# started:   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    _emulator = Emulation.Label;
                    if (_emulator != null)
                        _writer.WriteLine("# target:    " + _emulator + " -- an emulator, not a car");
                    _writer.WriteLine();
                    return _path;
                }
                catch (Exception)
                {
                    _writer = null;
                    _path = null;
                    return null;
                }
            }
        }

        public static void Stop()
        {
            FlashHistory.Entry entry = null;
            lock (Gate)
            {
                if (_writer == null) return;
                try
                {
                    _writer.WriteLine();
                    _writer.WriteLine("# ended: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch (Exception) { }

                entry = new FlashHistory.Entry
                {
                    Started = _started,
                    Seconds = Math.Round((DateTime.Now - _started).TotalSeconds, 1),
                    Operation = _operation,
                    Vin = Global.VIN,
                    Module = Global.HW_Ref,
                    Details = _firstNote,
                    Status = _lastStatus,
                    Result = _result,
                    Log = _path,
                    Files = _filesDir,
                    Emulator = _emulator,
                };
                _writer = null;
                _path = null;
            }
            // Outside the lock: the history raises an event the window listens to.
            FlashHistory.Append(entry);
        }

        /// <summary>Free-text note (a phase marker, an error, a decision).</summary>
        public static void Note(string text)
        {
            lock (Gate)
            {
                if (_writer == null) return;
                if (_firstNote == null) _firstNote = text;
                try { _writer.WriteLine(Stamp() + text); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Keeps a copy of an image the session is about to write, when the
        /// setting is on: in a folder of its own under the app's "flashed"
        /// folder, named after the session, which the history entry then
        /// points at. Best effort, like the log.
        /// </summary>
        public static void Attach(string fileName, byte[] data)
        {
            if (data == null || !Global.KeepFlashedFiles) return;
            lock (Gate)
            {
                if (_writer == null) return;
                try
                {
                    if (_filesDir == null)
                    {
                        _filesDir = Path.Combine(FlashHistory.FlashedDir,
                            _started.ToString("yyyyMMdd-HHmmss") + "_" + Sanitize(_operation) +
                            (string.IsNullOrEmpty(Global.VIN) ? string.Empty : "_" + Sanitize(Global.VIN)));
                        Directory.CreateDirectory(_filesDir);
                    }
                    string path = Path.Combine(_filesDir, fileName);
                    File.WriteAllBytes(path, data);
                    _writer.WriteLine(Stamp() + "copy of the image written: " + path);
                }
                catch (Exception ex)
                {
                    try { _writer.WriteLine(Stamp() + "could not keep a copy of " + fileName + ": " + ex.Message); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// The status line the app shows, kept as the session's outcome. Every
        /// flash path reports how it ended this way, so the history needs no
        /// call of its own at each of them.
        /// </summary>
        public static void Status(string text)
        {
            lock (Gate)
            {
                if (_writer == null || string.IsNullOrEmpty(text)) return;
                if (text.StartsWith("Logging to ")) return;
                _lastStatus = text;
                string judged = FlashHistory.Judge(text);
                if (judged == "failed") _result = "failed";
                else if (judged == "ok" && _result != "failed") _result = "ok";
            }
        }

        /// <summary>
        /// One job entry: name, arg summary, status, and the request/response
        /// telegrams. Called from ExecuteJob after the job runs.
        /// </summary>
        public static void Job(string name, string argSummary, string status,
            byte[] request, byte[] response)
        {
            lock (Gate)
            {
                if (_writer == null) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.Append(Stamp());
                    sb.Append(name);
                    if (!string.IsNullOrEmpty(argSummary))
                        sb.Append(" [").Append(argSummary).Append(']');
                    sb.Append(" -> ").Append(string.IsNullOrEmpty(status) ? "(no status)" : status);
                    _writer.WriteLine(sb.ToString());

                    if (request != null && request.Length > 0)
                        _writer.WriteLine("    tx: " + Hex(request));
                    if (response != null && response.Length > 0)
                        _writer.WriteLine("    rx: " + Hex(response));
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Starts a session and stops it on dispose, so callers with many early
        /// returns can `using (FlashLog.Session("flash-program")) { ... }`.
        /// </summary>
        public static IDisposable Session(string operation, out string path)
        {
            path = Start(operation);
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose() => Stop();
        }

        private static string Stamp() =>
            DateTime.Now.ToString("HH:mm:ss.fff") + "  ";

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            return sb.ToString();
        }

        // Cap very long telegrams (a flash block is up to ~0xFD data bytes plus
        // framing) so the log stays readable; note the elision.
        private static string Hex(byte[] data)
        {
            const int max = 64;
            int n = Math.Min(data.Length, max);
            var sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            if (data.Length > max)
                sb.Append(" ... (" + data.Length + " bytes)");
            return sb.ToString();
        }
    }
}
