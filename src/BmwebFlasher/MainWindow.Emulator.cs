using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace BmwebFlasher
{
    /// <summary>
    /// The Emulator screen: boots an emulated DME (ms45-emu) or transmission
    /// (gs20-emu) on chosen files, switches its ignition, and connects the
    /// app to it in place of the cable.
    ///
    /// The connection is the point. With an emulator connected, Global.Port
    /// is its K line and nothing else, so every other screen (identify, read,
    /// flash, fault codes, live log) works on the emulated module without
    /// knowing it. The banner above the control unit box, the window title
    /// and every session's log and history entry say which it was, so a run
    /// on an emulator can never be mistaken for one on a car, or the other
    /// way round.
    /// </summary>
    public partial class MainWindow
    {
        private readonly List<string> _emuLog = new List<string>();
        private const int EmuLogLines = 200;

        private void InitEmulator()
        {
            if (string.IsNullOrEmpty(Global.Settings.Ms45EmuPath))
                Global.Settings.Ms45EmuPath = Emulation.FindFolder("ms45-emu") ?? string.Empty;
            if (string.IsNullOrEmpty(Global.Settings.Gs20EmuPath))
                Global.Settings.Gs20EmuPath = Emulation.FindFolder("gs20-emu") ?? string.Empty;

            foreach (EmulatorProcess emulator in new[] { Emulation.Dme, Emulation.Tcu })
            {
                EmulatorProcess e = emulator;
                e.Changed += () => Dispatcher.UIThread.Post(RefreshEmulator);
                e.Line += line => Dispatcher.UIThread.Post(() => AppendEmulatorLog(e.Name + "  " + line));
            }
            Emulation.Changed += () => Dispatcher.UIThread.Post(OnEmulatorConnectionChanged);
            RefreshEmulator();
        }

        private void RailEmulator_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 5;

        private void AppendEmulatorLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || EmuLog == null) return;
            _emuLog.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            if (_emuLog.Count > EmuLogLines) _emuLog.RemoveRange(0, _emuLog.Count - EmuLogLines);
            EmuLog.Text = string.Join(Environment.NewLine, _emuLog);
            EmuLogScroll?.ScrollToEnd();
        }

        // ---- what the screen shows ------------------------------------------------

        private static string NameOrNone(string path) =>
            string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileName(path);

        /// <summary>
        /// Whether BMW's data files are what the DME is booted on: not while
        /// a full pair is chosen (both the external and the MPC flash), which
        /// is then booted as it is. A file chosen earlier is kept and comes
        /// back when the pair is cleared.
        /// </summary>
        private static bool EmuDmeDatenUsable =>
            string.IsNullOrEmpty(Global.Settings.EmuDmeFlash) || string.IsNullOrEmpty(Global.Settings.EmuDmeMpc);

        /// <summary>The same for the transmission: not while a full 512 KB image is chosen.</summary>
        private static bool EmuTcuDatenUsable => string.IsNullOrEmpty(Global.Settings.EmuTcuImage);

        /// <summary>Brings every control of the screen, the banner and the title in line with the emulators.</summary>
        private void RefreshEmulator()
        {
            if (EmuDmeStatus_Box == null) return;   // during init
            Settings s = Global.Settings;
            EmulatorProcess dme = Emulation.Dme, tcu = Emulation.Tcu;

            EmuDmeFlash_Box.Text = NameOrNone(s.EmuDmeFlash);
            EmuDmeMpc_Box.Text = NameOrNone(s.EmuDmeMpc);
            EmuTcuImage_Box.Text = NameOrNone(s.EmuTcuImage);
            // A full image (the DME's pair, the transmission's 512 KB) is booted as it
            // is: BMW's data files are an alternative to it, and are greyed out beside it.
            bool dmeDaten = EmuDmeDatenUsable, tcuDaten = EmuTcuDatenUsable;
            bool dmeProgram = dmeDaten && !string.IsNullOrEmpty(s.EmuDmeProgram);
            bool tcuProgram = tcuDaten && !string.IsNullOrEmpty(s.EmuTcuProgram);
            const string fullPair = "(not used: a full pair is chosen)", fullImage = "(not used: a full image is chosen)";
            EmuDmeProgram_Box.Text = dmeDaten ? NameOrNone(s.EmuDmeProgram) : fullPair;
            EmuDmeCalibration_Box.Text = !dmeDaten ? fullPair
                : string.IsNullOrEmpty(s.EmuDmeCalibration) && dmeProgram ? "(one that is made for the program)"
                : NameOrNone(s.EmuDmeCalibration);
            EmuTcuProgram_Box.Text = tcuDaten ? NameOrNone(s.EmuTcuProgram) : fullImage;
            EmuTcuCalibration_Box.Text = !tcuDaten ? fullImage
                : string.IsNullOrEmpty(s.EmuTcuCalibration) && tcuProgram ? "(one that goes with the program)"
                : NameOrNone(s.EmuTcuCalibration);
            if (tcuDaten && (tcuProgram || !string.IsNullOrEmpty(s.EmuTcuCalibration)))
                EmuTcuImage_Box.Text = "(gs20-emu's stock image)";
            if (dmeProgram)
            {
                EmuDmeMpc_Box.Text = "(the program's own)";
                if (string.IsNullOrEmpty(s.EmuDmeFlash))
                    EmuDmeFlash_Box.Text = "(ms45-emu's own boot loader)";
            }
            EmuFolderMs45_Box.Text = string.IsNullOrEmpty(s.Ms45EmuPath) ? "(not found)" : s.Ms45EmuPath;
            EmuFolderGs20_Box.Text = string.IsNullOrEmpty(s.Gs20EmuPath) ? "(not found)" : s.Gs20EmuPath;

            EmuDmeBoot.IsEnabled = !dme.Running;
            EmuDmeStop.IsEnabled = dme.Running;
            EmuDmeFlash.IsEnabled = EmuDmeMpc.IsEnabled = EmuDmeResume.IsEnabled = EmuDmeTurbo.IsEnabled = !dme.Running;
            EmuDmeFlashClear.IsEnabled = !dme.Running && !string.IsNullOrEmpty(s.EmuDmeFlash);
            EmuDmeMpcClear.IsEnabled = !dme.Running && !string.IsNullOrEmpty(s.EmuDmeMpc);
            EmuDmeProgram.IsEnabled = EmuDmeCalibration.IsEnabled = !dme.Running && dmeDaten;
            EmuDmeProgramClear.IsEnabled = !dme.Running && dmeProgram;
            EmuDmeCalibrationClear.IsEnabled = !dme.Running && dmeDaten && !string.IsNullOrEmpty(s.EmuDmeCalibration);
            EmuDmeIgnition.IsEnabled = dme.Ready;
            EmuDmeIgnition.IsChecked = dme.SwitchOn;
            EmuDmeStatus_Box.Text = dme.Status + (dme.Running && dme.Port != null ? "   ·   K line on " + dme.Port : "")
                                    + (dme.Gateway != null ? "   ·   BMWeb: ?gateway=" + dme.Gateway : "");
            EmuDmeBmweb.IsEnabled = dme.Gateway != null;

            EmuTcuBoot.IsEnabled = !tcu.Running;
            EmuTcuStop.IsEnabled = tcu.Running;
            EmuTcuImage.IsEnabled = EmuTcuResume.IsEnabled = !tcu.Running;
            EmuTcuProgram.IsEnabled = EmuTcuCalibration.IsEnabled = !tcu.Running && tcuDaten;
            EmuTcuImageClear.IsEnabled = !tcu.Running && !string.IsNullOrEmpty(s.EmuTcuImage);
            EmuTcuProgramClear.IsEnabled = !tcu.Running && tcuProgram;
            EmuTcuCalibrationClear.IsEnabled = !tcu.Running && tcuDaten && !string.IsNullOrEmpty(s.EmuTcuCalibration);
            EmuTcuPower.IsEnabled = tcu.Ready;
            EmuTcuPower.IsChecked = tcu.SwitchOn;
            EmuTcuStatus_Box.Text = tcu.Status + (tcu.Running && tcu.Port != null ? "   ·   K line on " + tcu.Port : "")
                                    + (tcu.Gateway != null ? "   ·   BMWeb: ?gateway=" + tcu.Gateway : "");
            EmuTcuBmweb.IsEnabled = tcu.Gateway != null;

            EmuConnectDme.IsEnabled = dme.Running || Emulation.Connected == dme;
            EmuConnectTcu.IsEnabled = tcu.Running || Emulation.Connected == tcu;
            EmuConnectCable.IsChecked = !Emulation.Active;
            EmuConnectDme.IsChecked = Emulation.Connected == dme;
            EmuConnectTcu.IsChecked = Emulation.Connected == tcu;

            EmulatorProcess on = Emulation.Connected;
            if (on == null)
            {
                EmuConnect_Box.Text = "Jobs go down the cable" +
                    (string.IsNullOrEmpty(Global.CablePort) ? " (none set)." : " on " + Global.CablePort + ".");
                EmulatorBanner.IsVisible = false;
                Title = Global.Title;
            }
            else
            {
                string state = !on.Running ? "It is not running: boot it, or go back to the cable."
                             : !on.Ready ? "It is still booting."
                             : on.SwitchOn ? "Its " + on.SwitchName + " is on."
                             : "Its " + on.SwitchName + " is off: it will not answer.";
                EmuConnect_Box.Text = "Every screen works on the " + Emulation.Label + ". Nothing is sent down the cable. " + state;
                // The bar says one thing; which unit it is and what state it is in are on this screen.
                EmulatorBannerText.Text = "EMULATED CONTROL UNIT";
                EmulatorBanner.IsVisible = true;
                Title = Global.Title + "  —  EMULATED " + on.Name;
            }
            RailEmulator.Classes.Set("current", MainTabs.SelectedIndex == 5);
        }

        /// <summary>
        /// The app was pointed at something else. What it knew about the
        /// module on the other end (the identification, the proven port) is
        /// about a different module now, so it is forgotten, and the control
        /// unit box follows an emulator to the unit it emulates.
        /// </summary>
        private void OnEmulatorConnectionChanged()
        {
            _portProven = false;
            EmulatorProcess on = Emulation.Connected;
            int index = on == null ? FlashModuleSelect.SelectedIndex : (on.Unit == EmulatedUnit.Tcu ? 1 : 0);
            if (FlashModuleSelect.SelectedIndex != index)
                FlashModuleSelect.SelectedIndex = index;            // its handler clears the identification
            else if (index >= 0)
                FlashModuleSelect_Changed(FlashModuleSelect, null);
            FaultsTab.IsEnabled = LiveTab.IsEnabled = false;         // both wait for an identify
            SetStatus(on == null
                ? "Connected to the cable" + (string.IsNullOrEmpty(Global.CablePort) ? "" : " (" + Global.CablePort + ")")
                : "Connected to the " + Emulation.Label + ": not a car");
            RefreshEmulator();
        }

        // ---- choosing files and folders ---------------------------------------------

        private static readonly FilePickerFileType BinaryImages =
            new FilePickerFileType("Binary image") { Patterns = new[] { "*.bin", "*.BIN" } };
        private static readonly FilePickerFileType ProgramFiles =
            new FilePickerFileType("BMW program file") { Patterns = new[] { "*.0pa", "*.0PA" } };
        private static readonly FilePickerFileType CalibrationFiles =
            new FilePickerFileType("BMW calibration file") { Patterns = new[] { "*.0da", "*.0DA" } };

        private async Task<string> PickEmulatorFileAsync(string title, FilePickerFileType kind = null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    kind ?? BinaryImages,
                    new FilePickerFileType("All Files") { Patterns = new[] { "*" } },
                },
            });
            return files?.FirstOrDefault()?.TryGetLocalPath();
        }

        /// <summary>One of the screen's file choices: picked from a dialog, or cleared with a null title.</summary>
        private async Task ChooseEmulatorFileAsync(Action<string> set, string title, FilePickerFileType kind = null)
        {
            string path = string.Empty;
            if (title != null)
            {
                path = await PickEmulatorFileAsync(title, kind);
                if (path == null) return;
            }
            set(path);
            Global.Save();
            RefreshEmulator();
        }

        private async void EmuDmeProgram_Click(object sender, RoutedEventArgs e) => await ChooseEmulatorFileAsync(
            p => Global.Settings.EmuDmeProgram = p, "Program file for the emulated DME (.0PA)", ProgramFiles);

        private async void EmuDmeCalibration_Click(object sender, RoutedEventArgs e) => await ChooseEmulatorFileAsync(
            p => Global.Settings.EmuDmeCalibration = p, "Calibration file for the emulated DME (.0DA)", CalibrationFiles);

        private async void EmuTcuProgram_Click(object sender, RoutedEventArgs e) => await ChooseEmulatorFileAsync(
            p => Global.Settings.EmuTcuProgram = p, "Program file for the emulated transmission (.0PA)", ProgramFiles);

        private async void EmuTcuCalibration_Click(object sender, RoutedEventArgs e) => await ChooseEmulatorFileAsync(
            p => Global.Settings.EmuTcuCalibration = p, "Calibration file for the emulated transmission (.0DA)", CalibrationFiles);

        private async void EmuDmeFlashClear_Click(object sender, RoutedEventArgs e) =>
            await ChooseEmulatorFileAsync(p => Global.Settings.EmuDmeFlash = p, null);

        private async void EmuDmeMpcClear_Click(object sender, RoutedEventArgs e) =>
            await ChooseEmulatorFileAsync(p => Global.Settings.EmuDmeMpc = p, null);

        private async void EmuDmeProgramClear_Click(object sender, RoutedEventArgs e) =>
            await ChooseEmulatorFileAsync(p => Global.Settings.EmuDmeProgram = p, null);

        private async void EmuDmeCalibrationClear_Click(object sender, RoutedEventArgs e) =>
            await ChooseEmulatorFileAsync(p => Global.Settings.EmuDmeCalibration = p, null);

        private async void EmuTcuImageClear_Click(object sender, RoutedEventArgs e) =>
            await ChooseEmulatorFileAsync(p => Global.Settings.EmuTcuImage = p, null);

        private async void EmuTcuProgramClear_Click(object sender, RoutedEventArgs e) =>
            await ChooseEmulatorFileAsync(p => Global.Settings.EmuTcuProgram = p, null);

        private async void EmuTcuCalibrationClear_Click(object sender, RoutedEventArgs e) =>
            await ChooseEmulatorFileAsync(p => Global.Settings.EmuTcuCalibration = p, null);

        private async Task<string> PickEmulatorFolderAsync(string title)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders?.FirstOrDefault()?.TryGetLocalPath();
        }

        private async void EmuDmeFlash_Click(object sender, RoutedEventArgs e)
        {
            string path = await PickEmulatorFileAsync("External flash for the emulated DME (1 MB)");
            if (path == null) return;
            Global.Settings.EmuDmeFlash = path;
            Global.Save();
            RefreshEmulator();
        }

        private async void EmuDmeMpc_Click(object sender, RoutedEventArgs e)
        {
            string path = await PickEmulatorFileAsync("MPC flash for the emulated DME (448 KB)");
            if (path == null) return;
            Global.Settings.EmuDmeMpc = path;
            Global.Save();
            RefreshEmulator();
        }

        private async void EmuTcuImage_Click(object sender, RoutedEventArgs e)
        {
            string path = await PickEmulatorFileAsync("Image for the emulated transmission (512 KB)");
            if (path == null) return;
            Global.Settings.EmuTcuImage = path;
            Global.Save();
            RefreshEmulator();
        }

        private async void EmuFolderMs45_Click(object sender, RoutedEventArgs e)
        {
            string path = await PickEmulatorFolderAsync("The ms45-emu folder");
            if (path == null) return;
            if (!File.Exists(Path.Combine(path, "tools", "kline.py")))
            {
                await MessageAsync("That folder has no tools/kline.py in it, so it is not an ms45-emu checkout.", "Emulator");
                return;
            }
            Global.Settings.Ms45EmuPath = path;
            Global.Save();
            RefreshEmulator();
        }

        private async void EmuFolderGs20_Click(object sender, RoutedEventArgs e)
        {
            string path = await PickEmulatorFolderAsync("The gs20-emu folder");
            if (path == null) return;
            if (!File.Exists(Path.Combine(path, "tools", "kline.py")))
            {
                await MessageAsync("That folder has no tools/kline.py in it, so it is not a gs20-emu checkout.", "Emulator");
                return;
            }
            Global.Settings.Gs20EmuPath = path;
            Global.Save();
            RefreshEmulator();
        }

        // ---- booting, switching, shutting down ---------------------------------------

        private async Task BootEmulatorAsync(EmulatorProcess emulator, EmulatorOptions options)
        {
            options.StateDir = Emulation.StateDir;
            try
            {
                AppendEmulatorLog(emulator.Name + "  booting " +
                                  (options.Resume ? "on the last session's flash"
                                                  : "on " + EmulatorProcess.Describe(emulator.Unit, options)));
                string note = await Task.Run(() => emulator.Start(options));
                if (!string.IsNullOrEmpty(note))
                    await MessageAsync(note, "Emulator");
            }
            catch (InvalidOperationException ex)
            {
                await MessageAsync(ex.Message, "Emulator");
                return;
            }
            catch (Exception ex)
            {
                await MessageAsync("The emulator could not be started: " + ex.Message, "Emulator");
                return;
            }
            // Booting one is asking to work on it.
            Emulation.Connect(emulator);
            RefreshEmulator();
        }

        private async void EmuDmeBoot_Click(object sender, RoutedEventArgs e) =>
            await BootEmulatorAsync(Emulation.Dme, new EmulatorOptions
            {
                Folder = Global.Settings.Ms45EmuPath,
                Image = Global.Settings.EmuDmeFlash,
                Mpc = Global.Settings.EmuDmeMpc,
                Program = EmuDmeDatenUsable ? Global.Settings.EmuDmeProgram : null,
                Calibration = EmuDmeDatenUsable ? Global.Settings.EmuDmeCalibration : null,
                Resume = EmuDmeResume.IsChecked == true,
                Turbo = EmuDmeTurbo.IsChecked == true,
            });

        private async void EmuTcuBoot_Click(object sender, RoutedEventArgs e) =>
            await BootEmulatorAsync(Emulation.Tcu, new EmulatorOptions
            {
                Folder = Global.Settings.Gs20EmuPath,
                Image = Global.Settings.EmuTcuImage,
                Program = EmuTcuDatenUsable ? Global.Settings.EmuTcuProgram : null,
                Calibration = EmuTcuDatenUsable ? Global.Settings.EmuTcuCalibration : null,
                Resume = EmuTcuResume.IsChecked == true,
            });

        private async void EmuDmeStop_Click(object sender, RoutedEventArgs e)
        {
            EmuDmeStop.IsEnabled = false;
            await Task.Run(() => Emulation.Dme.Stop());
            RefreshEmulator();
        }

        private async void EmuTcuStop_Click(object sender, RoutedEventArgs e)
        {
            EmuTcuStop.IsEnabled = false;
            await Task.Run(() => Emulation.Tcu.Stop());
            RefreshEmulator();
        }

        private void EmuDmeIgnition_Click(object sender, RoutedEventArgs e) =>
            Emulation.Dme.SetSwitch(EmuDmeIgnition.IsChecked == true);

        private void EmuTcuPower_Click(object sender, RoutedEventArgs e) =>
            Emulation.Tcu.SetSwitch(EmuTcuPower.IsChecked == true);

        // The browser's BMWeb on the emulator: the site with the emulator's
        // WebSocket as its cable (one page at a time; a second is refused).
        private async void EmuDmeBmweb_Click(object sender, RoutedEventArgs e) => await OpenBmwebAsync(Emulation.Dme);
        private async void EmuTcuBmweb_Click(object sender, RoutedEventArgs e) => await OpenBmwebAsync(Emulation.Tcu);

        private async Task OpenBmwebAsync(EmulatorProcess emulator)
        {
            string url = emulator.BmwebUrl;
            if (url == null) return;
            await Launcher.LaunchUriAsync(new Uri(url));
        }

        // ---- what the app talks to -----------------------------------------------------

        private void EmuConnect_Click(object sender, RoutedEventArgs e)
        {
            if (FlashLog.IsActive)
            {
                // a session is under way on whatever is connected now
                RefreshEmulator();
                return;
            }
            Emulation.Connect(EmuConnectDme.IsChecked == true ? Emulation.Dme
                            : EmuConnectTcu.IsChecked == true ? Emulation.Tcu
                            : null);
        }

        private void EmulatorUseCable_Click(object sender, RoutedEventArgs e)
        {
            if (FlashLog.IsActive) return;
            Emulation.Connect(null);
        }

        /// <summary>
        /// With both emulators running, choosing the other control unit in the
        /// box at the top moves the connection to its emulator, the way one
        /// cable reaches both modules in a car.
        /// </summary>
        private void EmulatorFollowModule()
        {
            if (!Emulation.Active || FlashModuleSelect.SelectedIndex < 0) return;
            EmulatorProcess wanted = FlashModuleSelect.SelectedIndex == 1 ? Emulation.Tcu : Emulation.Dme;
            if (wanted != Emulation.Connected && wanted.Running)
                Emulation.Connect(wanted);
        }
    }
}
