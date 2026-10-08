using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BmwebFlasher
{
    /// <summary>
    /// What was done to which car, kept on this machine only: one JSON line
    /// per logged session (flash, write, read) in history.jsonl next to
    /// settings.json. Written by <see cref="FlashLog"/> when a session ends,
    /// from what the session saw: its name, the first note (the module, the
    /// program, the options), the last status line (the outcome), and the
    /// path of its telegram log. Best effort: never throws into a flash.
    /// </summary>
    public static class FlashHistory
    {
        public sealed class Entry
        {
            public DateTime Started { get; set; }
            public double Seconds { get; set; }
            /// <summary>The session's operation name, e.g. flash-program.</summary>
            public string Operation { get; set; }
            /// <summary>The car: VIN and the module's hardware reference at the time.</summary>
            public string Vin { get; set; }
            public string Module { get; set; }
            /// <summary>The session's first note: what was written and with which options.</summary>
            public string Details { get; set; }
            /// <summary>The last status line the app showed while the session ran.</summary>
            public string Status { get; set; }
            /// <summary>ok, failed or ended, judged from the status line.</summary>
            public string Result { get; set; }
            public string Log { get; set; }
            /// <summary>The folder holding copies of the images written, when the setting was on.</summary>
            public string Files { get; set; }
            /// <summary>Set when the session ran on an emulated module rather than a car: which one.</summary>
            public string Emulator { get; set; }
        }

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bmweb-flasher", "history.jsonl");

        /// <summary>Where copies of written images go, one folder per session.</summary>
        public static string FlashedDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bmweb-flasher", "flashed");

        /// <summary>Raised after an entry is appended or the history is cleared.</summary>
        public static event Action Changed;

        private static readonly object Gate = new object();

        public static void Append(Entry entry)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not record history: " + ex.Message);
            }
            Changed?.Invoke();
        }

        /// <summary>All entries, newest first. A damaged line is skipped, not fatal.</summary>
        public static List<Entry> Load()
        {
            var entries = new List<Entry>();
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(FilePath))
                        return entries;
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { entries.Add(JsonSerializer.Deserialize<Entry>(line)); }
                        catch (JsonException) { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not read history: " + ex.Message);
            }
            entries.Reverse();
            return entries;
        }

        public static void Clear()
        {
            try
            {
                lock (Gate)
                {
                    if (File.Exists(FilePath))
                        File.Delete(FilePath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not clear history: " + ex.Message);
            }
            Changed?.Invoke();
        }

        /// <summary>A human name for a session's operation.</summary>
        public static string Describe(string operation)
        {
            switch (operation)
            {
                case "flash-tune": return "Flash tune";
                case "flash-program": return "Flash program";
                case "tcu-cal-write": return "Write TCU calibration";
                case "tcu-program-write": return "Write TCU program";
                case "tcu-full-read": return "Read TCU";
                default: return operation ?? string.Empty;
            }
        }

        /// <summary>ok / failed / ended, from the outcome the status line reports.</summary>
        public static string Judge(string status)
        {
            if (string.IsNullOrEmpty(status)) return "ended";
            string s = status.ToLowerInvariant();
            if (s.Contains("fail") || s.Contains("denied") || s.Contains("cancel") ||
                s.Contains("no data") || s.Contains("error") || s.Contains("not match"))
                return "failed";
            if (s.Contains("success") || s.Contains("written") || s.StartsWith("read 0x"))
                return "ok";
            return "ended";
        }
    }
}
