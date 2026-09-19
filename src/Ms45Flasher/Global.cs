using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MS45_Flasher
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

        private static string DefaultPort()
        {
            if (OperatingSystem.IsWindows())
                return "COM1";

            // Prefer a plugged-in FTDI/K+DCAN cable if we can see exactly one.
            try
            {
                var candidates = Directory.GetFiles("/dev", "cu.usbserial*");
                if (candidates.Length == 1)
                    return candidates[0];
            }
            catch (Exception)
            {
                // /dev is unreadable in some sandboxes; fall through to the blank default.
            }

            return string.Empty;
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

        public static string Port
        {
            get => Settings.Port;
            set { Settings.Port = value; Save(); }
        }

        private static Settings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath)) ?? new Settings();
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
