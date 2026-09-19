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
                _writer = null;
                _path = null;
            }
        }

        /// <summary>Free-text note (a phase marker, an error, a decision).</summary>
        public static void Note(string text)
        {
            lock (Gate)
            {
                if (_writer == null) return;
                try { _writer.WriteLine(Stamp() + text); }
                catch (Exception) { }
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
