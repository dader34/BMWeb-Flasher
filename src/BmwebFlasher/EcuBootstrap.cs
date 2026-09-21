using System;
using System.Formats.Tar;
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
        /// Extraction happens in process. It used to shell out to zstd and tar,
        /// which worked on macOS and not on Windows: the tar that ships with
        /// Windows recognises a zstd archive but hands the decoding to a zstd
        /// binary, which is not installed.
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
            // Decompress and untar in process. Shelling out to zstd and tar
            // seemed reasonable on macOS, where both exist, but the tar that
            // ships with Windows recognises a zstd archive and then tries to run
            // a zstd binary to decode it, which is not installed. Doing it here
            // removes the dependency on either platform.
            await using var file = new FileStream(
                archive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var decompressed = new ZstdSharp.DecompressionStream(file);
            await using var tar = new TarReader(decompressed);

            const string Wanted = "E46/ecu/";
            int written = 0;

            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.EntryType != TarEntryType.RegularFile) continue;
                if (entry.DataStream == null) continue;

                // The archive holds the whole data set; only the SGBDs are
                // wanted, flattened out of their E46/ecu prefix.
                string name = entry.Name.Replace('\\', '/');
                if (!name.StartsWith(Wanted, StringComparison.OrdinalIgnoreCase)) continue;

                string leaf = Path.GetFileName(name);
                if (string.IsNullOrEmpty(leaf)) continue;

                await using var dest = new FileStream(
                    Path.Combine(destDir, leaf), FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true);
                await entry.DataStream.CopyToAsync(dest, ct);
                written++;
            }

            if (written == 0)
            {
                throw new InvalidOperationException(
                    "The archive held no " + Wanted + " files. Its layout may have changed.");
            }
        }

    }
}
