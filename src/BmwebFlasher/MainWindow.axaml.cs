using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

using EdiabasLib;

namespace BmwebFlasher
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Title = Global.Title;
            ModuleSelect.SelectedIndex = 0; // fault-codes module, DME by default
            // The flashing module starts UNSELECTED: the user must choose DME or
            // TCU before any control unit's buttons appear. This keeps the two
            // modules' actions from ever being confused.

            // Auto-detect the cable if none is saved yet.
            if (string.IsNullOrEmpty(Global.Port))
            {
                string auto = Ports.AutoDetect();
                if (!string.IsNullOrEmpty(auto))
                    Global.Port = auto;
            }

            // Offer to fetch the SGBD data on first run, once the window is up.
            Opened += async (_, _) =>
            {
                if (!string.IsNullOrEmpty(Global.Port))
                    SetStatus("Detected port: " + Global.Port);
                await MaybeBootstrapEcuAsync();
            };
        }

        /// <summary>
        /// On first run there are no SGBD files, and without them no job can
        /// run. Show the setup window so the user can download them, point at an
        /// existing EDIABAS folder, or skip. Everything is opt-in.
        /// </summary>
        private async Task MaybeBootstrapEcuAsync()
        {
            if (EcuBootstrap.HasEcuData(Global.ecuPath))
                return; // already configured with valid data

            // A previous run may have downloaded them to the default location.
            if (EcuBootstrap.HasEcuData(EcuBootstrap.DefaultEcuPath))
            {
                Global.ecuPath = EcuBootstrap.DefaultEcuPath;
                Global.sgbd = "ms450ds0.prg";
                return;
            }

            var setup = new SetupWindow();
            await setup.ShowDialog(this);

            if (!string.IsNullOrEmpty(setup.Result))
            {
                Global.ecuPath = setup.Result;
                Global.sgbd = "ms450ds0.prg";
                SetStatus("ECU data ready. Connect the cable and Identify DME.");
            }
            else
            {
                SetStatus("No ECU data set up. Use Load SGBD to pick your own folder.");
            }
        }

        // --- UI helpers -----------------------------------------------------
        // WPF's Dispatcher.Invoke becomes Avalonia's Dispatcher.UIThread.

        private void SetStatus(string text) =>
            Dispatcher.UIThread.Post(() => statusTextBlock.Text = text);

        private void UpdateProgressBar(uint progress) =>
            Dispatcher.UIThread.Post(() => ProgressDME.Value = Math.Min(progress, 100),
                                     DispatcherPriority.Background);

        /// <summary>
        /// Colours the progress bar red while something is being written to a
        /// module, so a flash in progress is never mistaken for a read.
        /// </summary>
        private void ShowProgressAsFlashing(bool flashing) =>
            Dispatcher.UIThread.Post(() =>
                ProgressDME.Foreground = flashing
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xC0, 0x39, 0x2B))
                    : null);

        /// <summary>
        /// Replaces WPF MessageBox.Show(..., YesNo), which has no Avalonia
        /// equivalent. Returns true for "yes". Defaults to No, matching the
        /// original's MessageBoxResult.No default - these prompts all guard
        /// against flashing a mismatched file.
        /// </summary>
        private async Task<bool> ConfirmAsync(string message, string title)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            bool result = false;
            var yes = new Button { Content = "Yes", MinWidth = 90 };
            var no = new Button { Content = "No", MinWidth = 90, IsDefault = true };
            yes.Click += (_, _) => { result = true; dialog.Close(); };
            no.Click += (_, _) => { result = false; dialog.Close(); };

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { no, yes }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return result;
        }

        /// <summary>
        /// A confirmation that will not proceed until the warning is ticked.
        /// Used where the risk is real enough that clicking through on muscle
        /// memory should not be possible.
        /// </summary>
        private async Task<bool> ConfirmWithAcknowledgementAsync(
            string message, string acknowledgement, string title)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            bool result = false;
            var confirm = new Button { Content = "Confirm", MinWidth = 90, IsEnabled = false };
            var cancel = new Button { Content = "Cancel", MinWidth = 90, IsDefault = true };
            var acknowledged = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = acknowledgement,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            };

            acknowledged.IsCheckedChanged += (_, _) => confirm.IsEnabled = acknowledged.IsChecked == true;
            confirm.Click += (_, _) => { result = true; dialog.Close(); };
            cancel.Click += (_, _) => { result = false; dialog.Close(); };

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    acknowledged,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return result;
        }

        private async Task MessageAsync(string message, string title = "BMWeb Flasher")
        {
            var dialog = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };
            var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
            ok.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { ok }
                    }
                }
            };
            await dialog.ShowDialog(this);
        }

        // --- Click handlers -------------------------------------------------

        private async void SetPort_Click(object sender, RoutedEventArgs e)
        {
            // Auto-detect ranks likely cables first (FTDI / K+DCAN); the manual
            // box is the fallback for anything not detected.
            var ports = Ports.List();

            var dialog = new Window
            {
                Title = "Serial Port",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var combo = new ComboBox { ItemsSource = ports, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            var manual = new TextBox { Text = Global.Port ?? string.Empty, PlaceholderText = "/dev/cu.usbserial-A1B2C3D4" };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) manual.Text = s; };

            // Preselect the current port if it is in the list, otherwise the top
            // auto-detected candidate.
            int cur = ports.IndexOf(Global.Port ?? string.Empty);
            if (cur >= 0) combo.SelectedIndex = cur;
            else if (ports.Count > 0 && string.IsNullOrEmpty(manual.Text)) combo.SelectedIndex = 0;

            var refresh = new Button { Content = "Rescan" };
            refresh.Click += (_, _) =>
            {
                var again = Ports.List();
                combo.ItemsSource = again;
                if (again.Count > 0) combo.SelectedIndex = 0;
            };

            var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
            ok.Click += (_, _) => { Global.Port = manual.Text?.Trim() ?? string.Empty; dialog.Close(); };

            string detected = ports.Count == 0
                ? "No cable detected. Enter the port path manually."
                : "Detected ports (best guess first):";

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = detected },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new ContentControl { Content = combo, Width = 320 },
                            refresh,
                        }
                    },
                    new TextBlock { Text = "Port device path:" },
                    manual,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { ok }
                    }
                }
            };

            await dialog.ShowDialog(this);
            SetStatus(string.IsNullOrEmpty(Global.Port) ? "No serial port set" : "Port: " + Global.Port);
        }

        // --- Flashing tab module selection (DME vs TCU) ---------------------

        /// <summary>Which module the Flashing tab is acting on.</summary>
        private bool _flashTcu = false;

        private void FlashModuleSelect_Changed(object sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            if (DmePanel == null || TcuPanel == null) return; // during init

            int idx = FlashModuleSelect.SelectedIndex;
            if (idx < 0)
            {
                // Nothing chosen yet: hide both control sets, prompt to choose.
                DmePanel.IsVisible = false;
                TcuPanel.IsVisible = false;
                IdentifyDME.IsEnabled = false;
                ModuleInfoHeader.Text = "Select a control unit above.";
                return;
            }

            _flashTcu = idx == 1;
            DmePanel.IsVisible = !_flashTcu;
            TcuPanel.IsVisible = _flashTcu;
            IdentifyDME.IsEnabled = true;
            ModuleInfoHeader.Text = _flashTcu ? "TCU Information:" : "DME Information:";

            // The identified state belongs to one module; clear it on a switch.
            DMEType_Box.Text = HWRef_Box.Text = SWRef_Box.Text = programStatus_Box.Text =
                VIN_Box.Text = progRef_Box.Text = diagProtocol_Box.Text = string.Empty;
            ReadTcuCal.IsEnabled = false;
            LoadTcuCal.IsEnabled = false;
            WriteTcuCal.IsEnabled = false;
            _tcuCalToWrite = null;
            _tcuSgbd = null;
            _tcuIdentSwNr = _tcuIdentBmwNr = null;
            RefreshNoUpshiftGate();
            SetStatus("Module: " + (_flashTcu ? "TCU (transmission)" : "DME (engine)"));
        }

        private async void IdentifyDME_Click(object sender, RoutedEventArgs e)
        {
            UpdateProgressBar(0);
            // The original ran IdentDME() synchronously on the UI thread, which
            // froze the window for the duration of the read. It is off-thread
            // now -- but a discarded Task.Run swallows every exception, which
            // made a failing identify look like the button did nothing at all.
            try
            {
                if (_flashTcu)
                    await Task.Run(() => IdentTcu());
                else
                    await Task.Run(() => IdentDME());
            }
            catch (Exception ex)
            {
                SetStatus("Identify failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Identify");
            }
        }

        // --- TCU (transmission) -------------------------------------------------

        /// <summary>The SGBD the TCU resolved to, used for the calibration read.</summary>
        private string _tcuSgbd;

        /// <summary>What the transmission reported at IDENT, for the write gate.</summary>
        private string _tcuIdentSwNr;
        private string _tcuIdentBmwNr;

        /// <summary>
        /// The E46 automatic-transmission SGBDs, from BMW's D_EGS group /
        /// GD20+GD86xx DAT tables. These are the variants D_EGS.grp itself
        /// dispatches to, so asking each one's IDENT is what the group would do
        /// if it could select DS2: BMW's own variant list, not a hardcoded guess.
        ///
        /// The group cannot do it. Its concept selector reads 0 (unspecified)
        /// and the selector-0 path probes only the CAN concepts
        /// (BMW-FAST/KWP2000*/D-CAN), never DS2 (0x06), then raises (eerr).
        /// Confirmed by tracing D_EGS IDENTIFIKATION on the car.
        ///
        /// GS20 (A5S390R, DS2) is first since it is the M54 six's box.
        /// </summary>
        private static readonly string[] TcuVariants =
        {
            "gs20.prg",     // A5S390R  (GM 5-spd, M52/M54) - DS2
            "gs8600.prg",   // A5S325Z  (ZF 5-spd)
            "GS8602.prg",
            "GS8603.prg",
            "gs8604.prg",
        };

        /// <summary>
        /// Resolves the transmission ECU and reads its identity by asking each
        /// of BMW's E46 auto-TCU variants in turn; the first whose own IDENT
        /// answers wins.
        ///
        /// The D_EGS group file is deliberately not tried. Every E46 automatic
        /// is DS2, and the group cannot reach a DS2 module through this
        /// transport, so it failed on every car and only added a timeout to the
        /// front of an identify.
        /// </summary>
        private void IdentTcu()
        {
            string portProblem = CheckPort(Global.Port);
            if (portProblem != null)
            {
                SetStatus("Port unavailable");
                Dispatcher.UIThread.Post(() => _ = MessageAsync(portProblem, "Identify"));
                return;
            }

            SetStatus("Probing transmission variants...");
            foreach (string variant in TcuVariants)
            {
                using EdiabasNet ediabas = StartEdiabasSgbd(variant);
                if (ediabas != null && ExecuteJob(ediabas, "IDENT", string.Empty))
                {
                    _tcuSgbd = variant;
                    ShowTcuIdent(ediabas, variant);
                    return;
                }
            }

            SetStatus("No response from the TCU");
            Dispatcher.UIThread.Post(() => _ = MessageAsync(
                "The transmission did not respond to any known E46 auto-TCU variant. " +
                "Check ignition and the cable.",
                "Identify TCU"));
        }

        // GS20 calibration region: 0x090000, 64 KB (from the TCU RE ground truth).
        private const int TcuCalStart = 0x090000;
        private const int TcuCalLength = 0x10000;

        private async void ReadTcuCal_Click(object sender, RoutedEventArgs e)
        {
            string portProblem = CheckPort(Global.Port);
            if (portProblem != null)
            {
                SetStatus("Port unavailable");
                await MessageAsync(portProblem, "Read Calibration");
                return;
            }

            if (string.IsNullOrEmpty(_tcuSgbd))
            {
                SetStatus("Identify the TCU first");
                return;
            }

            ReadTcuCal.IsEnabled = false;
            UpdateProgressBar(0);
            try
            {
                _tcuFastMode = TcuFastMode.IsChecked == true;
                byte[] cal = await Task.Run(ReadTcuCalibration);
                UpdateProgressBar(0);
                if (cal == null || cal.Length == 0)
                {
                    SetStatus("TCU read returned no data");
                    return;
                }
                await SaveDumpAsync(cal, "TCU_cal_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                SetStatus("Read TCU calibration (0x" + cal.Length.ToString("X") + " bytes)" +
                          DescribeCalChecksum(cal));
            }
            catch (Exception ex)
            {
                UpdateProgressBar(0);
                SetStatus("TCU read failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Read Calibration");
            }
            finally
            {
                ReadTcuCal.IsEnabled = true;
            }
        }

        /// <summary>
        /// Reads the transmission calibration via SPEICHER_LESEN (EPROM). The
        /// GS20 requires an established session, so IDENT runs first; reads then
        /// go in ~250-byte chunks (the job returns at most ~251 bytes).
        /// Read-only -- there is no TCU flash through this SGBD.
        /// </summary>
        /// <summary>
        /// Reads the transmission calibration.
        ///
        /// The GS20 is read over raw DS2 rather than through EDIABAS. The
        /// SPEICHER_LESEN route appended each reply to a running buffer and
        /// stepped on by however many bytes came back, so one short or repeated
        /// reply shifted everything after it: dumps came back with their 16 KB
        /// blocks out of order and a duplicate header at 0x4000, and could
        /// reproduce an earlier dump exactly even after the calibration on the
        /// module had changed. The raw reader places every chunk at the address
        /// it asked for, and moves the line to a faster rate for the transfer.
        ///
        /// Other transmissions keep the EDIABAS path, since only the GS20's
        /// region and framing are known.
        /// </summary>
        private byte[] ReadTcuCalibration()
        {
            bool isGs20 = string.Equals(_tcuSgbd, "gs20.prg", StringComparison.OrdinalIgnoreCase);
            return isGs20 ? ReadGs20CalibrationRaw() : ReadTcuCalibrationViaEdiabas();
        }

        private byte[] ReadGs20CalibrationRaw()
        {
            using (SleepBlocker.Acquire())
            using (var link = new Ds2SerialLink(Global.Port))
            {
                // A faster line rate is worth having but not worth failing over;
                // the read is identical either way, just slower.
                if (_tcuFastMode)
                {
                    // Quiet either way: the percentage is the progress report
                    // while this runs, and the outcome is reported at the end.
                    try { link.SwitchBaud(Ds2SerialLink.FastBaud); }
                    catch (Exception) { }
                }

                try
                {
                    var progress = new Progress<int>(p =>
                    {
                        UpdateProgressBar((uint)p);
                        SetStatus(p + "%");
                    });
                    return new Gs20CalReader(link).Read(progress);
                }
                finally
                {
                    // Leave the module where the rest of the app expects it.
                    try { link.SwitchBaud(Ds2SerialLink.DefaultBaud); }
                    catch (Exception) { }
                }
            }
        }

        private byte[] ReadTcuCalibrationViaEdiabas()
        {
            using (SleepBlocker.Acquire())
            using (EdiabasNet ediabas = StartEdiabasSgbd(_tcuSgbd))
            {
                if (ediabas == null) return null;

                // Establish the diagnostic session; SPEICHER_LESEN does not
                // respond without it.
                ExecuteJob(ediabas, "IDENT", string.Empty);

                var dump = new byte[TcuCalLength];
                int read = 0;
                const int chunk = 250;

                while (read < TcuCalLength)
                {
                    int n = Math.Min(chunk, TcuCalLength - read);
                    if (!ExecuteJob(ediabas, "SPEICHER_LESEN",
                                    "EPROM;" + (TcuCalStart + read) + ";" + n))
                        return null;

                    byte[] data = GetResult_ByteArray("DATEN", ediabas.ResultSets);
                    if (data == null || data.Length == 0) return null;

                    // Place the reply where it was asked for; appending is what
                    // let a short reply shift the rest of the image.
                    int usable = Math.Min(data.Length, n);
                    Buffer.BlockCopy(data, 0, dump, read, usable);
                    read += usable;
                    UpdateProgressBar((uint)(read * 100L / TcuCalLength));
                }
                return dump;
            }
        }

        /// <summary>The calibration picked for a write, already checksum-corrected.</summary>
        private byte[] _tcuCalToWrite;

        /// <summary>Whether the last write got as far as erasing.</summary>
        private bool _tcuWriteErased;

        /// <summary>
        /// Whether to ask the transmission for a faster line rate. Read from the
        /// checkbox on the UI thread before the work starts, since the transfers
        /// themselves run elsewhere.
        /// </summary>
        private bool _tcuFastMode = true;

        private async void LoadTcuCal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Load TCU Calibration",
                    AllowMultiple = false,
                    FileTypeFilter = BinaryFilters()
                });

                string path = files?.FirstOrDefault()?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                    return;

                byte[] cal = File.ReadAllBytes(path);
                if (cal.Length != Gs20Checksum.CalLength)
                {
                    _tcuCalToWrite = null;
                    WriteTcuCal.IsEnabled = false;
                    SetStatus("A GS20 calibration is 64 KB; that file is 0x" +
                              cal.Length.ToString("X") + " bytes");
                    return;
                }

                // Correct the checksum on the way in, so what is held here is
                // exactly what would be written.
                ushort stored = Gs20Checksum.Stored(cal);
                _tcuCalToWrite = Gs20Checksum.Correct(cal);
                ushort corrected = Gs20Checksum.Stored(_tcuCalToWrite);

                WriteTcuCal.IsEnabled = true;

                RefreshNoUpshiftGate();

                string version = Gs20Checksum.ReadVersion(_tcuCalToWrite);
                SetStatus("Loaded " + Path.GetFileName(path) +
                          (version != null ? " (" + version + ")" : string.Empty) +
                          (stored == corrected
                              ? ", checksum already correct"
                              : ", checksum corrected 0x" + stored.ToString("X4") +
                                " to 0x" + corrected.ToString("X4")) +
                          (CalibrationMatchesTransmission(version)
                              ? string.Empty
                              : " - does NOT match the identified transmission"));
            }
            catch (Exception ex)
            {
                _tcuCalToWrite = null;
                WriteTcuCal.IsEnabled = false;
                RefreshNoUpshiftGate();
                SetStatus("Could not load the calibration: " + ex.Message);
                await MessageAsync(Describe(ex), "Load Calibration");
            }
        }


        /// <summary>
        /// Whether a calibration file belongs on the transmission that answered.
        ///
        /// A calibration names itself like "G2210_0090C0ER10", where the four
        /// digits after the underscore are the release. The transmission reports
        /// that same release as its software number, so "0090" lines up with a
        /// reported 90. The "C0" that follows is a variant marker the module
        /// never reports, so it takes no part in the comparison.
        ///
        /// Anything that cannot be lined up counts as a mismatch: warning
        /// needlessly is cheaper than staying quiet about a real one.
        /// </summary>
        private bool CalibrationMatchesTransmission(string fileVersion)
        {
            string release = Gs20Checksum.ReadRelease(fileVersion);
            if (release == null) return false;

            // Only the reported software number is compared. A part number would
            // have to be matched by substring, which invents agreement that is
            // not there, and a wrong match here is the failure that stays quiet.
            string reported = (_tcuIdentSwNr ?? string.Empty).Trim().TrimStart('0');
            if (reported.Length == 0) return false;

            return string.Equals(release, reported, StringComparison.OrdinalIgnoreCase);
        }


        private string DescribeIdentifiedSoftware()
        {
            string software = string.IsNullOrWhiteSpace(_tcuIdentSwNr) ? null : _tcuIdentSwNr.Trim();
            string part = string.IsNullOrWhiteSpace(_tcuIdentBmwNr) ? null : _tcuIdentBmwNr.Trim();

            if (software == null && part == null) return "not identified";
            if (software != null && part != null) return part + " (software " + software + ")";
            return software ?? part;
        }

        /// <summary>
        /// Why the upshift patch cannot be offered, or null when it can.
        ///
        /// It is gated on the loaded calibration rather than on what the car
        /// reports, because the patch rewrites tables at fixed offsets and the
        /// only thing that makes those offsets meaningful is the layout of the
        /// file itself. A calibration that already has the tables raised is
        /// refused too, so ticking the box always means a change.
        /// </summary>
        private string NoUpshiftBlockedReason()
        {
            if (_tcuCalToWrite == null)
                return "Load a calibration first. The patch is applied to the file, not to the car.";

            if (!Gs20NoUpshift.IsApplicable(_tcuCalToWrite))
                return "This calibration does not have the upshift tables the patch expects, so " +
                       "it cannot be applied to it safely.";

            if (Gs20NoUpshift.IsApplied(_tcuCalToWrite))
                return "This calibration already has its upshift points raised.";

            return null;
        }

        private void RefreshNoUpshiftGate()
        {
            bool allowed = NoUpshiftBlockedReason() == null;
            NoUpshift_CheckBox.IsEnabled = allowed;
            if (!allowed)
                NoUpshift_CheckBox.IsChecked = false;
        }

        private async void NoUpshift_CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (NoUpshift_CheckBox.IsChecked != true)
                return;

            // Re-check on the tick: the loaded calibration may have changed
            // since the box was enabled.
            string blocked = NoUpshiftBlockedReason();
            if (blocked != null)
            {
                NoUpshift_CheckBox.IsChecked = false;
                NoUpshift_CheckBox.IsEnabled = false;
                await MessageAsync(blocked, "Remove auto upshift");
                return;
            }

            if (!await ConfirmAsync(
                    "This raises every upshift point out of reach, so the gearbox holds whichever " +
                    "gear is selected and will not change up on its own.\n\n" +
                    "The engine will run to the limiter rather than shifting, and the car will not " +
                    "move off again until you shift manually.\n\nApply it to the loaded calibration?",
                    "Remove auto upshift"))
            {
                NoUpshift_CheckBox.IsChecked = false;
            }
        }

        /// <summary>
        /// Erases and reprograms the transmission calibration over raw DS2.
        ///
        /// This is the one path in the app that drives a module without EDIABAS,
        /// because the GS20's SGBD has no programming job. Between the erase and
        /// the commit the transmission holds no valid calibration, so the checks
        /// in front of it matter more than the speed of getting past them.
        /// </summary>
        private async void WriteTcuCal_Click(object sender, RoutedEventArgs e)
        {
            if (_tcuCalToWrite == null)
            {
                SetStatus("Load a calibration first");
                return;
            }

            string portProblem = CheckPort(Global.Port);
            if (portProblem != null)
            {
                SetStatus("Port unavailable");
                await MessageAsync(portProblem, "Write Calibration");
                return;
            }

            // A calibration built for different software can drive the box badly
            // even though it flashes cleanly, so a mismatch has to be deliberate.
            string fileVersion = Gs20Checksum.ReadVersion(_tcuCalToWrite);
            if (!CalibrationMatchesTransmission(fileVersion))
            {
                if (!await ConfirmWithAcknowledgementAsync(
                        "This calibration was not built for the transmission that answered.\n\n" +
                        "Transmission: " + DescribeIdentifiedSoftware() + "\n" +
                        "Calibration file: " + (fileVersion ?? "no version found") + "\n\n" +
                        "Writing software meant for another gearbox can make it shift badly or " +
                        "not at all. Only continue if you know this calibration belongs on this " +
                        "transmission. You do this at your own risk.",
                        "I understand this calibration may not match, and I accept the risk.",
                        "Calibration does not match"))
                {
                    SetStatus("Write cancelled: the calibration does not match the transmission");
                    return;
                }
            }

            string patchNote = NoUpshift_CheckBox.IsChecked == true
                ? "Automatic upshifts will be removed from the calibration first.\n\n"
                : string.Empty;

            if (!await ConfirmAsync(
                    "This erases and reprograms the transmission calibration at 0x" +
                    Gs20CalWriter.CalAddress.ToString("X6") + ".\n\n" +
                    patchNote +
                    "Until it finishes the transmission has no valid calibration. Do not switch " +
                    "the ignition off or unplug the cable. Keep the voltage steady.\n\nWrite now?",
                    "Write Calibration"))
            {
                return;
            }

            ReadTcuCal.IsEnabled = LoadTcuCal.IsEnabled = false;
            WriteTcuCal.IsEnabled = false;
            UpdateProgressBar(0);

            try
            {
                ShowProgressAsFlashing(true);
                using (FlashLog.Session("tcu-cal-write", out _))
                {
                    FlashLog.Note("TCU " + _tcuSgbd + " / cal 0x" +
                                  Gs20CalWriter.CalAddress.ToString("X6") + " / checksum 0x" +
                                  Gs20Checksum.Stored(_tcuCalToWrite).ToString("X4"));

                    // Patch a copy, so unticking the box gets the loaded
                    // calibration back rather than a permanently altered one.
                    byte[] image = NoUpshift_CheckBox.IsChecked == true
                        ? Gs20NoUpshift.Apply(_tcuCalToWrite)
                        : _tcuCalToWrite;
                    if (!ReferenceEquals(image, _tcuCalToWrite))
                        FlashLog.Note("auto upshift removed");

                    bool fastMode = TcuFastMode.IsChecked == true;
                    var progress = new Progress<int>(p =>
                    {
                        UpdateProgressBar((uint)p);
                        SetStatus(p + "%");
                    });
                    _tcuWriteErased = false;

                    await Task.Run(() =>
                    {
                        using (SleepBlocker.Acquire())
                        using (var link = new Ds2SerialLink(Global.Port, FlashLog.Note))
                        {
                            var writer = new Gs20CalWriter(link, FlashLog.Note);

                            // The session has to be open before anything else is
                            // asked of the module: without it even a supply
                            // reading comes back refused, which reads like a
                            // hardware fault rather than a missing step.
                            writer.OpenSession();
                            FlashLog.Note("session open");

                            // Worth knowing, not worth failing over. A module
                            // left mid-session by an earlier run refuses this
                            // while still flashing perfectly well.
                            try
                            {
                                decimal volts = writer.ReadBatteryVolts();
                                FlashLog.Note("battery " + volts.ToString("0.0") + " V");
                                if (volts > 0m && volts < 11.5m)
                                    throw new InvalidOperationException(
                                        "Battery is " + volts.ToString("0.0") + " V, which is too " +
                                        "low to flash safely. Put a charger on it and try again.");
                            }
                            catch (InvalidOperationException ex) when (!ex.Message.StartsWith("Battery"))
                            {
                                FlashLog.Note("battery reading unavailable: " + ex.Message);
                            }

                            // Five hundred odd telegrams at 9600 leaves the
                            // calibration erased for minutes rather than tens of
                            // seconds. The change is made after the session is
                            // open and before anything is erased, so a module
                            // that will not take it costs only speed.
                            if (fastMode)
                            {
                                try
                                {
                                    link.SwitchBaud(Ds2SerialLink.FastBaud);
                                    FlashLog.Note("baud " + link.Baud);
                                }
                                catch (Exception ex)
                                {
                                    FlashLog.Note("staying at " + link.Baud + " baud: " + ex.Message);
                                }
                            }

                            try
                            {
                                writer.Write(image, progress);
                            }
                            finally
                            {
                                _tcuWriteErased = writer.EraseStarted;

                                // Leave the module on the rate the rest of the
                                // app expects to find it on.
                                try { link.SwitchBaud(Ds2SerialLink.DefaultBaud); }
                                catch (Exception) { }

                                // And out of programming mode, or it holds the
                                // line and the engine control unit stops
                                // identifying.
                                writer.CloseSession();
                            }
                        }
                    });

                    SetStatus("Calibration written. Cycle the ignition before driving.");
                    await MessageAsync(
                        "The calibration was written and the transmission confirmed it.\n\n" +
                        "Cycle the ignition, then check for stored faults before driving.",
                        "Write Calibration");
                }
            }
            catch (Exception ex)
            {
                SetStatus("Calibration write failed: " + ex.Message);

                // Only warn about a half-written calibration when one is
                // actually possible. Stopping before the erase leaves the
                // transmission exactly as it was.
                string aftermath = _tcuWriteErased
                    ? "\n\nThe transmission may be holding an incomplete calibration. Its boot " +
                      "block and program are untouched, so it still answers and can be written " +
                      "again: fix the cause, then write a known-good calibration before driving."
                    : "\n\nNothing was erased or written, so the calibration on the transmission " +
                      "is unchanged.";

                await MessageAsync(Describe(ex) + aftermath, "Write Calibration");
            }
            finally
            {
                ShowProgressAsFlashing(false);
                UpdateProgressBar(0);
                ReadTcuCal.IsEnabled = true;
                LoadTcuCal.IsEnabled = string.Equals(_tcuSgbd, "gs20.prg",
                                                     StringComparison.OrdinalIgnoreCase);
                WriteTcuCal.IsEnabled = _tcuCalToWrite != null;
            }
        }

        /// <summary>
        /// A short note on whether a calibration we just read carries a checksum
        /// that matches its own data. A complete, healthy read always does; a
        /// mismatch means either the read came back short or the box is running
        /// a calibration it will reject on the next power-up. Only the GS20's
        /// checksum is known, so other transmissions say nothing.
        /// </summary>
        private string DescribeCalChecksum(byte[] cal)
        {
            bool isGs20 = string.Equals(_tcuSgbd, "gs20.prg", StringComparison.OrdinalIgnoreCase);
            if (!isGs20 || cal == null || cal.Length < Gs20Checksum.CalLength)
                return string.Empty;

            return Gs20Checksum.Verify(cal)
                ? ", checksum valid"
                : ", checksum does NOT match (stored 0x" +
                  Gs20Checksum.Stored(cal).ToString("X4") + ", expected 0x" +
                  Gs20Checksum.Compute(cal).ToString("X4") + ")";
        }

        private void ShowTcuIdent(EdiabasNet ediabas, string sgbdLabel)
        {
            string bmwNr = GetResult_String("ID_BMW_NR", ediabas.ResultSets);
            string hwNr = GetResult_String("ID_HW_NR", ediabas.ResultSets);
            string swNr = GetResult_String("ID_SW_NR", ediabas.ResultSets);
            _tcuIdentSwNr = swNr;
            _tcuIdentBmwNr = bmwNr;

            Dispatcher.UIThread.Post(() =>
            {
                DMEType_Box.Text = "TCU";
                HWRef_Box.Text = hwNr;
                SWRef_Box.Text = swNr;
                programStatus_Box.Text = bmwNr;
                VIN_Box.Text = string.Empty;
                progRef_Box.Text = sgbdLabel;
                diagProtocol_Box.Text = Global.diagProtocol;
                ReadTcuCal.IsEnabled = true;

                // The fault tab reads whichever module is selected there, so a
                // transmission that answers is reason enough to open it.
                FaultsTab.IsEnabled = true;

                // Only the GS20's calibration layout and checksum are known, so
                // the write tooling stays shut for any other transmission.
                bool isGs20 = string.Equals(_tcuSgbd, "gs20.prg", StringComparison.OrdinalIgnoreCase);
                LoadTcuCal.IsEnabled = isGs20;
                if (!isGs20)
                {
                    _tcuCalToWrite = null;
                    WriteTcuCal.IsEnabled = false;
                }
            });
            SetStatus("TCU identified (" + sgbdLabel + ")");
        }

        /// <summary>
        /// Starts EDIABAS on a specific SGBD/group file, or returns null if it
        /// cannot be resolved (e.g. a group probe that fails through this
        /// transport). Never throws; the caller decides the fallback.
        /// </summary>
        private EdiabasNet StartEdiabasSgbd(string sgbd)
        {
            EdiabasNet ediabas = new EdiabasNet();
            EdInterfaceBase edInterface = new EdInterfaceObd();
            ediabas.EdInterfaceClass = edInterface;
            ediabas.ProgressJobFunc = ProgressJobFunc;
            ediabas.ErrorRaisedFunc = ErrorRaisedFunc;
            ((EdInterfaceObd)edInterface).ComPort = Global.Port;
            ediabas.SetConfigProperty("EcuPath", Global.ecuPath);
            ediabas.ResultsRequests = string.Empty;
            try
            {
                ediabas.ResolveSgbdFile(sgbd);
                return ediabas;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ResolveSgbdFile(" + sgbd + ") failed: " +
                    EdiabasNet.GetExceptionText(ex));
                ediabas.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Unwraps the exception chain so the dialog shows the real cause
        /// rather than a bare "an exception occurred" wrapper.
        /// </summary>
        private static string Describe(Exception ex)
        {
            var parts = new List<string>();
            for (Exception e = ex; e != null; e = e.InnerException)
                parts.Add(e.GetType().Name + ": " + e.Message);
            return string.Join("\n\n", parts);
        }

        private async void LoadSGBD_Click(object sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load SGBD",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("SGBD File") { Patterns = new[] { "*.prg", "*.PRG" } }
                },
                SuggestedStartLocation = await StartLocation(Global.ecuPath)
            });

            var file = files?.FirstOrDefault();
            if (file == null) return;

            string path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            Global.ecuPath = Path.GetDirectoryName(path);
            Global.sgbd = Path.GetFileName(path);
            SetStatus("SGBD: " + Global.sgbd);
        }

        private async void ReadTune_Click(object sender, RoutedEventArgs e)
        {
            try { await ReadDME(); }
            catch (Exception ex)
            {
                SetStatus("Read failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Read DME");
            }
        }

        private async void LoadFile_Click(object sender, RoutedEventArgs e)
        {
            UpdateProgressBar(0);
            await LoadFile_1();
        }

        private async void LoadFile2_Click(object sender, RoutedEventArgs e)
        {
            UpdateProgressBar(0);
            await LoadFile_2();
        }

        private async void FlashData_Click(object sender, RoutedEventArgs e)
        {
            try { await FlashDME_Data(); }
            catch (Exception ex)
            {
                SetStatus("Flash failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Flash Tune");
            }
        }

        private async void FlashProgram_Click(object sender, RoutedEventArgs e)
        {
            try { await Flashfull(); }
            catch (Exception ex)
            {
                SetStatus("Flash failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Flash Program");
            }
        }

        // --- Fault codes ----------------------------------------------------

        /// <summary>The rows from the most recent read, for CSV export.</summary>
        private List<FaultRow> _lastFaults = new List<FaultRow>();

        /// <summary>
        /// A module the Fault Codes tab can talk to. The DME reuses the flasher's
        /// SGBD and supports per-fault detail (P-codes); the TCU uses its own
        /// SGBD and has no FS_LESEN_DETAIL, so no P-codes.
        /// </summary>
        private class FaultModule
        {
            public string Label;
            public string Sgbd;      // null = use Global.sgbd (the DME the flasher loaded)
            public bool DmeDetail;   // DME: FS_LESEN_DETAIL is known present
        }

        private static readonly FaultModule[] Modules =
        {
            new FaultModule { Label = "DME (engine)",       Sgbd = null,         DmeDetail = true  },
            // Direct GS20 SGBD rather than the D_EGS.grp group file. The group
            // file resolves by probing several candidate TCUs and, through this
            // transport, aborts on the first non-responding probe (IFH-0009:
            // NO RESPONSE FROM CONTROLUNIT) instead of continuing to the module
            // that is actually present -- a retry-timing gap in the transport,
            // not a code bug. Addressing GS20 directly skips the probe entirely
            // and reads reliably. GS20 is the only automatic TCU the M54 E46
            // shipped, so this covers this car; the read code still handles the
            // other TCU field shapes (F_ART1_TEXT vs F_SYMPTOM_TEXT) if a
            // different direct SGBD is added later.
            new FaultModule { Label = "TCU (transmission)", Sgbd = "gs20.prg",   DmeDetail = false },
        };

        private FaultModule _module = Modules[0];

        private void ModuleSelect_Changed(object sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            int idx = ModuleSelect.SelectedIndex;
            if (idx < 0 || idx >= Modules.Length) return;
            _module = Modules[idx];

            // Fired once during InitializeComponent before the other controls
            // exist; guard against that.
            if (FaultGrid == null) return;

            // Switching modules invalidates the shown faults.
            FaultGrid.ItemsSource = null;
            _lastFaults = new List<FaultRow>();
            ExportFaults.IsEnabled = false;
            faultCount_Box.Text = string.Empty;
            SetStatus("Module: " + _module.Label);
        }

        /// <summary>
        /// Starts EDIABAS on the fault module's own SGBD when it has one,
        /// otherwise on the flasher's loaded DME SGBD. The TCU is a different
        /// ECU on the bus, so it needs its own SGBD resolved.
        /// </summary>
        private EdiabasNet StartEdiabasFor(FaultModule module)
        {
            if (module.Sgbd == null)
                return StartEdiabas();

            EdiabasNet ediabas = new EdiabasNet();
            EdInterfaceBase edInterface = new EdInterfaceObd();
            ediabas.EdInterfaceClass = edInterface;
            ediabas.ProgressJobFunc = ProgressJobFunc;
            ediabas.ErrorRaisedFunc = ErrorRaisedFunc;
            ((EdInterfaceObd)edInterface).ComPort = Global.Port;
            ediabas.SetConfigProperty("EcuPath", Global.ecuPath);
            ediabas.ResultsRequests = string.Empty;
            try
            {
                ediabas.ResolveSgbdFile(module.Sgbd);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ResolveSgbdFile failed: " + EdiabasNet.GetExceptionText(ex));
                SetStatus("Could not load SGBD '" + module.Sgbd + "' for " + module.Label + ".");
            }
            return ediabas;
        }

        /// <summary>
        /// One row in the fault grid. The grid shows only Code and PCode; the
        /// rest is shown in the detail window on double-click.
        /// </summary>
        public class FaultRow
        {
            public string Code { get; set; }
            public string PCode { get; set; }
            public string PCodeText { get; set; }
            public string Location { get; set; }
            public string Symptom { get; set; }
            public string Present { get; set; }
        }

        private async void ReadFaults_Click(object sender, RoutedEventArgs e)
        {
            string portProblem = CheckPort(Global.Port);
            if (portProblem != null)
            {
                SetStatus("Port unavailable");
                await MessageAsync(portProblem, "Fault Codes");
                return;
            }

            ReadFaults.IsEnabled = false;
            ClearFaults.IsEnabled = false;
            SetStatus("Reading fault codes...");
            try
            {
                var rows = await Task.Run(ReadFaultCodes);
                FaultGrid.ItemsSource = rows;
                _lastFaults = rows;
                ExportFaults.IsEnabled = rows.Count > 0;
                faultCount_Box.Text = rows.Count == 0 ? "No fault codes stored" : rows.Count + " fault(s)";
                SetStatus(rows.Count == 0
                    ? "No fault codes"
                    : "Read " + rows.Count + " fault(s). Double-click a row for details.");
            }
            catch (Exception ex)
            {
                SetStatus("Read faults failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Fault Codes");
            }
            finally
            {
                ReadFaults.IsEnabled = true;
                ClearFaults.IsEnabled = true;
            }
        }

        /// <summary>
        /// FS_LESEN returns one result set per stored fault (its summary). The
        /// P-code is NOT in that summary -- it only comes from FS_LESEN_DETAIL,
        /// which takes the fault's location index (F_ORT_NR) as its argument, so
        /// we do a detail read per fault to fill in the P-code. This mirrors how
        /// the bmweb viewer sources P-codes (FS_LESEN_DETAIL?arg=F_ORT_NR).
        /// </summary>
        private List<FaultRow> ReadFaultCodes()
        {
            var rows = new List<FaultRow>();
            using (EdiabasNet ediabas = StartEdiabasFor(_module))
            {
                if (!ExecuteJob(ediabas, "FS_LESEN", string.Empty) || ediabas.ResultSets == null)
                    return rows;

                // First pass: capture the summary and each fault's location
                // index. Do this before any further job runs, because the next
                // ExecuteJob replaces ResultSets. The symptom field differs by
                // module: the DME has F_SYMPTOM_TEXT, the TCU uses F_ART1_TEXT
                // (fault type).
                var summaries = new List<(string ort, string code, string symptom, string present, string ortNr)>();
                foreach (var set in ediabas.ResultSets)
                {
                    if (!set.TryGetValue("F_ORT_TEXT", out var ortData) || !(ortData.OpData is string ort))
                        continue;
                    if (string.IsNullOrWhiteSpace(ort))
                        continue;

                    string symptom = SetString(set, "F_SYMPTOM_TEXT");
                    if (string.IsNullOrWhiteSpace(symptom))
                        symptom = SetString(set, "F_ART1_TEXT");

                    summaries.Add((
                        ort,
                        SetString(set, "F_HEX_CODE"),
                        symptom,
                        SetString(set, "F_VORHANDEN_TEXT"),
                        // F_ORT_NR is an Int64 on this SGBD, so read it as any type.
                        SetAny(set, "F_ORT_NR")));
                }

                // Second pass: pull the P-code per fault via the detail read.
                // The DME always offers it; a TCU may or may not (GK30 does,
                // GS20/ZF do not). ExecuteJob catches a missing job and returns
                // false, so we simply attempt it and take whatever comes back --
                // no P-code on the modules that lack it, which is expected.
                bool tryDetail = _module.DmeDetail || _module.Sgbd != null;

                foreach (var s in summaries)
                {
                    string pcode = string.Empty;
                    string pcodeText = string.Empty;
                    if (tryDetail &&
                        !string.IsNullOrEmpty(s.ortNr) &&
                        ExecuteJob(ediabas, "FS_LESEN_DETAIL", s.ortNr) &&
                        ediabas.ResultSets != null)
                    {
                        foreach (var dset in ediabas.ResultSets)
                        {
                            string p = SetString(dset, "F_PCODE_STRING");
                            if (!string.IsNullOrWhiteSpace(p))
                            {
                                pcode = p;
                                pcodeText = SetString(dset, "F_PCODE_TEXT");
                                break;
                            }
                        }
                    }

                    // The hex code is the leading token of the location text
                    // when F_HEX_CODE is empty (as on the DME, e.g. "27C3 ...").
                    var (code, location) = SplitOrtText(s.ort, s.code);

                    // Some modules (the TCU) put the whole description in
                    // F_ORT_TEXT with no code prefix; there the fault number
                    // F_ORT_NR IS the code (INPA/bmweb show "81 CAN-Time-Out DME").
                    if (string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(s.ortNr))
                    {
                        code = s.ortNr;
                        location = s.ort;
                    }

                    rows.Add(new FaultRow
                    {
                        Code = code,
                        PCode = pcode,
                        PCodeText = pcodeText,
                        Location = location,
                        Symptom = s.symptom,
                        Present = s.present,
                    });
                }
            }
            return rows;
        }

        private static string SetString(Dictionary<string, EdiabasNet.ResultData> set, string name)
        {
            if (set.TryGetValue(name, out var rd) && rd.OpData is string s)
                return s;
            return string.Empty;
        }

        /// <summary>
        /// Reads a result as text regardless of its EDIABAS type. Numeric
        /// results (like F_ORT_NR, the fault index used to argument
        /// FS_LESEN_DETAIL) come back as Int64, which SetString would drop --
        /// that was why the P-code column stayed empty.
        /// </summary>
        private static string SetAny(Dictionary<string, EdiabasNet.ResultData> set, string name)
        {
            if (!set.TryGetValue(name, out var rd) || rd.OpData == null)
                return string.Empty;
            if (rd.OpData is string s) return s;
            if (rd.OpData is Int64 l) return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (rd.OpData is byte[] b) return BitConverter.ToString(b).Replace("-", string.Empty);
            return rd.OpData.ToString();
        }

        /// <summary>
        /// On this SGBD the hex fault code is the leading token of F_ORT_TEXT
        /// (e.g. "27C3 Thermischer Ölniveausensor"), and F_HEX_CODE is empty.
        /// Pull that prefix out as the code, and return the remaining text as
        /// the location.
        /// </summary>
        private static (string code, string location) SplitOrtText(string ortText, string hexCode)
        {
            if (!string.IsNullOrWhiteSpace(hexCode))
                return (hexCode, ortText ?? string.Empty);

            if (string.IsNullOrWhiteSpace(ortText))
                return (string.Empty, string.Empty);

            int sp = ortText.IndexOf(' ');
            if (sp <= 0)
                return (string.Empty, ortText);

            string token = ortText.Substring(0, sp);
            // A code token is a short run of hex digits (e.g. 27C3, 299A).
            bool looksHex = token.Length >= 2 && token.Length <= 6 &&
                            token.All(c => Uri.IsHexDigit(c));
            return looksHex
                ? (token, ortText.Substring(sp + 1).Trim())
                : (string.Empty, ortText);
        }

        private async void ClearFaults_Click(object sender, RoutedEventArgs e)
        {
            string portProblem = CheckPort(Global.Port);
            if (portProblem != null)
            {
                SetStatus("Port unavailable");
                await MessageAsync(portProblem, "Fault Codes");
                return;
            }

            string moduleName = _module.Sgbd == null ? "DME" : "TCU";
            if (!await ConfirmAsync(
                    "Clear all stored fault codes from the " + moduleName + "?\n\n" +
                    "This erases the fault memory. Faults for problems that are still " +
                    "present will come back on the next drive cycle.",
                    "Clear Fault Codes"))
                return;

            ReadFaults.IsEnabled = false;
            ClearFaults.IsEnabled = false;
            SetStatus("Clearing fault codes...");
            try
            {
                bool ok = await Task.Run(ClearFaultCodes);
                SetStatus(ok ? "Fault codes cleared" : "Clear failed");
                if (ok)
                {
                    // Re-read so the grid reflects what is actually left; a
                    // still-present fault reappears immediately.
                    var rows = await Task.Run(ReadFaultCodes);
                    FaultGrid.ItemsSource = rows;
                    _lastFaults = rows;
                    ExportFaults.IsEnabled = rows.Count > 0;
                    faultCount_Box.Text = rows.Count == 0
                        ? "No fault codes stored"
                        : rows.Count + " fault(s) still present";
                }
            }
            catch (Exception ex)
            {
                SetStatus("Clear faults failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Fault Codes");
            }
            finally
            {
                ReadFaults.IsEnabled = true;
                ClearFaults.IsEnabled = true;
            }
        }

        /// <summary>
        /// FS_LOESCHEN with no argument = KWP2000 $14 ClearDiagnosticInformation
        /// over the whole memory. Passing an F_CODE would clear one fault.
        /// </summary>
        private bool ClearFaultCodes()
        {
            using (EdiabasNet ediabas = StartEdiabasFor(_module))
                return ExecuteJob(ediabas, "FS_LOESCHEN", string.Empty);
        }

        private void FaultGrid_DoubleTapped(object sender, Avalonia.Input.TappedEventArgs e)
        {
            if (FaultGrid.SelectedItem is FaultRow row)
                ShowFaultDetail(row);
        }

        private async void ExportFaults_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFaults == null || _lastFaults.Count == 0)
            {
                SetStatus("Nothing to export. Read fault codes first.");
                return;
            }

            try
            {
                string suggested = "faults_" +
                    (string.IsNullOrEmpty(Global.VIN) ? "MS45" : Global.VIN) + "_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss");

                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export fault codes",
                    SuggestedFileName = suggested,
                    DefaultExtension = "csv",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
                    },
                });

                string path = file?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                    return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Code,P-Code,P-Code Detail,Location,Symptom,Present");
                foreach (var f in _lastFaults)
                {
                    sb.Append(Csv(f.Code)).Append(',')
                      .Append(Csv(f.PCode)).Append(',')
                      .Append(Csv(f.PCodeText)).Append(',')
                      .Append(Csv(f.Location)).Append(',')
                      .Append(Csv(f.Symptom)).Append(',')
                      .Append(Csv(f.Present)).Append("\r\n");
                }

                File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
                SetStatus("Exported " + _lastFaults.Count + " fault(s) to " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                SetStatus("Export failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Export CSV");
            }
        }

        /// <summary>RFC 4180 field: quote when it contains a comma, quote or newline.</summary>
        private static string Csv(string value)
        {
            value ??= string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
                return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private void ShowFaultDetail(FaultRow row)
        {
            var dialog = new Window
            {
                Title = "Fault " + row.Code,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                ColumnDefinitions = new ColumnDefinitions("120,*"),
            };

            string[,] fields =
            {
                { "Code", row.Code },
                { "P-Code", row.PCode },
                { "P-Code detail", row.PCodeText },
                { "Location", row.Location },
                { "Symptom", row.Symptom },
                { "Present", row.Present },
            };

            for (int r = 0; r < fields.GetLength(0); r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock
                {
                    Text = fields[r, 0],
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Margin = new Avalonia.Thickness(0, 4, 12, 4),
                };
                var value = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(fields[r, 1]) ? "n/a" : fields[r, 1],
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 4, 0, 4),
                };
                Grid.SetRow(label, r); Grid.SetColumn(label, 0);
                Grid.SetRow(value, r); Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }

            var close = new Button
            {
                Content = "Close",
                MinWidth = 90,
                IsDefault = true,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(0, 12, 0, 0),
            };
            close.Click += (_, _) => dialog.Close();

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(close, fields.GetLength(0)); Grid.SetColumn(close, 1);
            grid.Children.Add(close);

            dialog.Content = grid;
            _ = dialog.ShowDialog(this);
        }

        private void FullBin_CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            Global.openedFlash = null;
            Global.openedMPC = null;
            FlashDME.IsEnabled = false;
            FlashProgram.IsEnabled = false;
            UpdateProgressBar(0);

            statusTextBlock.Text = null;
            LoadFile2.IsEnabled = FullBin_CheckBox.IsChecked == true;

            // Toggling Full Binary clears the loaded files above, so the gate
            // has to be re-evaluated against the new state.
            RefreshEwsDeleteGate();
        }

        /// <summary>
        /// Why the EWS checkbox is not available, or null when it is.
        ///
        /// The gate is the DME's own PROGRAM reference, read by ZIF_LESEN
        /// (KWP2000 $2503 ProgrammReferenz) during identify. That is a
        /// different field from DATEN_REFERENZ ($2504), which reports the data
        /// reference and reads LO00S on a car whose program is LO02S, so the
        /// data reference cannot tell the two MS45.1 programs apart, and
        /// neither can the hardware reference, which is 0044570 for both.
        ///
        /// The loaded file is checked too, so that a file for a different
        /// program cannot be flashed onto a car that reports the right one.
        /// </summary>
        private string EwsDeleteBlockedReason()
        {
            if (string.IsNullOrEmpty(Global.HW_Ref))
                return "Identify the DME first.";

            if (Global.HW_Ref != "0044570")
            {
                return "EWS delete is only verified for the MS45.1 (hardware reference 0044570). " +
                       "This DME reports " + Global.HW_Ref + ".";
            }

            if (string.IsNullOrEmpty(Global.Prog_Ref))
            {
                return "The DME did not report a program reference (ZIF_LESEN), so its program " +
                       "version cannot be confirmed. EWS delete stays disabled.";
            }

            if (!Global.Prog_Ref.Contains(EwsDelete.SupportedProgramVersion))
            {
                return "EWS delete is only verified for program " + EwsDelete.SupportedProgramVersion +
                       ", but this DME reports " + Global.Prog_Ref + ". The other MS45.1 program lays " +
                       "its globals out differently, so the patch offsets would land on unrelated code.";
            }

            if (FullBin_CheckBox.IsChecked != true)
                return "EWS delete patches the program area, so it needs Full Binary mode.";

            if (Global.openedFlash == null)
                return "Load the full binary first, so its program version can be checked.";

            string version = EwsDelete.ReadProgramVersion(Global.openedFlash);

            if (EwsDelete.IsAlreadyPatched(Global.openedFlash))
                return null;

            if (version != EwsDelete.SupportedProgramVersion)
            {
                return "EWS delete is only verified for program version " +
                       EwsDelete.SupportedProgramVersion + ", but the loaded program reports " +
                       (version ?? "an unreadable version") + ". Other MS45.1 programs lay their " +
                       "globals out differently, so the patch offsets would land on unrelated code.";
            }

            if (!EwsDelete.IsApplicable(Global.openedFlash))
            {
                return "The loaded program is the right version but does not carry the expected " +
                       "instructions at the patch sites, so it may already be modified.";
            }

            return null;
        }

        /// <summary>
        /// Enables the EWS checkbox only when the patch could actually be
        /// applied, and clears it when it could not.
        /// </summary>
        private void RefreshEwsDeleteGate()
        {
            bool allowed = EwsDeleteBlockedReason() == null;
            EwsDelete_CheckBox.IsEnabled = allowed;
            if (!allowed)
                EwsDelete_CheckBox.IsChecked = false;
        }

        private async void EwsDelete_CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (EwsDelete_CheckBox.IsChecked != true)
                return;

            // Re-check at the moment of ticking: the loaded file or the
            // identified DME may have changed since the box was enabled.
            string blocked = EwsDeleteBlockedReason();
            if (blocked != null)
            {
                EwsDelete_CheckBox.IsChecked = false;
                EwsDelete_CheckBox.IsEnabled = false;
                await MessageAsync(blocked, "EWS Delete");
                return;
            }

            if (!await ConfirmAsync(
                    "EWS delete disables the immobilizer check in the DME program.\n\n" +
                    "The car will start without a valid EWS handshake, which removes a theft " +
                    "deterrent. Only do this on a vehicle you own.\n\n" +
                    "Continue?",
                    "EWS Delete"))
            {
                EwsDelete_CheckBox.IsChecked = false;
                return;
            }

            SetStatus("EWS delete will be applied to the program before flashing.");
        }

        private async Task<IStorageFolder> StartLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return await StorageProvider.TryGetFolderFromPathAsync(path); }
            catch (Exception) { return null; }
        }

        // --- Identify -------------------------------------------------------

        /// <summary>
        /// Checks that the configured serial port exists and can be opened
        /// before any job runs. EDIABAS reports a busy port, a missing port and
        /// an unresponsive DME all as the same generic job failure, so this
        /// distinguishes them up front (opening a port another process holds is
        /// the common one on macOS -- INPA/ISTA or a stray process keeps it).
        /// Returns null when the port is usable, or a specific message.
        /// </summary>
        private static string CheckPort(string port)
        {
            if (string.IsNullOrWhiteSpace(port))
                return "No serial port is set. Use Set Serial Port to choose your cable.";

            try
            {
                using var sp = new System.IO.Ports.SerialPort(port);
                sp.Open();
                sp.Close();
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                // macOS/Windows both raise this when the device node is held by
                // another process, or (rarely) on a permissions problem.
                return "The port " + port + " is in use by another program (or access was denied).\n\n" +
                       "Close any other diagnostic tool that may have it open (INPA, ISTA, a serial " +
                       "monitor, or a previous session of this app), then try again.";
            }
            catch (System.IO.FileNotFoundException)
            {
                return "The port " + port + " no longer exists. Is the cable still plugged in? " +
                       "Re-check it with Set Serial Port.";
            }
            catch (Exception ex)
            {
                return "Could not open " + port + ": " + ex.Message;
            }
        }

        /// <summary>
        /// Reads DME identity. <paramref name="preflightPort"/> should be false
        /// when this is a refresh right after a flash: the flash has only just
        /// released the port, and probing it again can race the OS releasing the
        /// device node, which surfaced as a spurious "Port unavailable" at the
        /// end of an otherwise-successful flash.
        /// </summary>
        private void IdentDME(bool preflightPort = true)
        {
            if (preflightPort)
            {
                string portProblem = CheckPort(Global.Port);
                if (portProblem != null)
                {
                    SetStatus("Port unavailable");
                    Dispatcher.UIThread.Post(() => _ = MessageAsync(portProblem, "Identify DME"));
                    return;
                }
            }

            string DMEType;
            using (EdiabasNet ediabas = StartEdiabas())
            {
                ExecuteJob(ediabas, "aif_lesen", string.Empty);
                Global.VIN = GetResult_String("AIF_FG_NR", ediabas.ResultSets);

                ExecuteJob(ediabas, "hardware_referenz_lesen", string.Empty);
                Global.HW_Ref = GetResult_String("HARDWARE_REFERENZ", ediabas.ResultSets);

                ExecuteJob(ediabas, "daten_referenz_lesen", string.Empty);
                Global.SW_Ref = GetResult_String("DATEN_REFERENZ", ediabas.ResultSets);

                // The PROGRAM reference ($2503), which is what the EWS patch is
                // tied to. DATEN_REFERENZ above is the DATA reference and does
                // not distinguish the two MS45.1 programs.
                if (ExecuteJob(ediabas, "ZIF_LESEN", string.Empty))
                    Global.Prog_Ref = GetResult_String("ZIF_PROGRAMM_REFERENZ", ediabas.ResultSets);
                else
                    Global.Prog_Ref = null;

                ExecuteJob(ediabas, "flash_programmier_status_lesen", string.Empty);
                string programming_status = GetResult_String("FLASH_PROGRAMMIER_STATUS_TEXT", ediabas.ResultSets);

                DMEType = "Unknown / Unsuppported";

                if (Global.HW_Ref == "0044560")
                    DMEType = "MS45.0";
                if (Global.HW_Ref == "0044570")
                    DMEType = "MS45.1";

                // If the HW reference never came back, the port opened but the
                // DME never answered -- a wiring / ignition / cable issue rather
                // than a port-in-use one, which CheckPort already ruled out.
                if (string.IsNullOrEmpty(Global.HW_Ref) ||
                    !ExecuteJob(ediabas, "DIAGNOSEPROTOKOLL_LESEN", string.Empty))
                {
                    SetStatus("No response from the DME");
                    Dispatcher.UIThread.Post(() => _ = MessageAsync(
                        "The port opened, but the DME did not respond.\n\n" +
                        "Check that:\n" +
                        "• ignition is on (position 2 / KL15)\n" +
                        "• the OBD cable is fully seated at both ends\n" +
                        "• this is a K+DCAN cable in the right mode\n" +
                        "• the battery voltage is healthy",
                        "Identify DME"));
                    return;
                }

                Global.diagProtocol = GetResult_String("DIAG_PROT_IST", ediabas.ResultSets);

                Dispatcher.UIThread.Post(() =>
                {
                    DMEType_Box.Text = DMEType;
                    HWRef_Box.Text = Global.HW_Ref;
                    SWRef_Box.Text = Global.SW_Ref;
                    programStatus_Box.Text = programming_status;
                    VIN_Box.Text = Global.VIN;
                    progRef_Box.Text = Global.Prog_Ref ?? "(not reported)";
                    diagProtocol_Box.Text = Global.diagProtocol;

                    if (DMEType != String.Empty && DMEType != "Unknown / Unsuppported")
                    {
                        ReadTune.IsEnabled = true;
                        LoadFile.IsEnabled = true;
                        FullBin_CheckBox.IsEnabled = true;
                        // Fault-code jobs need a resolved SGBD and a module that
                        // answers, both of which a successful identify proves.
                        FaultsTab.IsEnabled = true;
                    }

                    // Identifying a different DME can invalidate an already
                    // ticked EWS box, so re-evaluate on every identify.
                    RefreshEwsDeleteGate();
                });
            }
        }

        // --- File loading ---------------------------------------------------

        private async Task LoadFile_1()
        {
            // Every exit path below can change whether the EWS patch applies,
            // so the gate is refreshed once here rather than at each return.
            try { await LoadFile_1_Core(); }
            finally { RefreshEwsDeleteGate(); }
        }

        private async Task LoadFile_1_Core()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load File",
                AllowMultiple = false,
                FileTypeFilter = BinaryFilters()
            });

            var file = files?.FirstOrDefault();
            string path = file?.TryGetLocalPath();

            if (string.IsNullOrEmpty(path))
            {
                FlashDME.IsEnabled = false;
                Global.openedFlash = null;
                return;
            }

            Global.openedFlash = File.ReadAllBytes(path);

            if (FullBin_CheckBox.IsChecked == false)
            {
                if (Global.openedFlash.Length >= 0x1D000 && Global.openedFlash.Length <= 0x20000)
                {
                    if (!VerifyParameterMatch(Global.openedFlash, Global.SW_Ref))
                    {
                        if (!await ConfirmAsync(
                                "Loaded tune does not match DME's program.\n\nDo you wish to flash anyway?",
                                "Warning"))
                        {
                            Global.openedFlash = null;
                            Global.openedMPC = null;
                            FlashDME.IsEnabled = false;
                            FlashProgram.IsEnabled = false;
                            return;
                        }
                    }

                    FlashDME.IsEnabled = true;
                }
                else
                {
                    SetStatus("Invalid tune file length");
                    FlashDME.IsEnabled = false;
                    Global.openedFlash = null;
                }
            }

            if (FullBin_CheckBox.IsChecked == true && Global.openedFlash != null &&
                Global.openedFlash.Length != 0x100000)
            {
                SetStatus("Invalid flash file length");
                FlashDME.IsEnabled = false;
                Global.openedFlash = null;
            }

            if (FullBin_CheckBox.IsChecked == true && Global.openedFlash != null)
            {
                if (!VerifyProgramMatch(Global.openedFlash, Global.HW_Ref))
                {
                    if (!await ConfirmAsync(
                            "Loaded program does not match DME hardware.\n\nDo you wish to flash anyway?",
                            "Warning"))
                    {
                        Global.openedFlash = null;
                        Global.openedMPC = null;
                        FlashDME.IsEnabled = false;
                        FlashProgram.IsEnabled = false;
                        return;
                    }
                }

                if (Global.openedMPC != null)
                {
                    if (!VerifyFlashMPCMatch(Global.openedFlash, Global.openedMPC))
                    {
                        if (!await ConfirmAsync(
                                "External flash and MPC flash do not match. Flashing anyway may permanently brick your DME.\n\nDo you wish to continue?",
                                "Warning"))
                        {
                            Global.openedFlash = null;
                            Global.openedMPC = null;
                            FlashDME.IsEnabled = false;
                            FlashProgram.IsEnabled = false;
                            return;
                        }
                    }

                    FlashDME.IsEnabled = true;
                    FlashProgram.IsEnabled = true;
                    }
            }

            if (Global.openedFlash != null)
                SetStatus("Loaded " + Path.GetFileName(path));
        }

        private async Task LoadFile_2()
        {
            try { await LoadFile_2_Core(); }
            finally { RefreshEwsDeleteGate(); }
        }

        private async Task LoadFile_2_Core()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load File 2 (MPC Flash)",
                AllowMultiple = false,
                FileTypeFilter = BinaryFilters()
            });

            var file = files?.FirstOrDefault();
            string path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            Global.openedMPC = File.ReadAllBytes(path);

            // The original dereferenced openedMPC here without a null check, so
            // cancelling the dialog threw a NullReferenceException.
            if (Global.openedMPC.Length != 0x70000)
            {
                SetStatus("Invalid mpc file length");
                FlashDME.IsEnabled = false;
                Global.openedMPC = null;
                return;
            }

            if (Global.openedFlash != null)
            {
                if (!VerifyFlashMPCMatch(Global.openedFlash, Global.openedMPC))
                {
                    if (!await ConfirmAsync(
                            "External flash and MPC flash do not match. Flashing anyway may permanently brick your DME.\n\nDo you wish to continue?",
                            "Warning"))
                    {
                        Global.openedFlash = null;
                        Global.openedMPC = null;
                        FlashDME.IsEnabled = false;
                        FlashProgram.IsEnabled = false;
                        return;
                    }
                }
                FlashDME.IsEnabled = true;
                FlashProgram.IsEnabled = true;
            }

            SetStatus("Loaded " + Path.GetFileName(path));
        }

        private static FilePickerFileType[] BinaryFilters() => new[]
        {
            new FilePickerFileType("Binary") { Patterns = new[] { "*.bin" } },
            new FilePickerFileType("Original File") { Patterns = new[] { "*.ori" } },
            new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
        };

        private bool VerifyParameterMatch(byte[] flash, string swref)
        {
            string binref = System.Text.Encoding.ASCII.GetString(flash.Skip(0x10).Take(0xC).ToArray());
            return swref != null && swref.Contains(binref);
        }

        private bool VerifyProgramMatch(byte[] flash, string hwref)
        {
            string binref = System.Text.Encoding.ASCII.GetString(flash.Skip(0x6031C).Take(0xC).ToArray());
            return hwref != null && binref.Contains(hwref);
        }

        private bool VerifyFlashMPCMatch(byte[] flash, byte[] mpc)
        {
            string flashstring = System.Text.Encoding.ASCII.GetString(flash.Skip(0x60310).Take(0xA).ToArray());
            string mpcstring = System.Text.Encoding.ASCII.GetString(mpc.Skip(0x100).Take(0xA).ToArray());

            try
            {
                return Convert.ToUInt64(flashstring) - Convert.ToUInt64(mpcstring) == 500;
            }
            catch (Exception)
            {
                // Non-numeric bytes at those offsets mean this is not a matching
                // pair; the original let the FormatException escape.
                return false;
            }
        }

        // --- Sleep suppression ---------------------------------------------

        /// <summary>
        /// The original P/Invoked kernel32!SetThreadExecutionState to keep
        /// Windows awake during a read/flash. The macOS equivalent is the
        /// caffeinate(8) utility; a dropped connection mid-flash can brick the
        /// DME, so this matters.
        /// </summary>
        private sealed class SleepBlocker : IDisposable
        {
            private System.Diagnostics.Process _process;

            public static SleepBlocker Acquire()
            {
                var blocker = new SleepBlocker();
                if (!OperatingSystem.IsMacOS()) return blocker;
                try
                {
                    blocker._process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "/usr/bin/caffeinate",
                        // -i idle, -m disk, -s system
                        Arguments = "-ims",
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Could not block sleep: " + ex.Message);
                }
                return blocker;
            }

            public void Dispose()
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                        _process.Kill();
                    _process?.Dispose();
                }
                catch (Exception)
                {
                    // Process already gone; nothing to release.
                }
                _process = null;
            }
        }

        // --- Read, Write, Erase ---------------------------------------------

        private async Task ReadDME()
        {
            uint start;
            uint end;
            string MemSegment;
            byte[] MemoryDump = null;
            byte[] mpcdump = null;

            bool fullBin = FullBin_CheckBox.IsChecked == true;

            using (EdiabasNet ediabas = StartEdiabas())
            {
                if (Global.diagProtocol != "BMW-FAST")
                {
                    await Task.Run(() =>
                    {
                        if (!RequestSecurityAccess(ediabas))
                            SetStatus("Security Access Denied");
                    });
                }

                if (!fullBin)
                {
                    start = 0x40000;
                    end = 0x5CFFF;
                    MemSegment = "ROMX";

                    SetStatus("Reading parameters");
                    await Task.Run(() => MemoryDump = ReadMemory(ediabas, start, end, MemSegment));
                }
                else
                {
                    start = 0x00000;
                    end = 0xFFFFF;
                    MemSegment = "ROMX";
                    SetStatus("Reading External Flash");
                    await Task.Run(() => MemoryDump = ReadMemory(ediabas, start, end, MemSegment));

                    start = 0x00000;
                    end = 0x6FFFF;
                    MemSegment = "LAR";
                    SetStatus("Reading Internal Flash");
                    await Task.Run(() => mpcdump = ReadMemory(ediabas, start, end, MemSegment));
                }

                SetStatus(null);

                string suggested = fullBin
                    ? Global.VIN + "_" + Global.HW_Ref + "_Flash"
                    : Global.VIN + "_" + Global.HW_Ref;

                await SaveDumpAsync(MemoryDump, suggested);

                MemoryDump = null;

                if (fullBin)
                    await SaveDumpAsync(mpcdump, Global.VIN + "_" + Global.HW_Ref + "_MPC");

                if (Global.diagProtocol != "BMW-FAST")
                {
                    if (!ExecuteJob(ediabas, "diagnose_mode", "DEFAULT;PC9600")) return;
                    if (!ExecuteJob(ediabas, "SET_PARAMETER", ";9600")) return;
                }
            }
        }

        private async Task SaveDumpAsync(byte[] data, string suggestedName)
        {
            if (data == null || data.Length == 0)
            {
                SetStatus("Nothing to save. The read returned no data.");
                return;
            }

            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save dump",
                    SuggestedFileName = suggestedName,
                    DefaultExtension = "bin",
                    FileTypeChoices = BinaryFilters()
                });

                string path = file?.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    File.WriteAllBytes(path, data);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Exception caught in process: " + ex);
                await MessageAsync("Error trying to save file");
            }
        }

        private byte[] ReadMemory(EdiabasNet ediabas, uint start, uint end, string MemSegment)
        {
            using (SleepBlocker.Acquire())
            {
                byte[] MemoryDump = { };
                byte[] MemoryRead;
                uint length = end - start + 1;
                uint lengthRemaining = length;
                uint segLength = 254;
                uint bytesRead = 0;

                while (bytesRead < length)
                {
                    if (lengthRemaining < segLength)
                        segLength = lengthRemaining;
                    if (!ExecuteJob(ediabas, "speicher_lesen_ascii", MemSegment + ";" + start + ";" + segLength))
                        return MemoryDump;

                    bytesRead += segLength;
                    MemoryRead = GetResult_ByteArray("DATEN", ediabas.ResultSets);

                    start = start + segLength;
                    lengthRemaining = lengthRemaining - segLength;

                    uint progress = bytesRead * 100 / length;
                    UpdateProgressBar(progress);
                    MemoryDump = MemoryDump.Concat(MemoryRead).ToArray();
                }

                return MemoryDump;
            }
        }

        private async Task FlashDME_Data()
        {
            Checksums_Signatures ChecksumsSignatures = new Checksums_Signatures();
            bool success = true;
            bool fullBin = FullBin_CheckBox.IsChecked == true;

            ShowProgressAsFlashing(true);
            try
            {
            using (FlashLog.Session("flash-tune", out string logPath))
            {
            FlashLog.Note("DME " + Global.HW_Ref + " / prog " + Global.Prog_Ref +
                          " / diag " + Global.diagProtocol);
            if (logPath != null) SetStatus("Logging to " + Path.GetFileName(logPath));

            using (EdiabasNet ediabas = StartEdiabas())
            {
                await Task.Run(() =>
                {
                    if (!RequestSecurityAccess(ediabas))
                    {
                        success = false;
                        SetStatus("Security Access Denied");
                    }
                });

                if (!success) return;

                uint eraseStart = 0x2040000;
                uint eraseBlock = 0x20000;
                uint flashStart = 0x2040000;
                uint flashEnd = 0x205CFFF;

                if (Global.diagProtocol == "BMW-FAST")
                {
                    if (!ExecuteJob(ediabas, "normaler_datenverkehr", "nein;nein;ja")) return;
                    if (!ExecuteJob(ediabas, "normaler_datenverkehr", "ja;nein;nein")) return;
                }

                SetStatus("Erasing Flash");
                await Task.Run(() => success = EraseECU(ediabas, eraseBlock, eraseStart));
                if (!success) return;

                byte[] toFlash;

                if (fullBin || Global.openedFlash.Length > 0x40000)
                    toFlash = Global.openedFlash.Skip(0x40000).Take(0x1D000).ToArray();
                else
                    toFlash = Global.openedFlash.ToArray();

                toFlash = ChecksumsSignatures.CorrectParameterChecksums(toFlash);
                toFlash = ChecksumsSignatures.SignMS45Parameters(toFlash);

                SetStatus("Flashing ECU");
                await Task.Run(() => success = FlashBlock(ediabas, toFlash, flashStart, flashEnd));

                if (success)
                {
                    await Task.Run(() => success = FinishFlash(ediabas, "Daten"));
                    SetStatus(success ? "Flash successful" : "Flash failed");
                }
            }

            // Re-identify AFTER the flash's EdiabasNet (and its serial port) has
            // been disposed by the using block above; preflight is skipped so a
            // still-releasing port node cannot report a false "Port unavailable".
            await Task.Run(() => IdentDME(preflightPort: false));
            } // FlashLog session
            }
            finally { ShowProgressAsFlashing(false); }
        }

        private async Task Flashfull()
        {
            Checksums_Signatures ChecksumsSignatures = new Checksums_Signatures();
            bool success = true;

            ShowProgressAsFlashing(true);
            try
            {
            using (FlashLog.Session("flash-program", out string logPath))
            {
            FlashLog.Note("DME " + Global.HW_Ref + " / prog " + Global.Prog_Ref +
                          " / diag " + Global.diagProtocol +
                          " / EWS delete " + (EwsDelete_CheckBox.IsChecked == true));
            if (logPath != null) SetStatus("Logging to " + Path.GetFileName(logPath));

            using (EdiabasNet ediabas = StartEdiabas())
            {
                await Task.Run(() =>
                {
                    if (!RequestSecurityAccess(ediabas))
                    {
                        success = false;
                        SetStatus("Security Access Denied");
                    }
                });

                if (!success) return;

                uint eraseStart = 0x2060000;
                uint eraseBlock = 0xA0000;
                uint flashStart = 0x2060000;
                uint flashEnd = 0x20FFF3F;

                uint flashMPCStart = 0;
                uint flashMPCEnd = 0x6FFFF;

                if (Global.diagProtocol == "BMW-FAST")
                {
                    if (!ExecuteJob(ediabas, "normaler_datenverkehr", "nein;nein;ja")) return;
                    if (!ExecuteJob(ediabas, "normaler_datenverkehr", "ja;nein;nein")) return;
                }

                FlashLog.Note("PHASE: erase program region 0x2060000 block 0xA0000");
                SetStatus("Erasing Flash");
                await Task.Run(() => success = EraseECU(ediabas, eraseBlock, eraseStart));
                if (!success) return;

                // The EWS patch edits program bytes, so it has to happen before
                // checksums and signing are computed over them.
                byte[] source = Global.openedFlash;
                if (EwsDelete_CheckBox.IsChecked == true)
                {
                    try
                    {
                        source = EwsDelete.Apply(source);
                        SetStatus("Applied EWS delete");
                    }
                    catch (Exception ex)
                    {
                        SetStatus("EWS delete failed: " + ex.Message);
                        await MessageAsync(ex.Message, "EWS Delete");
                        return;
                    }
                }

                byte[] toFlash = ChecksumsSignatures.CorrectProgramChecksums(source, Global.openedMPC);
                toFlash = ChecksumsSignatures.SignMS45Program(toFlash, Global.openedMPC).Skip(0x60000).Take(0x9FF40).ToArray();

                FlashLog.Note("PHASE: write external program 0x2060000..0x20FFF3F");
                SetStatus("Flashing External Program");
                await Task.Run(() => success = FlashBlock(ediabas, toFlash, flashStart, flashEnd));

                // The original pressed on to the internal flash even if the
                // external block failed. Stopping here leaves the DME in a
                // recoverable state instead of half-written.
                if (!success)
                {
                    SetStatus("Flash failed");
                }
                else
                {
                    // The MPC (internal) write must happen: the program signature
                    // the DME verifies (FLASH_SIGNATUR_PRUEFEN Programm) spans
                    // external + MPC together, so an external-only write leaves
                    // the program invalid ("Programm nicht vorhanden"). There is
                    // no external-only shortcut for a program flash.
                    FlashLog.Note("PHASE: write internal MPC 0x0..0x6FFFF (brick-capable step)");
                    SetStatus("Flashing Internal Program");
                    await Task.Run(() => success = FlashBlock(ediabas, Global.openedMPC, flashMPCStart, flashMPCEnd));

                    if (success)
                    {
                        await Task.Run(() => success = FinishFlash(ediabas, "Programm"));
                        SetStatus(success ? "Flash successful" : "Flash failed");
                    }
                }
            }

            // Re-identify after the port is released; see FlashDME_Data.
            await Task.Run(() => IdentDME(preflightPort: false));
            } // FlashLog session
            }
            finally { ShowProgressAsFlashing(false); }
        }

        /// <summary>
        /// The post-flash sequence shared by the tune and full-program paths:
        /// drop back to normal comms, verify the signature, reset the ECU.
        /// It was duplicated verbatim in both flash methods upstream.
        /// </summary>
        private bool FinishFlash(EdiabasNet ediabas, string signatureArea)
        {
            if (Global.diagProtocol != "BMW-FAST")
            {
                if (!ExecuteJob(ediabas, "diagnose_mode", "DEFAULT;PC9600")) return false;
                if (!ExecuteJob(ediabas, "SET_PARAMETER", ";9600")) return false;
            }
            else
            {
                if (signatureArea == "Daten" && !ExecuteJob(ediabas, "diagnose_mode", "DEFAULT")) return false;
                if (!ExecuteJob(ediabas, "normaler_datenverkehr", "ja;nein;ja")) return false;
            }

            if (!ExecuteJob(ediabas, "FLASH_PROGRAMMIER_STATUS_LESEN", String.Empty)) return false;

            SetStatus("Checking signature");
            if (!ExecuteJob(ediabas, "FLASH_SIGNATUR_PRUEFEN", signatureArea + ";64"))
            {
                SetStatus("Signature check failed");
                ExecuteJob(ediabas, "STEUERGERAETE_RESET", String.Empty);
                return false;
            }

            if (!ExecuteJob(ediabas, "FLASH_PROGRAMMIER_STATUS_LESEN", String.Empty)) return false;

            SetStatus("Resetting ECU");
            return ExecuteJob(ediabas, "STEUERGERAETE_RESET", String.Empty);
        }

        private bool FlashBlock(EdiabasNet ediabas, byte[] toFlash, uint blockStart, uint blockEnd)
        {
            using (SleepBlocker.Acquire())
            {
                uint blockStartOrig = blockStart;
                uint blockLength = blockEnd - blockStart + 1;

                byte[] flashAddressSet = new Byte[22];
                flashAddressSet[0] = 1;
                flashAddressSet[21] = 3;

                BitConverter.GetBytes(blockStart).CopyTo(flashAddressSet, 17);
                BitConverter.GetBytes(blockLength).CopyTo(flashAddressSet, 13);
                //See ediabas comments on flash_schreiben_adresse to see details on how this array should be set

                byte[] flashHeader = new Byte[21];
                byte[] three = { 3 };
                int flashSegLength = 0xFD;
                flashHeader[0] = 1;
                flashHeader[13] = (byte)flashSegLength;

                string flashAddressJob = "flash_schreiben_adresse";
                string flashJob = "flash_schreiben";
                string flashEndJob = "flash_schreiben_ende";

                if (!ExecuteJob(ediabas, flashAddressJob, flashAddressSet))
                {
                    SetStatus("Failed to set flash address");
                    return false;
                }

                while (blockLength > 0)
                {
                    if (blockLength < flashSegLength)
                    {
                        flashSegLength = (int)blockLength;
                        flashHeader[13] = (byte)flashSegLength;
                    }
                    BitConverter.GetBytes(blockStart).CopyTo(flashHeader, 17);

                    if (!ExecuteJob(ediabas, flashJob,
                            flashHeader
                                .Concat(toFlash.Skip((int)(blockStart) - (int)blockStartOrig).Take(flashSegLength))
                                .Concat(three).ToArray()))
                    {
                        SetStatus("Flash failed at 0x" + blockStart.ToString("X") + ". Resetting DME.");
                        if (!ExecuteJob(ediabas, "STEUERGERAETE_RESET", String.Empty))
                            SetStatus("Error Resetting ECU");
                        return false;
                    }
                    blockStart += (uint)flashSegLength;
                    blockLength -= (uint)flashSegLength;

                    uint progress = (blockStart - blockStartOrig) * 100 / (blockEnd - blockStartOrig);
                    UpdateProgressBar(progress);
                }

                if (!ExecuteJob(ediabas, flashEndJob, flashAddressSet))
                {
                    SetStatus("Failed to end flash job");
                    return false;
                }

                return true;
            }
        }

        private bool EraseECU(EdiabasNet ediabas, uint blockLength, uint blockStart)
        {
            string flashEraseJob = "flash_loeschen";

            byte[] eraseCommand = new Byte[22];
            eraseCommand[0] = 1;
            eraseCommand[4] = 0xFE;

            BitConverter.GetBytes(blockStart).CopyTo(eraseCommand, 17); //Start address
            BitConverter.GetBytes(blockLength).CopyTo(eraseCommand, 13); //Length - doesn't really matter for erases.

            if (!ExecuteJob(ediabas, flashEraseJob, eraseCommand))
            {
                SetStatus("Erase failed");
                return false;
            }
            return true;
        }

        // --- Security Access -------------------------------------------------

        private bool RequestSecurityAccess(EdiabasNet ediabas)
        {
            Checksums_Signatures ChecksumsSignatures = new Checksums_Signatures();

            SetStatus("Requesting Security Access");
            if (!ExecuteJob(ediabas, "seriennummer_lesen", string.Empty))
                return false;
            byte[] serialReply = GetResult_ByteArray("_TEL_ANTWORT", ediabas.ResultSets);
            byte[] serialNumber = serialReply.Skip(serialReply.Length - 5).Take(4).ToArray(); //DME uses last 4 bytes of serial number in authentication message
            byte[] userID = new byte[4]; //user ID can be any 4 bytes.
            Random rng = new Random();
            rng.NextBytes(userID);

            if (!ExecuteJob(ediabas, "authentisierung_zufallszahl_lesen",
                    "3;0x" + BitConverter.ToUInt32(userID.Reverse().ToArray(), 0).ToString("X")))
                return false;
            byte[] seed = GetResult_ByteArray("ZUFALLSZAHL", ediabas.ResultSets); //DME sends a random number

            if (!ExecuteJob(ediabas, "authentisierung_start",
                    ChecksumsSignatures.GetSecurityAccessMessage(userID, serialNumber, seed)))
                return false;

            if (Global.diagProtocol != "BMW-FAST") //If not using the BMW-FAST protocol (BN2000 cars, i.e E60/E65 MS45), raise baudrate (from 9600 default) to 115200
            {
                if (!ExecuteJob(ediabas, "diagnose_mode", "ECUPM;PC115200")) return false;
                if (!ExecuteJob(ediabas, "SET_PARAMETER", ";115200")) return false;
                if (!ExecuteJob(ediabas, "ACCESS_TIMING_PARAMETER", "00;120;24;240;00")) return false;
                if (!ExecuteJob(ediabas, "SET_PARAMETER", ";115200;;15")) return false;
            }
            else //"BMW-FAST" cars communicate @ 115200 natively and don't need all those parameters set
            {
                if (!ExecuteJob(ediabas, "diagnose_mode", "ECUPM")) return false;
            }
            return true;//Should be in ECU Programming Mode now
        }

        // --- EDIABAS plumbing (unchanged from upstream) ----------------------

        private EdiabasNet StartEdiabas()
        {
            EdiabasNet ediabas = new EdiabasNet();
            EdInterfaceBase edInterface = new EdInterfaceObd();

            ediabas.EdInterfaceClass = edInterface;
            ediabas.ProgressJobFunc = ProgressJobFunc;
            ediabas.ErrorRaisedFunc = ErrorRaisedFunc;

            ((EdInterfaceObd)edInterface).ComPort = Global.Port;

            ediabas.ArgBinary = null;
            ediabas.ArgBinaryStd = null;
            ediabas.ResultsRequests = string.Empty;

            ediabas.SetConfigProperty("EcuPath", Global.ecuPath);
            ediabas.ResultsRequests = string.Empty;

            try
            {
                ediabas.ResolveSgbdFile(Global.sgbd);
            }
            catch (Exception ex2)
            {
                System.Diagnostics.Debug.WriteLine("ResolveSgbdFile failed: " + EdiabasNet.GetExceptionText(ex2));
                SetStatus("Could not load SGBD '" + Global.sgbd + "'. Set the ECU path with Load SGBD.");
            }

            return ediabas;
        }

        private static void ProgressJobFunc(EdiabasNet ediabas)
        {
            string infoProgressText = ediabas.InfoProgressText;
            int infoProgressPercent = ediabas.InfoProgressPercent;
            string text = string.Empty;
            if (infoProgressPercent >= 0)
                text += string.Format("{0,3}% ", infoProgressPercent);
            if (infoProgressText.Length > 0)
                text += string.Format("'{0}'", infoProgressText);
            if (text.Length > 0)
                System.Diagnostics.Debug.WriteLine("Progress: " + text);
        }

        private static void ErrorRaisedFunc(EdiabasNet.ErrorCodes error)
        {
            string errorDescription = EdiabasNet.GetErrorDescription(error);
            System.Diagnostics.Debug.WriteLine("Error occured: 0x{0:X08} {1}", new object[]
            {
                (uint)error,
                errorDescription
            });
        }

        private static string GetResult_String(string resultName, List<Dictionary<string, EdiabasNet.ResultData>> resultSets)
        {
            string result = string.Empty;
            if (resultSets != null)
            {
                foreach (Dictionary<string, EdiabasNet.ResultData> dictionary in resultSets)
                {
                    foreach (string key in from x in dictionary.Keys orderby x select x)
                    {
                        EdiabasNet.ResultData resultData = dictionary[key];
                        if (resultData.Name == resultName && resultData.OpData is string)
                            result = (string)resultData.OpData;
                    }
                }
            }
            return result;
        }

        private static byte[] GetResult_ByteArray(string resultName, List<Dictionary<string, EdiabasNet.ResultData>> resultSets)
        {
            byte[] result = null;
            if (resultSets != null)
            {
                foreach (Dictionary<string, EdiabasNet.ResultData> dictionary in resultSets)
                {
                    foreach (string key in from x in dictionary.Keys orderby x select x)
                    {
                        EdiabasNet.ResultData resultData = dictionary[key];
                        if (resultData.Name == resultName && resultData.OpData.GetType() == typeof(byte[]))
                            result = (byte[])resultData.OpData;
                    }
                }
            }
            return result;
        }

        private static bool ExecuteJob(EdiabasNet ediabas, string Job, string Arg)
        {
            ediabas.ArgString = Arg;
            try
            {
                ediabas.ExecuteJob(Job);
            }
            catch (Exception ex)
            {
                if (ediabas.ErrorCodeLast == EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE)
                    System.Diagnostics.Debug.WriteLine("Job execution failed: " + EdiabasNet.GetExceptionText(ex));
                FlashLog.Job(Job, Arg, "EXCEPTION: " + ex.Message, null, null);
                return false;
            }
            string status = GetResult_String("JOB_STATUS", ediabas.ResultSets);
            LogJobTelegrams(Job, Arg, status, ediabas);
            return status == "OKAY";
        }

        private static bool ExecuteJob(EdiabasNet ediabas, string Job, byte[] Arg)
        {
            ediabas.ArgBinary = Arg;
            try
            {
                ediabas.ExecuteJob(Job);
            }
            catch (Exception ex)
            {
                if (ediabas.ErrorCodeLast == EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE)
                    System.Diagnostics.Debug.WriteLine("Job execution failed: " + EdiabasNet.GetExceptionText(ex));
                FlashLog.Job(Job, BinArg(Arg), "EXCEPTION: " + ex.Message, Arg, null);
                return false;
            }
            string status = GetResult_String("JOB_STATUS", ediabas.ResultSets);
            LogJobTelegrams(Job, BinArg(Arg), status, ediabas);
            return status == "OKAY";
        }

        /// <summary>Records a job and its wire telegrams to the flash log (no-op when not logging).</summary>
        private static void LogJobTelegrams(string job, string argSummary, string status, EdiabasNet ediabas)
        {
            if (!FlashLog.IsActive) return;
            byte[] tx = GetResult_ByteArray("_TEL_AUFTRAG", ediabas.ResultSets);
            byte[] rx = GetResult_ByteArray("_TEL_ANTWORT", ediabas.ResultSets);
            FlashLog.Job(job, argSummary, status, tx, rx);
        }

        /// <summary>Short summary of a binary argument (length + first bytes), for the log.</summary>
        private static string BinArg(byte[] arg)
        {
            if (arg == null || arg.Length == 0) return string.Empty;
            int n = Math.Min(arg.Length, 8);
            var sb = new System.Text.StringBuilder();
            sb.Append(arg.Length).Append("B:");
            for (int i = 0; i < n; i++) sb.Append(' ').Append(arg[i].ToString("X2"));
            if (arg.Length > n) sb.Append(" ...");
            return sb.ToString();
        }
    }
}
