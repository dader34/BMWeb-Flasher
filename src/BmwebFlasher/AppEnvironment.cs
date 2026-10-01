using System;
using System.Collections.Generic;
using System.IO;

namespace BmwebFlasher
{
    /// <summary>
    /// Whether the app is running as a development build, read from a .env file.
    ///
    /// The file is looked for next to the executable and then in the working
    /// directory and each of its parents, so `dotnet run` from anywhere in the
    /// repo finds the .env at the repo root. A release archive ships without
    /// one and is therefore production.
    ///
    ///   BMWEB_ENV=development
    ///
    /// A real environment variable of the same name wins over the file.
    /// Anything other than "development" (or "dev") is production: a missing or
    /// unreadable file must never unlock development-only controls.
    /// </summary>
    public static class AppEnvironment
    {
        public const string Variable = "BMWEB_ENV";
        private const string FileName = ".env";

        private static readonly Lazy<bool> Development = new Lazy<bool>(Detect);

        /// <summary>What the .env / variable says: a development build or not.</summary>
        public static bool IsDevelopmentBuild => Development.Value;

        /// <summary>
        /// Whether the development-only controls are shown. A development
        /// build can switch this off from Settings to see the app as an end
        /// user does; a production build can never switch it on.
        /// </summary>
        public static bool DevelopmentUi { get; set; } = true;

        public static bool IsDevelopment => Development.Value && DevelopmentUi;

        private static bool Detect()
        {
            string value = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                string path = FindFile();
                if (path != null)
                {
                    try { Parse(File.ReadAllLines(path)).TryGetValue(Variable, out value); }
                    catch (Exception) { value = null; }
                }
            }
            return IsDevelopmentValue(value);
        }

        internal static bool IsDevelopmentValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            return value.Equals("development", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("dev", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindFile()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, FileName);
            if (File.Exists(beside)) return beside;

            try
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, FileName);
                    if (File.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            catch (Exception)
            {
                // An unreadable working directory just means no file.
            }
            return null;
        }

        /// <summary>
        /// KEY=VALUE lines. Blank lines and # comments are skipped, an optional
        /// leading "export " is dropped, and matching quotes around the value
        /// are removed.
        /// </summary>
        internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line.Substring(7).TrimStart();

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                    value = value.Substring(1, value.Length - 2);

                values[key] = value;
            }
            return values;
        }
    }
}
