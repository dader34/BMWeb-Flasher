using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace BmwebFlasher
{
    /// <summary>
    /// Loading BMW's own .0PA (program) and .0DA (data) files in place of raw
    /// binaries. A loaded exchange file stands in for the load buttons: they
    /// are greyed out, the file is named underneath, and the X beside it
    /// removes it again.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Names of the raw binaries last loaded, shown under the load buttons.</summary>
        private string _loadedFlashName;
        private string _loadedMpcName;

        /// <summary>
        /// Shows each loaded file's name under its button, and nothing once
        /// the file is gone. A .0PA / .0DA has its own row, so the names stay
        /// hidden while one is loaded.
        /// </summary>
        private void RefreshLoadedNames()
        {
            if (LoadFileName_Box == null || LoadFile2Name_Box == null) return; // during init

            bool raw = _exchangeFileKind == null;
            bool full = FullBin_CheckBox.IsChecked == true;

            bool showFlash = raw && Global.openedFlash != null && !string.IsNullOrEmpty(_loadedFlashName);
            LoadFileName_Box.IsVisible = showFlash;
            LoadFileName_Box.Text = showFlash ? _loadedFlashName : string.Empty;
            ToolTip.SetTip(LoadFileName_Box, showFlash ? _loadedFlashName : null);

            bool showMpc = raw && full && Global.openedMPC != null && !string.IsNullOrEmpty(_loadedMpcName);
            LoadFile2Name_Box.IsVisible = showMpc;
            LoadFile2Name_Box.Text = showMpc ? _loadedMpcName : string.Empty;
            ToolTip.SetTip(LoadFile2Name_Box, showMpc ? _loadedMpcName : null);
        }

        /// <summary>"0PA", "0DA", or null when the loaded files are raw binaries.</summary>
        private string _exchangeFileKind;

        /// <summary>True when the loaded program came from a .0PA.</summary>
        private bool ProgramFromExchangeFile => _exchangeFileKind == "0PA";

        /// <summary>True once a matching .0DA has been paired with the loaded .0PA.</summary>
        private bool _exchangeTuneLoaded;

        /// <summary>
        /// True when the loaded program came from a .0PA and no .0DA was
        /// paired with it. Such an image has no calibration in it, so nothing
        /// may write its (blank) tune area.
        /// </summary>
        private bool ProgramHasNoTune => ProgramFromExchangeFile && !_exchangeTuneLoaded;

        /// <summary>
        /// The labels on the two load buttons. With a .0PA loaded the second
        /// button stops being the MPC (the .0PA brought that) and offers the
        /// tune instead.
        /// </summary>
        private void RefreshLoadButtonLabels()
        {
            bool full = FullBin_CheckBox.IsChecked == true;
            LoadFile.Content = full ? "Load External / .0pa" : "Load File / .0da";
            LoadFile2.Content = ProgramFromExchangeFile
                ? "Load .0da (optional)"
                : full ? "Load MPC" : "Load File 2 (MPC Flash)";
        }

        /// <summary>
        /// Pairs a .0DA with the loaded .0PA. Only a data file built for that
        /// program is accepted.
        /// </summary>
        private async Task LoadPairedDataFileAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load .0da",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("BMW data file") { Patterns = new[] { "*.0DA", "*.0da" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                }
            });

            string path = files?.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            string name = Path.GetFileName(path);

            if (!Ms45ExchangeFile.IsDataFile(path))
            {
                await MessageAsync(name + " is not a .0DA data file.", "Load .0da");
                return;
            }

            try
            {
                Ms45ExchangeFile.Calibration cal = Ms45ExchangeFile.DecodeCalibration(path);

                if (!Ms45ExchangeFile.IsMatchingPair(Global.openedFlash, cal.Data))
                {
                    await MessageAsync(
                        name + " does not belong to the loaded program.\n\n" +
                        "The program is " + (MapSwitch.ReadProgramVersion(Global.openedFlash) ?? "unknown") +
                        " (project " + (Ms45ExchangeFile.ProgramProjectToken(Global.openedFlash) ?? "?") +
                        "), and this data file is " + (cal.Reference ?? "unknown") +
                        " (project " + (Ms45ExchangeFile.CalibrationProjectToken(cal.Data) ?? "?") + ").\n\n" +
                        "It has not been loaded.",
                        "Load .0da");
                    return;
                }

                // Into a copy, so a reference held elsewhere never sees a
                // half-assembled image.
                byte[] image = (byte[])Global.openedFlash.Clone();
                Buffer.BlockCopy(cal.Data, 0, image, MapSwitch.CalibrationStart, cal.Data.Length);
                Global.openedFlash = image;

                _exchangeTuneLoaded = true;
                ExchangeTune_Box.Text = "0DA: " + name;
                ToolTip.SetTip(ExchangeTune_Box, ExchangeTune_Box.Text +
                    (string.IsNullOrEmpty(cal.Reference) ? string.Empty : "  (" + cal.Reference + ")"));
                ExchangeTuneRow.IsVisible = true;
                LoadFile2.IsEnabled = false;
                SetStatus("Loaded " + name + " as the tune for the program");
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException ||
                                       ex is UnauthorizedAccessException)
            {
                await MessageAsync("Could not load " + name + ".\n\n" + ex.Message, "Load .0da");
            }
        }

        /// <summary>Takes the paired tune away again, leaving the .0PA loaded.</summary>
        private void RemovePairedTune()
        {
            if (!_exchangeTuneLoaded) return;

            if (Global.openedFlash != null && Global.openedFlash.Length == MapSwitch.FullFlashLength)
            {
                byte[] image = (byte[])Global.openedFlash.Clone();
                for (int i = 0; i < MapSwitch.CalibrationLength; i++)
                    image[MapSwitch.CalibrationStart + i] = 0xFF;
                Global.openedFlash = image;
            }

            _exchangeTuneLoaded = false;
            ExchangeTuneRow.IsVisible = false;
            ExchangeTune_Box.Text = string.Empty;
            LoadFile2.IsEnabled = ProgramFromExchangeFile;
        }

        private void ExchangeTuneRemove_Click(object sender, RoutedEventArgs e)
        {
            RemovePairedTune();
            // A ticked EWS delete needed that tune to patch.
            RefreshEwsDeleteGate();
            SetStatus("Removed the tune. The tune on the car will be left as it is.");
        }

        /// <summary>
        /// The question asked when a loaded tune cannot be matched to the DME.
        /// This is asked on load, so it asks about loading; and with no DME
        /// identified there is nothing to mismatch, which it says instead.
        /// </summary>
        private static string TuneMismatchPrompt() => string.IsNullOrEmpty(Global.SW_Ref)
            ? "No DME has been identified, so this tune cannot be checked against one.\n\n" +
              "Do you wish to load it anyway?"
            : "Loaded tune does not match DME's program.\n\nDo you wish to load it anyway?";

        private static string ProgramMismatchPrompt() => string.IsNullOrEmpty(Global.HW_Ref)
            ? "No DME has been identified, so this program cannot be checked against one.\n\n" +
              "Do you wish to load it anyway?"
            : "Loaded program does not match DME hardware.\n\nDo you wish to load it anyway?";

        private static FilePickerFileType[] LoadFilters(bool fullBinary) => fullBinary
            ? new[]
            {
                new FilePickerFileType("Binary or BMW program file")
                    { Patterns = new[] { "*.bin", "*.ori", "*.0PA", "*.0pa" } },
                new FilePickerFileType("Binary") { Patterns = new[] { "*.bin" } },
                new FilePickerFileType("BMW program file") { Patterns = new[] { "*.0PA", "*.0pa" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
            : new[]
            {
                new FilePickerFileType("Binary or BMW data file")
                    { Patterns = new[] { "*.bin", "*.ori", "*.0DA", "*.0da" } },
                new FilePickerFileType("Binary") { Patterns = new[] { "*.bin" } },
                new FilePickerFileType("BMW data file") { Patterns = new[] { "*.0DA", "*.0da" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            };

        /// <summary>
        /// Loads a .0PA (Full Binary ticked) or a .0DA (unticked) as the file
        /// to flash. Leaves whatever was loaded before in place if the file is
        /// refused.
        /// </summary>
        private async Task LoadExchangeFile(string path)
        {
            bool full = FullBin_CheckBox.IsChecked == true;
            bool isProgram = Ms45ExchangeFile.IsProgramFile(path);
            string name = Path.GetFileName(path);

            if (isProgram && !full)
            {
                await MessageAsync(
                    name + " is a program file. Tick Full Binary to load it.", "Load File");
                return;
            }
            if (!isProgram && full)
            {
                await MessageAsync(
                    name + " is a data (tune) file. Untick Full Binary to load it.", "Load External");
                return;
            }

            try
            {
                if (isProgram)
                {
                    Ms45ExchangeFile.Program program = Ms45ExchangeFile.DecodeProgram(path);

                    if (!VerifyFlashMPCMatch(program.Flash, program.Mpc))
                    {
                        await MessageAsync(
                            "The external program and the MPC inside " + name + " do not belong together, " +
                            "so the file is not being loaded.", "Load External");
                        return;
                    }

                    if (!VerifyProgramMatch(program.Flash, Global.HW_Ref) &&
                        !await ConfirmAsync(
                            ProgramMismatchPrompt(),
                            "Warning"))
                        return;

                    Global.openedFlash = program.Flash;
                    Global.openedMPC = program.Mpc;
                    FlashProgram.IsEnabled = true;
                    // A .0PA carries no tune, so there is nothing for Flash Tune to write.
                    FlashDME.IsEnabled = false;
                    ShowExchangeFile("0PA", name, program.Reference);
                    SetStatus("Loaded " + name + " (external program and MPC)");
                }
                else
                {
                    Ms45ExchangeFile.Calibration cal = Ms45ExchangeFile.DecodeCalibration(path);

                    if (!VerifyParameterMatch(cal.Data, Global.SW_Ref) &&
                        !await ConfirmAsync(
                            TuneMismatchPrompt(),
                            "Warning"))
                        return;

                    Global.openedFlash = cal.Data;
                    Global.openedMPC = null;
                    FlashDME.IsEnabled = true;
                    FlashProgram.IsEnabled = false;
                    ShowExchangeFile("0DA", name, cal.Reference);
                    SetStatus("Loaded " + name);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException ||
                                       ex is UnauthorizedAccessException)
            {
                await MessageAsync("Could not load " + name + ".\n\n" + ex.Message,
                                   isProgram ? "Load External" : "Load File");
            }
        }

        private void ShowExchangeFile(string kind, string name, string reference)
        {
            _exchangeFileKind = kind;
            // The row is narrow, so it shows the name only; the version is in
            // the tooltip.
            ExchangeFile_Box.Text = kind + ": " + name;
            ToolTip.SetTip(ExchangeFile_Box, ExchangeFile_Box.Text +
                (string.IsNullOrEmpty(reference) ? string.Empty : "  (" + reference + ")"));
            ExchangeFileRow.IsVisible = true;

            // The exchange file stands in for both load buttons. A .0PA
            // then offers the second one for its tune.
            LoadFile.IsEnabled = false;
            LoadFile2.IsEnabled = kind == "0PA";
            RefreshLoadButtonLabels();
        }

        /// <summary>
        /// Takes the exchange file away and hands the load buttons back.
        /// <paramref name="clearFiles"/> false is for callers that have
        /// already replaced or cleared the loaded files themselves.
        /// </summary>
        private void ClearExchangeFile(bool clearFiles)
        {
            if (_exchangeFileKind == null) return;

            _exchangeFileKind = null;
            ExchangeFileRow.IsVisible = false;
            ExchangeFile_Box.Text = string.Empty;

            _exchangeTuneLoaded = false;
            ExchangeTuneRow.IsVisible = false;
            ExchangeTune_Box.Text = string.Empty;

            LoadFile.IsEnabled = true;
            LoadFile2.IsEnabled = FullBin_CheckBox.IsChecked == true;
            RefreshLoadButtonLabels();

            if (clearFiles)
            {
                Global.openedFlash = null;
                Global.openedMPC = null;
                FlashDME.IsEnabled = false;
                FlashProgram.IsEnabled = false;
                UpdateProgressBar(0);
                SetStatus("Removed the loaded file");
            }
        }

        private void ExchangeFileRemove_Click(object sender, RoutedEventArgs e)
        {
            ClearExchangeFile(clearFiles: true);
            RefreshEwsDeleteGate();
        }
    }
}
