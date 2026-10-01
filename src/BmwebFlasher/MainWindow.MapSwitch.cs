using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using EdiabasLib;

namespace BmwebFlasher
{
    /// <summary>
    /// The Custom Options view: EWS delete and map switch. It shares the
    /// Flashing tab with the main flashing controls instead of having a tab of
    /// its own, so Flashing stays the selected tab while it is open.
    ///
    /// Map switch prepares files and never talks to the car: the built pair is
    /// handed to the flashing controls, which do the writing with their usual
    /// checks and warnings.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Set by a successful DME identify, cleared when the module changes.</summary>
        private bool _dmeIdentified;

        /// <summary>
        /// Whether the Custom Options button is offered. A development build
        /// (see <see cref="AppEnvironment"/>) always shows it. Otherwise it
        /// appears only once the DME has been identified as an MS45.1 running
        /// the program the options were built for, which is the same program
        /// EWS delete is tied to.
        /// </summary>
        private bool CustomOptionsAllowed()
        {
            if (AppEnvironment.IsDevelopment)
                return true;

            // The transmission's options only apply to a GS20, which identify
            // establishes.
            if (_flashTcu)
                return string.Equals(_tcuSgbd, "gs20.prg", StringComparison.OrdinalIgnoreCase);

            return _dmeIdentified &&
                   FlashModuleSelect.SelectedIndex == 0 &&
                   Global.HW_Ref == "0044570" &&
                   !string.IsNullOrEmpty(Global.Prog_Ref) &&
                   Global.Prog_Ref.Contains(EwsDelete.SupportedProgramVersion);
        }

        private void RefreshCustomOptionsGate()
        {
            if (CustomOptions == null) return; // during init

            // A development build can tick Full Binary and load files without
            // a car; identify enables them otherwise.
            if (AppEnvironment.IsDevelopment)
            {
                FullBin_CheckBox.IsEnabled = true;

                // Likewise the load buttons, unless a .0PA / .0DA is standing
                // in for them.
                if (_exchangeFileKind == null)
                {
                    LoadFile.IsEnabled = true;
                    LoadFile2.IsEnabled = FullBin_CheckBox.IsChecked == true;
                }
            }

            bool allowed = CustomOptionsAllowed();
            CustomOptions.IsVisible = allowed;
            CustomOptionsSummary_Box.IsVisible = allowed;
            Avalonia.Controls.ToolTip.SetTip(CustomOptions, _flashTcu ? "Remove auto upshift." : "EWS delete and map switch.");
            if (!allowed)
                ShowCustomOptions(false);
            RefreshCustomOptionsSummary();
        }

        private void ShowCustomOptions(bool show)
        {
            if (CustomOptionsView == null || FlashingMainView == null) return; // during init
            CustomOptionsView.IsVisible = show;
            FlashingMainView.IsVisible = !show;
            RefreshRail();
        }

        private void CustomOptions_Click(object sender, RoutedEventArgs e)
        {
            if (!CustomOptionsAllowed()) return;
            // One view serves both modules; only the selected module's
            // sections are shown.
            EwsSection.IsVisible = !_flashTcu;
            MapSwitchSection.IsVisible = !_flashTcu;
            TcuUpshiftSection.IsVisible = _flashTcu;
            if (_flashTcu)
                RefreshNoUpshiftGate();
            else
                RefreshEwsDeleteGate();
            ShowCustomOptions(true);
        }

        private void CustomOptionsBack_Click(object sender, RoutedEventArgs e) => ShowCustomOptions(false);

        /// <summary>
        /// File 2 and Flash Program only apply to a full binary, so they are
        /// shown only while Full Binary is ticked; Flash Tune only while it
        /// is not.
        /// </summary>
        private void RefreshFullBinVisibility()
        {
            bool full = FullBin_CheckBox.IsChecked == true;
            LoadFile2.IsVisible = full;
            FlashProgram.IsVisible = full;
            // A full binary is written with Flash Program, which writes the
            // tune as well, so Flash Tune is only offered for a tune file.
            FlashDME.IsVisible = !full;

            RefreshLoadButtonLabels();
        }

        /// <summary>
        /// One line under the Custom Options button saying what is switched
        /// on, since the options themselves are out of sight on the main view.
        /// </summary>
        private void RefreshCustomOptionsSummary()
        {
            if (CustomOptionsSummary_Box == null) return;

            var active = new System.Collections.Generic.List<string>();
            if (_flashTcu)
            {
                CustomOptionsSummary_Box.Text = NoUpshift_CheckBox.IsChecked == true
                    ? "On: no auto upshift" : string.Empty;
                return;
            }
            if (EwsDelete_CheckBox.IsChecked == true)
                active.Add("EWS delete");
            if (Global.openedMPC != null && MapSwitch.IsAlreadyPatched(Global.openedMPC))
                active.Add("map switch");

            CustomOptionsSummary_Box.Text = active.Count == 0
                ? string.Empty
                : "On: " + string.Join(", ", active);
        }

        // ------------------------------------------------------------------
        // The map switch on the car, read once at identify
        // ------------------------------------------------------------------

        /// <summary>What the car's MPC carries; null when not read.</summary>
        private MapSwitch.CarState? _carMapSwitch;

        /// <summary>The trigger and scope of the map switch on the car; null when there is none or it was not read.</summary>
        private MapSwitch.Trigger? _carTrigger;
        private MapSwitch.Scope? _carScope;
        private int? _carDscPresses;

        /// <summary>The data version of the map 2 stored on the car; null when there is none or it was not read.</summary>
        private string _carMap2Version;

        private void ForgetCarMapSwitch()
        {
            _carMapSwitch = null;
            _carTrigger = null;
            _carScope = null;
            _carDscPresses = null;
            _carMap2Version = null;
            RefreshReadInstalledMaps();
        }

        private bool CarHasMapSwitch =>
            _carMapSwitch == MapSwitch.CarState.Current || _carMapSwitch == MapSwitch.CarState.Earlier;

        /// <summary>The button is only there for a car that identified as carrying the map switch.</summary>
        private void RefreshReadInstalledMaps()
        {
            bool show = CarHasMapSwitch;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReadInstalledMaps != null)   // during init
                    ReadInstalledMaps.IsVisible = show;
            });
        }

        private async void ReadInstalledMaps_Click(object sender, RoutedEventArgs e)
        {
            ReadInstalledMaps.IsEnabled = false;
            try { await ReadInstalledMapsAsync(); }
            catch (Exception ex)
            {
                SetStatus("Read failed: " + ex.Message);
                await MessageAsync(Describe(ex), "Read Installed Maps");
            }
            finally
            {
                ReadInstalledMaps.IsEnabled = true;
                UpdateProgressBar(0);
            }
        }

        /// <summary>
        /// Reads both maps from the car and saves each as a tune file that
        /// can be loaded again, here or as a tune to flash.
        /// </summary>
        private async Task ReadInstalledMapsAsync()
        {
            if (!CarHasMapSwitch) return;

            byte[] map1 = null, map2Area = null;

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

                SetStatus("Reading map 1");
                await Task.Run(() => map1 = ReadMemory(ediabas,
                    (uint)MapSwitch.CalibrationStart,
                    (uint)(MapSwitch.CalibrationStart + MapSwitch.CalibrationLength - 1), "ROMX"));

                SetStatus("Reading map 2");
                await Task.Run(() => map2Area = ReadMemory(ediabas,
                    (uint)MapSwitch.Map2Start,
                    (uint)(MapSwitch.Map2Start + MapSwitch.Map2Length - 1), "ROMX"));

                if (Global.diagProtocol != "BMW-FAST")
                {
                    ExecuteJob(ediabas, "diagnose_mode", "DEFAULT;PC9600");
                    ExecuteJob(ediabas, "SET_PARAMETER", ";9600");
                }
            }

            if (map1 == null || map1.Length != MapSwitch.CalibrationLength ||
                map2Area == null || map2Area.Length != MapSwitch.Map2Length)
            {
                SetStatus("Read failed. The DME did not return both maps.");
                return;
            }

            byte[] map2 = MapSwitch.Map2AsCalibration(map2Area);

            string name = Global.VIN + "_" + Global.HW_Ref;
            await SaveDumpAsync(map1, name + "_map1");
            if (map2 != null)
                await SaveDumpAsync(map2, name + "_map2");

            string v1 = MapSwitch.ReadDataVersion(map1) ?? "unknown data version";
            SetStatus(map2 != null
                ? "Read map 1 (" + v1 + ") and map 2 (" + MapSwitch.ReadDataVersion(map2) + ")"
                : "Read map 1 (" + v1 + "). No map 2 is stored on the car.");
        }

        /// <summary>
        /// Reads the MPC's free area, and map 2's data version when the map
        /// switch is there. Called from identify, on its session, for the
        /// same DME the immobilizer is read from. Any failure leaves the
        /// state unknown; it never fails the identify.
        /// </summary>
        private void ReadCarMapSwitch(EdiabasNet ediabas)
        {
            ForgetCarMapSwitch();

            if (Global.HW_Ref != "0044570" ||
                !(Global.Prog_Ref?.Contains(MapSwitch.SupportedProgramVersion) ?? false))
                return;

            try
            {
                byte[] freeArea = ReadCarBytes(ediabas,
                    (uint)MapSwitch.CarCheckOffset,
                    (uint)(MapSwitch.CarCheckOffset + MapSwitch.CarCheckLength - 1), "LAR");
                if (freeArea == null)
                    return;

                _carMapSwitch = MapSwitch.StateOnCar(freeArea);
                _carTrigger = MapSwitch.TriggerOnCar(freeArea);
                _carScope = MapSwitch.ScopeOnCar(freeArea);
                _carDscPresses = MapSwitch.DscPressesOnCar(freeArea);

                if (_carMapSwitch == MapSwitch.CarState.Current ||
                    _carMapSwitch == MapSwitch.CarState.Earlier)
                {
                    _carMap2Version = MapSwitch.DataVersionFrom(ReadCarBytes(ediabas,
                        (uint)MapSwitch.Map2DataVersionOffset,
                        (uint)(MapSwitch.Map2DataVersionOffset + MapSwitch.DataVersionFieldLength - 1)));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not read the map switch state: " + ex.Message);
                ForgetCarMapSwitch();
            }
            finally
            {
                UpdateProgressBar(0);   // ReadMemory drives the bar
                RefreshReadInstalledMaps();
            }
        }

        /// <summary>
        /// What is added to the line shown after identify; empty when the
        /// state was not read.
        /// </summary>
        private string CarMapSwitchSummary()
        {
            switch (_carMapSwitch)
            {
                case MapSwitch.CarState.NotInstalled:
                    return ", no map switch";
                case MapSwitch.CarState.Current:
                case MapSwitch.CarState.Earlier:
                    // Only the trigger and whether it is current: the scope
                    // is always the full tune now, and the map 2 version is
                    // the program's data version, which says nothing useful.
                    return ", map switch installed (" +
                           (_carTrigger != null
                               ? MapSwitch.Describe(_carTrigger.Value, _carDscPresses ?? MapSwitch.DefaultDscPresses)
                               : "unknown trigger") +
                           (_carMapSwitch == MapSwitch.CarState.Earlier ? ", earlier version)" : ")") +
                           (_carMap2Version == null ? ", no map 2 stored" : string.Empty);
                case MapSwitch.CarState.Unrecognised:
                    return ", MPC carries an unrecognised modification";
                default:
                    return string.Empty;
            }
        }

        private byte[] _mapSwitchFlash;
        private byte[] _mapSwitchMpc;
        private byte[] _mapSwitchMap1;
        private byte[] _mapSwitchMap2;
        private MapSwitch.Result _mapSwitchBuilt;

        private async Task<string> PickPathAsync(string title, FilePickerFileType[] filters)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = filters
            });

            string path = files?.FirstOrDefault()?.TryGetLocalPath();
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// Anything that changes an input throws the last build away, so a
        /// stale result can never be saved or sent on.
        /// </summary>
        private void MapSwitchInputsChanged()
        {
            _mapSwitchBuilt = null;
            MapSwitchSave.IsEnabled = false;
            MapSwitchSend.IsEnabled = false;

            if (_mapSwitchFlash == null || _mapSwitchMpc == null)
            {
                MapSwitchBuild.IsEnabled = false;
                MapSwitchReport_Box.Text = "Choose an external flash and its MPC flash.";
                return;
            }

            string blocked = MapSwitch.BlockedReason(_mapSwitchFlash, _mapSwitchMpc);
            if (blocked == null && !VerifyFlashMPCMatch(_mapSwitchFlash, _mapSwitchMpc))
                blocked = "The external flash and the MPC flash are not a matching pair.";

            MapSwitchBuild.IsEnabled = blocked == null;
            MapSwitchReport_Box.Text = blocked ?? DescribeMapSwitchInputs();
        }

        /// <summary>What Build will do to the pair, given the chosen trigger.</summary>
        private string DescribeMapSwitchInputs()
        {
            MapSwitch.Trigger? trigger = MapSwitch.InstalledTrigger(_mapSwitchMpc);
            MapSwitch.Scope? scope = MapSwitch.InstalledScope(_mapSwitchMpc);
            int? presses = MapSwitch.InstalledDscPresses(_mapSwitchMpc);
            if (trigger == null || scope == null || presses == null)
                return "Ready to build.";
            string installed = MapSwitch.Describe(trigger.Value, presses.Value) + ", " + MapSwitch.Describe(scope.Value);
            if (!MapSwitch.IsCurrentVersion(_mapSwitchMpc))
                return "This pair carries an earlier version of the map switch (" + installed + "). Build updates it.";
            if (trigger == SelectedTrigger && (trigger == MapSwitch.Trigger.Pedals || presses == SelectedDscPresses))
                return "This pair already carries the map switch with this trigger. Build replaces its maps.";
            return "This pair carries the map switch with another trigger (" + installed + "). Build changes it.";
        }

        private MapSwitch.Trigger SelectedTrigger =>
            MapSwitchTriggerPedals?.IsChecked == true ? MapSwitch.Trigger.Pedals : MapSwitch.Trigger.DscButton;

        private int SelectedDscPresses => MapSwitchPresses2?.IsChecked == true ? 2 : 4;

        private void MapSwitchTrigger_Click(object sender, RoutedEventArgs e)
        {
            // The press count only means something for the DSC button.
            bool dsc = SelectedTrigger == MapSwitch.Trigger.DscButton;
            MapSwitchPressesLabel.IsVisible = dsc;
            MapSwitchPressesRow.IsVisible = dsc;
            MapSwitchInputsChanged();
        }

        private async void MapSwitchLoadFlash_Click(object sender, RoutedEventArgs e)
        {
            string path = await PickPathAsync("External Flash", LoadFilters(fullBinary: true));
            if (path == null) return;
            string name = Path.GetFileName(path);

            try
            {
                // A .0PA carries the MPC as well, so it fills both rows.
                if (Ms45ExchangeFile.IsProgramFile(path))
                {
                    Ms45ExchangeFile.Program program = Ms45ExchangeFile.DecodeProgram(path);
                    _mapSwitchFlash = program.Flash;
                    _mapSwitchMpc = program.Mpc;
                    MapSwitchFlash_Box.Text = name + "  (" + (program.Reference ?? "unknown program") + ")";
                    MapSwitchMpc_Box.Text = name;
                    MapSwitchInputsChanged();
                    return;
                }

                byte[] data = File.ReadAllBytes(path);
                if (data.Length != MapSwitch.FullFlashLength)
                {
                    await MessageAsync("An external flash image is 1 MB (0x100000 bytes). This file is 0x" +
                                       data.Length.ToString("X") + " bytes.", "Map Switch");
                    return;
                }

                _mapSwitchFlash = data;
                MapSwitchFlash_Box.Text = name + "  (" + (MapSwitch.ReadProgramVersion(data) ?? "unknown program") + ")";
                MapSwitchInputsChanged();
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException ||
                                       ex is UnauthorizedAccessException)
            {
                await MessageAsync("Could not load " + name + ".\n\n" + ex.Message, "Map Switch");
            }
        }

        private async void MapSwitchLoadMpc_Click(object sender, RoutedEventArgs e)
        {
            string path = await PickPathAsync("MPC Flash", LoadFilters(fullBinary: true));
            if (path == null) return;
            string name = Path.GetFileName(path);

            try
            {
                // From a .0PA only the MPC half is taken here; the External
                // Flash button is the one that takes both.
                byte[] data = Ms45ExchangeFile.IsProgramFile(path)
                    ? Ms45ExchangeFile.DecodeProgram(path).Mpc
                    : File.ReadAllBytes(path);

                if (data.Length != MapSwitch.MpcLength)
                {
                    await MessageAsync("An MPC flash image is 448 KB (0x70000 bytes). This file is 0x" +
                                       data.Length.ToString("X") + " bytes.", "Map Switch");
                    return;
                }

                _mapSwitchMpc = data;
                MapSwitchMpc_Box.Text = name;
                MapSwitchInputsChanged();
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException ||
                                       ex is UnauthorizedAccessException)
            {
                await MessageAsync("Could not load " + name + ".\n\n" + ex.Message, "Map Switch");
            }
        }

        private void MapSwitchUseLoaded_Click(object sender, RoutedEventArgs e)
        {
            if (Global.openedFlash == null || Global.openedMPC == null ||
                Global.openedFlash.Length != MapSwitch.FullFlashLength ||
                Global.openedMPC.Length != MapSwitch.MpcLength)
            {
                MapSwitchReport_Box.Text =
                    "No external flash and MPC flash are loaded for flashing. Go back, tick Full Binary and load both, " +
                    "or choose the files here.";
                return;
            }

            _mapSwitchFlash = (byte[])Global.openedFlash.Clone();
            _mapSwitchMpc = (byte[])Global.openedMPC.Clone();
            MapSwitchFlash_Box.Text = "Loaded for flashing  (" +
                (MapSwitch.ReadProgramVersion(_mapSwitchFlash) ?? "unknown program") + ")";
            MapSwitchMpc_Box.Text = "Loaded for flashing";
            MapSwitchInputsChanged();
        }

        private async Task<(byte[] Cal, string Label)> PickTuneAsync(string title)
        {
            string path = await PickPathAsync(title, LoadFilters(fullBinary: false));
            if (path == null) return (null, null);
            string name = Path.GetFileName(path);

            try
            {
                byte[] cal = Ms45ExchangeFile.IsDataFile(path)
                    ? Ms45ExchangeFile.DecodeCalibration(path).Data
                    : MapSwitch.ExtractCalibration(File.ReadAllBytes(path));
                return (cal, name + "  (" + (MapSwitch.ReadDataVersion(cal) ?? "unknown data version") + ")");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is InvalidDataException ||
                                       ex is IOException || ex is UnauthorizedAccessException)
            {
                await MessageAsync("Could not load " + name + ".\n\n" + ex.Message, "Map Switch");
                return (null, null);
            }
        }

        private async void MapSwitchLoadMap1_Click(object sender, RoutedEventArgs e)
        {
            var (cal, label) = await PickTuneAsync("Map 1 Tune");
            if (cal == null) return;
            _mapSwitchMap1 = cal;
            MapSwitchMap1_Box.Text = label;
            MapSwitchInputsChanged();
        }

        private async void MapSwitchLoadMap2_Click(object sender, RoutedEventArgs e)
        {
            var (cal, label) = await PickTuneAsync("Map 2 Tune");
            if (cal == null) return;
            _mapSwitchMap2 = cal;
            MapSwitchMap2_Box.Text = label;
            MapSwitchInputsChanged();
        }

        private void MapSwitchClearMaps_Click(object sender, RoutedEventArgs e)
        {
            _mapSwitchMap1 = null;
            _mapSwitchMap2 = null;
            MapSwitchMap1_Box.Text = "(none)";
            MapSwitchMap2_Box.Text = "(none)";
            MapSwitchInputsChanged();
        }

        /// <summary>
        /// Offers to switch the immobilizer off in the tunes of an image whose
        /// program is (or is about to be) EWS-deleted. Map 1 is asked about;
        /// map 2, when the image has one, is matched without asking.
        ///
        /// An EWS-deleted program with the flags still on in the selected map
        /// cranks but does not start (fault P1665). Map 1 is asked about
        /// because a user may have loaded it deliberately; map 2 is only ever
        /// a copy of a tune, so it is matched without a prompt. Under the
        /// full-tune scope the flags are read from whichever map is selected,
        /// so both maps must agree with the program.
        ///
        /// Returns the image to use, which is a copy when anything changed.
        /// </summary>
        private async Task<byte[]> MatchImmobilizerAsync(
            byte[] flash, byte[] mpc, bool programDeleted, bool checkMap1, string title,
            System.Collections.Generic.List<string> log = null)
        {
            if (!programDeleted || flash == null || flash.Length != MapSwitch.FullFlashLength)
                return flash;

            void Note(string line)
            {
                log?.Add(line);
                FlashLog.Note(line);
            }

            if (checkMap1 && EwsDelete.CalibrationHasStockImmobilizer(flash, MapSwitch.CalibrationStart))
            {
                if (await ConfirmAsync(
                        "The program is EWS-deleted, but the tune (map 1) still has the immobilizer switched on.\n\n" +
                        "Flashed like this the car will crank but not start (EWS fault P1665).\n\n" +
                        "Clear the immobilizer flags in the tune to match the program?",
                        title))
                {
                    flash = EwsDelete.ApplyCalibrationDelete(flash, MapSwitch.CalibrationStart);
                    Note("Map 1: immobilizer flags cleared to match the EWS-deleted program");
                }
                else
                {
                    Note("WARNING: EWS-deleted program with the immobilizer on in map 1. " +
                         "The car will not start like this.");
                }
            }

            // Map 2's flags are never read, so there is nothing to ask:
            // they are simply kept in step with the program.
            if (MapSwitch.HasMap2(flash, mpc) &&
                EwsDelete.CalibrationHasStockImmobilizer(flash, MapSwitch.Map2Start))
            {
                flash = EwsDelete.ApplyCalibrationDelete(flash, MapSwitch.Map2Start);
                Note("Map 2: immobilizer flags cleared to match the EWS-deleted program");
            }

            return flash;
        }

        private async void MapSwitchBuild_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MapSwitch.Result built = MapSwitch.Build(
                    _mapSwitchFlash, _mapSwitchMpc, _mapSwitchMap1, _mapSwitchMap2, SelectedTrigger, SelectedDscPresses);

                // An EWS-deleted program needs the immobilizer off in the
                // tunes too; see MatchImmobilizerAsync.
                built.Flash = await MatchImmobilizerAsync(
                    built.Flash, built.Mpc, programDeleted: EwsDelete.ProgramBytesAreDeleted(
                        built.Flash[EwsDelete.ProgramStateOffset], built.Flash[EwsDelete.ProgramMaskOffset]),
                    checkMap1: true, "Map Switch", built.Log);

                // Checksum and sign here as well, so the saved files are
                // complete on their own. The flash path repeats both.
                var cs = new Checksums_Signatures();
                byte[] cal = built.Flash.Skip(MapSwitch.CalibrationStart).Take(MapSwitch.CalibrationLength).ToArray();
                cal = cs.SignMS45Parameters(cs.CorrectParameterChecksums(cal));
                Buffer.BlockCopy(cal, 0, built.Flash, MapSwitch.CalibrationStart, cal.Length);
                built.Flash = cs.CorrectProgramChecksums(built.Flash, built.Mpc);
                built.Flash = cs.SignMS45Program(built.Flash, built.Mpc);

                _mapSwitchBuilt = built;
                MapSwitchSave.IsEnabled = true;
                MapSwitchSend.IsEnabled = true;

                var report = new StringBuilder();
                foreach (string line in built.Log)
                    report.AppendLine(line);
                if (built.MapsIdentical)
                    report.AppendLine("Map 1 and map 2 are identical, so switching will change nothing yet.");
                report.Append("Checksums corrected and both partitions signed.");
                MapSwitchReport_Box.Text = report.ToString();
                SetStatus("Map switch built");
            }
            catch (Exception ex)
            {
                _mapSwitchBuilt = null;
                MapSwitchSave.IsEnabled = false;
                MapSwitchSend.IsEnabled = false;
                MapSwitchReport_Box.Text = "Build failed: " + ex.Message;
            }
        }

        private async void MapSwitchSave_Click(object sender, RoutedEventArgs e)
        {
            if (_mapSwitchBuilt == null) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            await SaveDumpAsync(_mapSwitchBuilt.Flash, "MS45.1_mapswitch_Flash_" + stamp);
            await SaveDumpAsync(_mapSwitchBuilt.Mpc, "MS45.1_mapswitch_MPC_" + stamp);
        }

        private async void MapSwitchSend_Click(object sender, RoutedEventArgs e)
        {
            if (_mapSwitchBuilt == null) return;

            // The identified state belongs to whichever module is selected, so
            // it only counts when that module is the DME.
            if (FlashModuleSelect.SelectedIndex != 0 || string.IsNullOrEmpty(Global.HW_Ref))
            {
                await MessageAsync(
                    "Go back and identify the DME first, then load the build again.\n\n" +
                    "A loaded program is checked against the DME that was identified.",
                    "Map Switch");
                return;
            }

            if (!VerifyProgramMatch(_mapSwitchBuilt.Flash, Global.HW_Ref))
            {
                await MessageAsync(
                    "The built program is for hardware 0044570, but the identified DME reports " +
                    Global.HW_Ref + ".", "Map Switch");
                return;
            }

            if (!await ConfirmAsync(
                    "This loads the map switch build as the full binary to flash.\n\n" +
                    "Flashing it rewrites the DME's internal (MPC) flash, and a program that does " +
                    "not boot cannot be recovered over the diagnostic port. Keep a full read of the " +
                    "car as it is now, and have a way to write the MPC directly before you flash.\n\n" +
                    "Load it?",
                    "Map Switch"))
                return;

            // Setting IsChecked does not raise Click, so the Full Binary handler
            // (which clears the loaded files) does not run here.
            // The build replaces whatever was loaded, including a .0PA.
            ClearExchangeFile(clearFiles: false);
            FullBin_CheckBox.IsChecked = true;
            LoadFile2.IsEnabled = true;
            RefreshFullBinVisibility();

            Global.openedFlash = (byte[])_mapSwitchBuilt.Flash.Clone();
            Global.openedMPC = (byte[])_mapSwitchBuilt.Mpc.Clone();
            _loadedFlashName = "Map switch build (external)";
            _loadedMpcName = "Map switch build (MPC)";
            FlashProgram.IsEnabled = true;
            FlashDME.IsEnabled = true;
            RefreshEwsDeleteGate();
            ShowCustomOptions(false);

            SetStatus("Loaded the map switch build. Use Flash Program to write it.");

            // The build is checked against the car like any other loaded
            // file: a car that is EWS-deleted needs the program in it deleted.
            await GateLoadedFilesAsync();
            RefreshEwsDeleteGate();
        }
    }
}
