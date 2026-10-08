using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace BmwebFlasher
{
    /// <summary>
    /// Replaces the original App.config/ConfigurationManager settings, which were
    /// .NET Framework only and defaulted to Windows paths (COM1, C:\Ediabas\Ecu).
    /// Settings live in a JSON file next to the user's config directory so the
    /// port has no Windows assumptions.
    /// </summary>
    public class Settings
    {
        /// <summary>
        /// On macOS/Linux this is a device path such as /dev/cu.usbserial-XXXX
        /// rather than a COM port. EdInterfaceObd passes it straight to
        /// System.IO.Ports.SerialPort, which accepts either form.
        /// </summary>
        public string Port { get; set; } = DefaultPort();

        /// <summary>Directory holding the EDIABAS .prg (SGBD) files.</summary>
        public string EcuPath { get; set; } = string.Empty;

        public string Sgbd { get; set; } = "D_MOTOR.GRP";

        /// <summary>The skin, by BMWeb's id ("instrument", "inpa", ...), see <see cref="Skins"/>.</summary>
        public string Theme { get; set; } = Skins.DefaultId;

        /// <summary>
        /// Keep a copy of every image written to a module, in the app's
        /// folder, so a history entry can open exactly what went on the car.
        /// </summary>
        public bool KeepFlashedFiles { get; set; } = false;

        /// <summary>Append a programming-log (AIF) entry to a module after every flash, as WinKFP does.</summary>
        public bool WriteAif { get; set; } = true;

        // The Emulator screen: where the two emulators are checked out, and
        // the files each was last booted with.
        public string Ms45EmuPath { get; set; } = string.Empty;
        public string Gs20EmuPath { get; set; } = string.Empty;
        public string EmuDmeFlash { get; set; } = string.Empty;
        public string EmuDmeMpc { get; set; } = string.Empty;
        public string EmuTcuImage { get; set; } = string.Empty;
        // BMW's data files to boot instead of what the images hold: a program (.0PA), a calibration (.0DA).
        public string EmuDmeProgram { get; set; } = string.Empty;
        public string EmuDmeCalibration { get; set; } = string.Empty;
        public string EmuTcuProgram { get; set; } = string.Empty;
        public string EmuTcuCalibration { get; set; } = string.Empty;

        private static string DefaultPort() => Ports.AutoDetect() ?? string.Empty;
    }

    /// <summary>
    /// Serial-port discovery. Lists the ports that plausibly carry a diagnostic
    /// cable and picks the most likely one, so the user rarely has to choose.
    /// </summary>
    public static class Ports
    {
        /// <summary>
        /// All serial ports on the machine that could be a diagnostic cable,
        /// best candidate first. Non-cable ports (Bluetooth, the macOS debug
        /// console, call-up modems) are dropped.
        /// </summary>
        public static List<string> List()
        {
            var names = new List<string>();
            try
            {
                names.AddRange(System.IO.Ports.SerialPort.GetPortNames());
            }
            catch (Exception)
            {
                // GetPortNames can throw on some platforms; return what we have.
            }

            // On macOS also scan /dev directly, since GetPortNames has been
            // known to miss freshly plugged FTDI nodes.
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    foreach (var f in Directory.GetFiles("/dev", "cu.*"))
                        if (!names.Contains(f)) names.Add(f);
                }
                catch (Exception) { }
            }

            var cables = names.Where(IsPlausibleCable).ToList();

            // On macOS every USB serial device appears twice: /dev/cu.* and
            // /dev/tty.*. Diagnostic tools want the cu. (call-up) node -- tty.
            // blocks on carrier detect and can hang the open. Drop a tty. entry
            // whenever its cu. twin is also present.
            cables = cables
                .Where(p => !(p.Contains("/tty.") && cables.Contains(p.Replace("/tty.", "/cu."))))
                .ToList();

            return cables.OrderByDescending(Score).ToList();
        }

        /// <summary>
        /// The most likely cable, or null when nothing is plugged in.
        ///
        /// Returning a guess when no port exists is worse than returning
        /// nothing: it gets saved as though it were a real detection, and the
        /// cable plugged in afterwards is then never looked for.
        /// </summary>
        public static string AutoDetect() => List().FirstOrDefault();

        private static bool IsPlausibleCable(string port)
        {
            string p = port.ToLowerInvariant();
            // Exclude the obvious non-cables on macOS.
            if (p.Contains("bluetooth") || p.Contains("debug-console") || p.EndsWith("/cu.wlan"))
                return false;
            // On Windows every COMx is a candidate.
            if (OperatingSystem.IsWindows())
                return true;
            // On macOS/Linux, a real USB serial adapter.
            return p.Contains("usbserial") || p.Contains("usbmodem") ||
                   p.Contains("ftdi") || p.Contains("ttyusb") || p.Contains("ttyacm") ||
                   p.Contains("slab") || p.Contains("wchusbserial");
        }

        /// <summary>Rank cables: an FTDI/usbserial node (K+DCAN) beats the rest.</summary>
        private static int Score(string port)
        {
            string p = port.ToLowerInvariant();

            // Windows port names carry no hint of what is on the other end, so
            // there is nothing to rank on. Identify sweeps the ports and keeps
            // whichever one a module answers on, which settles it properly.
            if (OperatingSystem.IsWindows()) return 0;

            // On macOS prefer the cu. node over its tty. twin.
            int cuBonus = p.Contains("/cu.") ? 4 : 0;
            if (p.Contains("usbserial") || p.Contains("ftdi") || p.Contains("ttyusb")) return 3 + cuBonus;
            if (p.Contains("wchusbserial") || p.Contains("slab")) return 2;
            if (p.Contains("usbmodem") || p.Contains("ttyacm")) return 1;
            return 0;
        }

    }

    public static class Global
    {
        public static string Title = SetTitle();
        public static string VIN;
        public static string HW_Ref;
        public static string SW_Ref;

        /// <summary>
        /// The PROGRAM reference, read from the ECU by ZIF_LESEN
        /// (KWP2000 $22 $2503 ProgrammReferenz).
        ///
        /// This is a different field from SW_Ref: DATEN_REFERENZ_LESEN ($2504)
        /// returns the DATA reference, which tracks the tune, while this tracks
        /// the program. On a stock MS45.1 they read 0044570LO00S and
        /// 0044570LO02S respectively. Upstream never reads this one.
        /// </summary>
        public static string Prog_Ref;

        public static string diagProtocol;

        public static byte[] openedFlash = null;
        public static byte[] openedMPC = null;

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "bmweb-flasher", "settings.json");

        // The app used to store settings under "ms45flasher"; migrate that file
        // once so an existing install keeps its port / ECU path after the rename.
        private static readonly string LegacyConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ms45flasher", "settings.json");

        public static Settings Settings { get; private set; } = Load();

        public static string sgbd
        {
            get => Settings.Sgbd;
            set { Settings.Sgbd = value; Save(); }
        }

        public static string ecuPath
        {
            get => Settings.EcuPath;
            set { Settings.EcuPath = value; Save(); }
        }

        /// <summary>
        /// The port every job uses. While the app is connected to an emulator
        /// (the Emulator screen) this is that emulator's K line and nothing
        /// else: the saved cable is neither returned nor changed, so no job
        /// can reach a car by accident, and the sweep that looks for a cable
        /// cannot replace it.
        /// </summary>
        public static string Port
        {
            get => Emulation.Port ?? Settings.Port;
            set
            {
                if (Emulation.Active) return;
                Settings.Port = value;
                Save();
            }
        }

        /// <summary>The cable's port as saved, whatever the app is connected to.</summary>
        public static string CablePort => Settings.Port;

        public static string Theme
        {
            get => Settings.Theme;
            set { Settings.Theme = value; Save(); }
        }

        public static bool KeepFlashedFiles
        {
            get => Settings.KeepFlashedFiles;
            set { Settings.KeepFlashedFiles = value; Save(); }
        }

        public static bool WriteAif
        {
            get => Settings.WriteAif;
            set { Settings.WriteAif = value; Save(); }
        }

        private static Settings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath)) ?? new Settings();

                // One-time migration from the pre-rename location.
                if (File.Exists(LegacyConfigPath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(LegacyConfigPath)) ?? new Settings();
                    Settings = s;
                    Save(); // writes to the new ConfigPath
                    return s;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not read settings, using defaults: " + ex.Message);
            }
            return new Settings();
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not save settings: " + ex.Message);
            }
        }

        private static string SetTitle()
        {
            // ApplicationDeployment (ClickOnce) does not exist off .NET Framework;
            // the assembly version is the only source now.
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return "BMWeb Flasher " + version;
        }
    }
}
