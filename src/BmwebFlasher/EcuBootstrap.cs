using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BmwebFlasher
{
    /// <summary>
    /// First-run helper that fetches the EDIABAS SGBD data the app needs
    /// (ms450ds0.prg, gs20.prg, D_MOTOR.grp, ...) from a public Hugging Face
    /// dataset and unpacks it locally. The original Windows tool assumed an
    /// EDIABAS install at C:\Ediabas\Ecu; there is no such standard location on
    /// macOS/Linux, so this provides the files on demand instead.
    ///
    /// The archive is BMW-derived data hosted by the user, not shipped in the
    /// app. Nothing downloads without the user's explicit consent (see the
    /// prompt in MainWindow).
    /// </summary>
    public static class EcuBootstrap
    {
        // Public HF dataset. The path segment has spaces and parens, so it is
        // URL-encoded.
        private const string Url =
            "https://huggingface.co/datasets/CraigFf/bmw-files/resolve/main/" +
            "sp-daten%20merge%20(compressed)/E46.tar.zst";

        /// <summary>Default local ECU folder: ~/.local/share (or AppData)/bmweb-flasher/ecu.</summary>
        public static string DefaultEcuPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bmweb-flasher", "ecu");

        /// <summary>The key SGBD files that must be present for the app to work.</summary>
        private static readonly string[] RequiredFiles =
        {
            "ms450ds0.prg", "D_MOTOR.grp",
        };

        /// <summary>
        /// True when <paramref name="ecuPath"/> already holds the SGBDs, so no
        /// download is needed. Case-insensitive, since the archive mixes cases.
        /// </summary>
        public static bool HasEcuData(string ecuPath)
        {
            if (string.IsNullOrWhiteSpace(ecuPath) || !Directory.Exists(ecuPath))
                return false;

            foreach (var required in RequiredFiles)
            {
                if (!FileExistsCaseInsensitive(ecuPath, required))
                    return false;
            }
            return true;
        }

        private static bool FileExistsCaseInsensitive(string dir, string name)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Downloads the E46 SP-Daten archive and extracts its SGBD files to
        /// <see cref="DefaultEcuPath"/>. Reports download progress (0..100, or
        /// -1 when the total size is unknown) via <paramref name="progress"/>.
        /// Returns the folder the SGBDs were written to.
        ///
        /// Extraction shells out to `zstd | tar`, which ships with macOS (bsdtar)
        /// and is present on Linux; this avoids adding a zstd/tar dependency to
        /// the app for a once-per-install operation.
        /// </summary>
        public static async Task<string> DownloadAndExtractAsync(
            IProgress<int> progress, CancellationToken ct = default)
        {
            string destDir = DefaultEcuPath;
            Directory.CreateDirectory(destDir);

            string tmp = Path.Combine(Path.GetTempPath(),
                "bmweb-e46-" + Guid.NewGuid().ToString("N") + ".tar.zst");

            try
            {
                await DownloadFileAsync(Url, tmp, progress, ct);
                await ExtractEcuAsync(tmp, destDir, ct);

                if (!HasEcuData(destDir))
                {
                    throw new InvalidOperationException(
                        "The archive extracted but the expected SGBD files are not in " +
                        destDir + ". The dataset layout may have changed.");
                }
                return destDir;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            }
        }

        private static async Task DownloadFileAsync(
            string url, string dest, IProgress<int> progress, CancellationToken ct)
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            int lastPct = -1;
            while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await dst.WriteAsync(buffer, 0, n, ct);
                read += n;
                if (total.HasValue && total.Value > 0)
                {
                    int pct = (int)(read * 100 / total.Value);
                    if (pct != lastPct) { lastPct = pct; progress?.Report(pct); }
                }
                else
                {
                    progress?.Report(-1);
                }
            }
            progress?.Report(100);
        }

        /// <summary>
        /// Extracts only the E46/ecu/ SGBD files from the archive into
        /// <paramref name="destDir"/> (flattened, no E46/ecu prefix).
        /// </summary>
        private static async Task ExtractEcuAsync(string archive, string destDir, CancellationToken ct)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            if (OperatingSystem.IsWindows())
            {
                // Windows 10 and later ship bsdtar as tar.exe, which reads zstd
                // itself. There is no shell to pipe through and no zstd binary
                // to pipe from, so the archive is handed straight to tar.
                psi.FileName = "tar";
                psi.ArgumentList.Add("-x");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(archive);
                psi.ArgumentList.Add("-C");
                psi.ArgumentList.Add(destDir);
                psi.ArgumentList.Add("--strip-components=2");
                psi.ArgumentList.Add("E46/ecu");
            }
            else
            {
                // zstd -dc <archive> | tar -x -C <destDir> --strip-components=2 E46/ecu
                // bsdtar (macOS) and GNU tar both accept --strip-components and a
                // path filter; reading zstd from stdin keeps it to two known tools.
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(
                    "set -e; " +
                    "zstd -dc " + Quote(archive) + " | " +
                    "tar -x -C " + Quote(destDir) + " --strip-components=2 'E46/ecu'");
            }

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start the extractor.");

            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    OperatingSystem.IsWindows()
                        ? "Extracting the SGBD archive failed. This needs the tar that ships " +
                          "with Windows 10 and later.\n\n" + stderr.Trim()
                        : "Extracting the SGBD archive failed. Is `zstd` installed? " +
                          "(brew install zstd)\n\n" + stderr.Trim());
            }
        }

        private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
    }
}
