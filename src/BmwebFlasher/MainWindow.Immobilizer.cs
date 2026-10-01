using System;
using System.Threading.Tasks;

using EdiabasLib;

namespace BmwebFlasher
{
    /// <summary>
    /// The immobilizer (EWS) state of the car, read once at identify, and the
    /// checks made against it when a file is loaded.
    ///
    /// The program and the tune each carry part of the immobilizer: two
    /// engine-enable bytes in the program, two error-class flags in the tune.
    /// They have to agree. An EWS-deleted program with the flags still on in
    /// the tune cranks but does not start (fault P1665). Reading the car's
    /// half at identify means a mismatch is raised when the file is loaded,
    /// rather than in the middle of a flash with the session already open.
    ///
    /// Only the MS45.1 on program 0044570LO02S is read; the offsets are not
    /// known for anything else, and the state is then simply unknown.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>True when the program on the car is EWS-deleted; null when not known.</summary>
        private bool? _carProgramEwsDeleted;

        /// <summary>True when the tune on the car has the immobilizer flags cleared; null when not known.</summary>
        private bool? _carTuneEwsDeleted;

        private void ForgetCarImmobilizer()
        {
            _carProgramEwsDeleted = null;
            _carTuneEwsDeleted = null;
        }

        /// <summary>
        /// Reads both halves from the car. Called from identify, on its
        /// session. Any failure leaves the state unknown; it never fails the
        /// identify.
        /// </summary>
        private void ReadCarImmobilizer(EdiabasNet ediabas)
        {
            ForgetCarImmobilizer();

            if (Global.HW_Ref != "0044570" ||
                !(Global.Prog_Ref?.Contains(EwsDelete.SupportedProgramVersion) ?? false))
                return;

            try
            {
                byte[] program = ReadCarBytes(ediabas,
                    (uint)EwsDelete.ProgramStateOffset, (uint)EwsDelete.ProgramMaskOffset);
                byte[] tune = ReadCarBytes(ediabas,
                    (uint)EwsDelete.CalFlag2Offset, (uint)EwsDelete.CalFlag3Offset);

                if (program != null)
                    _carProgramEwsDeleted =
                        EwsDelete.ProgramBytesAreDeleted(program[0], program[program.Length - 1]);

                if (tune != null)
                {
                    byte a = tune[0], b = tune[tune.Length - 1];
                    if (a == 0x00 && b == 0x00) _carTuneEwsDeleted = true;
                    else if (a == 0x10 && b == 0x10) _carTuneEwsDeleted = false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not read the immobilizer state: " + ex.Message);
                ForgetCarImmobilizer();
            }
            finally
            {
                UpdateProgressBar(0);   // ReadMemory drives the bar
            }
        }

        /// <summary>The bytes from start to end inclusive, or null if the read came back short.</summary>
        private byte[] ReadCarBytes(EdiabasNet ediabas, uint start, uint end, string segment = "ROMX")
        {
            int wanted = (int)(end - start + 1);
            byte[] data = ReadMemory(ediabas, start, end, segment, showProgress: false);

            // Outside BMW-FAST a memory read needs security access first, as
            // Read DME does. Only asked for if the plain read did not work.
            if ((data == null || data.Length != wanted) && Global.diagProtocol != "BMW-FAST" &&
                RequestSecurityAccess(ediabas))
                data = ReadMemory(ediabas, start, end, segment, showProgress: false);

            return data != null && data.Length == wanted ? data : null;
        }

        /// <summary>
        /// The line shown after identify. It leads with the state of the
        /// program, which is what decides whether the car needs the delete,
        /// and adds the tune only when the two disagree.
        /// </summary>
        private string CarImmobilizerSummary()
        {
            if (_carProgramEwsDeleted == true)
                return _carTuneEwsDeleted == false
                    ? "Identified, DME already EWS deleted, but its tune still has the immobilizer on"
                    : "Identified, DME already EWS deleted";

            if (_carProgramEwsDeleted == false)
                return _carTuneEwsDeleted == true
                    ? "Identified, DME not EWS deleted, but its tune has the immobilizer off"
                    : "Identified, DME not EWS deleted";

            return "Identified, EWS state not read";
        }

        /// <summary>
        /// Checks whatever was just loaded against the immobilizer state it
        /// will meet, and offers to put a mismatch right. Called after every
        /// load. A refused load is cleared again.
        /// </summary>
        private async Task GateLoadedFilesAsync()
        {
            bool full = FullBin_CheckBox.IsChecked == true;
            if (Global.openedFlash == null) return;

            if (!full)
            {
                // A tune on its own meets the program already on the car.
                if (Global.openedFlash.Length < MapSwitch.CalibrationLength) return;
                await GateTuneAgainstCarAsync();
                return;
            }

            if (Global.openedFlash.Length != MapSwitch.FullFlashLength) return;

            bool fileProgramDeleted = EwsDelete.ProgramBytesAreDeleted(
                Global.openedFlash[EwsDelete.ProgramStateOffset],
                Global.openedFlash[EwsDelete.ProgramMaskOffset]);

            if (ProgramHasNoTune)
            {
                // A .0PA brings no tune, so its program meets the tune on the car.
                if (!fileProgramDeleted && _carTuneEwsDeleted == true &&
                    !await ConfirmAsync(
                        "The tune on the car has the immobilizer switched off, and this program has it on.\n\n" +
                        "A .0PA has no tune in it, so on its own the tune on the car stays as it is, " +
                        "and the car may not start with this program.\n\n" +
                        "To keep the car EWS deleted, load the program's .0DA next with " +
                        "\"Load .0da (optional)\". The EWS delete can then be applied to both.\n\n" +
                        "Do you wish to load it anyway?",
                        "Immobilizer Mismatch"))
                {
                    ClearExchangeFile(clearFiles: true);
                }
                return;
            }

            // A full binary brings its own tune (and perhaps a map 2), which
            // only have to agree with the program in the same file. Wait for
            // the MPC: it is what says whether there is a map 2.
            if (Global.openedMPC == null) return;

            Global.openedFlash = await MatchImmobilizerAsync(
                Global.openedFlash, Global.openedMPC, fileProgramDeleted,
                checkMap1: true, "Immobilizer Mismatch");

            await GateProgramAgainstCarAsync(fileProgramDeleted);
        }

        /// <summary>
        /// A car running an EWS-deleted program was given the delete for a
        /// reason, so a full binary that is not deleted would put the
        /// immobilizer back. Offers to apply the delete at flash time.
        /// </summary>
        private async Task GateProgramAgainstCarAsync(bool fileProgramDeleted)
        {
            if (_carProgramEwsDeleted != true || fileProgramDeleted ||
                EwsDelete_CheckBox.IsChecked == true)
                return;

            string blocked = EwsDeleteBlockedReason();
            if (blocked != null)
            {
                await MessageAsync(
                    "The program on the car is EWS-deleted, but the program in this file is not.\n\n" +
                    "Flashing it puts the immobilizer back, and a car that needed the delete will " +
                    "crank but not start.\n\n" +
                    "The EWS delete cannot be applied to this file: " + blocked,
                    "Immobilizer Mismatch");
                return;
            }

            if (await ConfirmAsync(
                    "The program on the car is EWS-deleted, but the program in this file is not.\n\n" +
                    "Flashing it as it is puts the immobilizer back, and a car that needed the " +
                    "delete will crank but not start.\n\n" +
                    "Apply the EWS delete when flashing?",
                    "Immobilizer Mismatch"))
            {
                // Set directly: the question above stands in for the one the
                // checkbox asks when it is ticked by hand.
                EwsDelete_CheckBox.IsEnabled = true;
                EwsDelete_CheckBox.IsChecked = true;
                RefreshCustomOptionsSummary();
                SetStatus("EWS delete will be applied to the program before flashing.");
            }
            else
            {
                SetStatus("Loaded without EWS delete. The car may not start with this program.");
            }
        }

        private async Task GateTuneAgainstCarAsync()
        {
            byte[] tune = Global.openedFlash;

            if (_carProgramEwsDeleted == true && EwsDelete.CalibrationHasStockImmobilizer(tune, 0))
            {
                if (await ConfirmAsync(
                        "The program on the car is EWS-deleted, but this tune still has the immobilizer " +
                        "switched on.\n\n" +
                        "Flashed like this the car will crank but not start (EWS fault P1665).\n\n" +
                        "Clear the immobilizer flags in the tune to match the program?",
                        "Immobilizer Mismatch"))
                {
                    Global.openedFlash = EwsDelete.ApplyCalibrationDelete(tune, 0);
                    SetStatus("Matched tune to EWS-deleted program");
                }
                return;
            }

            if (_carProgramEwsDeleted == false && EwsDelete.CalibrationHasDeletedImmobilizer(tune, 0) &&
                !await ConfirmAsync(
                    "The program on the car has the immobilizer on, but this tune has it switched off.\n\n" +
                    "The two do not match, and the car may not start like this.\n\n" +
                    "Do you wish to load it anyway?",
                    "Immobilizer Mismatch"))
            {
                ClearExchangeFile(clearFiles: false);
                Global.openedFlash = null;
                FlashDME.IsEnabled = false;
                SetStatus("Tune not loaded");
            }
        }
    }
}
