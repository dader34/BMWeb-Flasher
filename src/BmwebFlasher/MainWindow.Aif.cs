using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using EdiabasLib;

namespace BmwebFlasher
{
    /// <summary>
    /// The Anwender-Info-Feld: the programming log BMW's tools append to after
    /// every flash (who programmed the module, when, with what). WinKFP and
    /// ISTA read it as the module's history and, before a flash, say how many
    /// of its 14 entries are left. Both modules get the same treatment here,
    /// switched by the Programming Record setting.
    ///
    /// The DME's SGBD has an AIF_SCHREIBEN job (KWP $3D into the area the DME
    /// names in its $1A $80 identification), so that write is a job call. The
    /// transmission's job lives only in BMW's programming SGBD, so its record
    /// is packed here (<see cref="Gs20Aif"/>) and written over the raw DS2
    /// link inside the unlocked write session.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>The newest AIF entry of each module as read at identify, by result name.</summary>
        private Dictionary<string, string> _dmeAif = new Dictionary<string, string>();
        private Dictionary<string, string> _tcuAif = new Dictionary<string, string>();

        /// <summary>
        /// Set by a program write that goes straight on to the calibration: one
        /// programming operation, so the calibration write neither asks about
        /// the counter again nor adds a second entry.
        /// </summary>
        private bool _tcuCalFollowsProgram;

        private const string TesterSerial = "BMWEB";

        private static readonly string[] AifFields =
        {
            "AIF_FG_NR", "AIF_FG_NR_LANG", "AIF_DATUM", "AIF_AENDERUNGS_INDEX", "AIF_ZB_NR", "AIF_SW_NR",
            "AIF_BEHOERDEN_NR", "AIF_HAENDLER_NR", "AIF_SERIEN_NR", "AIF_KM",
            "AIF_PROG_NR", "AIF_ANZ_FREI", "AIF_ANZAHL_PROG", "AIF_ADRESSE_HIGH", "AIF_ADRESSE_LOW",
            // The transmission's SGBD spells some of them its own way.
            "AIF_PROGG_NR", "AIF_WERKSCODE", "AIF_KM_STAND", "AIF_ADRESSE",
        };

        /// <summary>
        /// Every AIF result as text. Numbers come back as longs, not strings,
        /// so the plain string getter would drop them.
        /// </summary>
        private static Dictionary<string, string> CaptureAif(EdiabasNet ediabas)
        {
            var aif = new Dictionary<string, string>();
            foreach (var set in ediabas.ResultSets)
            {
                foreach (string f in AifFields)
                {
                    if (!set.TryGetValue(f, out EdiabasNet.ResultData rd) || rd.OpData == null) continue;
                    string v = rd.OpData is byte[] b
                        ? BitConverter.ToString(b).Replace("-", " ")
                        : Convert.ToString(rd.OpData, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(v)) aif[f] = v.Trim();
                }
            }
            return aif;
        }

        /// <summary>Keeps the fields of the aif_lesen that identify just ran.</summary>
        private void CaptureDmeAif(EdiabasNet ediabas)
        {
            _dmeAif = CaptureAif(ediabas);
            LogAif(_dmeAif);
        }

        /// <summary>
        /// The entry's fields in the log under the screen, the way INPA's
        /// coding-data page lists them, so an identify shows whose module
        /// this is and how many programming entries it has left.
        /// </summary>
        private void LogAif(Dictionary<string, string> aif)
        {
            var lines = new List<string>();
            if (aif.Count == 0)
            {
                lines.Add("Coding data: the programming record (AIF) was not answered");
            }
            else
            {
                string vin = Field(aif, "AIF_FG_NR_LANG");
                if (vin.Length == 0) vin = Field(aif, "AIF_FG_NR");
                string index = Field(aif, "AIF_AENDERUNGS_INDEX");
                string prog = Or(Field(aif, "AIF_PROG_NR"), Field(aif, "AIF_PROGG_NR"));
                string km = Or(Field(aif, "AIF_KM"), Field(aif, "AIF_KM_STAND"));
                string dealer = Or(Field(aif, "AIF_HAENDLER_NR"), Field(aif, "AIF_WERKSCODE"));

                lines.Add("Coding data: VIN " + Or(vin, "-") + " · date " + Or(Field(aif, "AIF_DATUM"), "-") +
                          (index.Length > 0 ? " · index " + index : string.Empty));
                lines.Add("    software " + Or(Field(aif, "AIF_SW_NR"), "-") +
                          " · assembly " + Or(Field(aif, "AIF_ZB_NR"), "-") +
                          " · official " + Or(Field(aif, "AIF_BEHOERDEN_NR"), "-"));

                var extra = new List<string>();
                if (prog.Length > 0) extra.Add("program " + prog);
                if (dealer.Length > 0 && dealer != "0") extra.Add("dealer " + dealer);
                if (km.Length > 0) extra.Add(km + " km");
                if (long.TryParse(Field(aif, "AIF_ADRESSE"), out long addr))
                    extra.Add("base 0x" + addr.ToString("X6"));
                else if (int.TryParse(Field(aif, "AIF_ADRESSE_HIGH"), out int hi) && int.TryParse(Field(aif, "AIF_ADRESSE_LOW"), out int lo))
                    extra.Add("base 0x" + ((hi << 16) | lo).ToString("X6"));
                if (int.TryParse(Field(aif, "AIF_ANZ_FREI"), out int free))
                    extra.Add(free + " of " + Gs20Aif.Slots + " entries free");
                if (extra.Count > 0) lines.Add("    " + string.Join(" · ", extra));
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { foreach (string l in lines) AppendLog(l); });
        }

        private static string Or(string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;

        /// <summary>
        /// Reads the transmission's newest entry and its free count with the
        /// diagnostic SGBD's AIF_LESEN (entry 0 = the current one). A variant
        /// without the job simply leaves the counter unknown.
        /// </summary>
        private void CaptureTcuAif(EdiabasNet ediabas)
        {
            _tcuAif = ExecuteJob(ediabas, "AIF_LESEN", "0") ? CaptureAif(ediabas) : new Dictionary<string, string>();
            LogAif(_tcuAif);
        }

        private static string Field(Dictionary<string, string> aif, string field) =>
            aif != null && aif.TryGetValue(field, out string v) ? v : string.Empty;

        private string Aif(string field) => Field(_dmeAif, field);

        /// <summary>The VIN typed for this flash's entry, empty when none was.</summary>
        private string _aifVin = string.Empty;

        /// <summary>
        /// The question before a flash: how many programming entries the
        /// module has left, whether to go on, and which VIN to put in the
        /// entry. The VIN box starts with the best one known (the DME's when
        /// it was identified this session, else the module's own last entry,
        /// which on a donor names its old car) and may be edited or cleared.
        /// Nothing to ask when the record is switched off.
        /// </summary>
        private async Task<bool> ConfirmProgrammingCounterAsync(bool tcu)
        {
            if (!Global.WriteAif) return true;

            string module = tcu ? "transmission" : "DME";
            var aif = tcu ? _tcuAif : _dmeAif;
            string freeText = Field(aif, "AIF_ANZ_FREI");
            bool known = int.TryParse(freeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int free);

            string counter = !known
                ? "The " + module + "'s programming counter could not be read; an entry is written if it has room."
                : free <= 0
                    ? "The " + module + "'s programming log is full: 0 of " + Gs20Aif.Slots + " entries left. " +
                      "The flash can still go ahead, but no entry can be written for it."
                    : "Remaining programming operations for this " + module + ": " + free + " of " + Gs20Aif.Slots +
                      ". This flash uses one; the entries cannot be erased.";

            string suggested = Aif("AIF_FG_NR_LANG");
            if (!IsAlnum(suggested, 17)) suggested = Field(aif, "AIF_FG_NR_LANG");
            if (!IsAlnum(suggested, 17)) suggested = Field(aif, "AIF_FG_NR");
            if (!IsAlnum(suggested, 17) && !IsAlnum(suggested, 7)) suggested = Global.VIN ?? string.Empty;

            var dialog = new Window
            {
                Title = "Programming counter",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            bool result = false;
            var vinBox = new TextBox { Text = suggested, Watermark = "17 characters, or leave empty", MaxLength = 17 };
            var hint = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 };
            var go = new Button { Content = "Continue", MinWidth = 90 };
            var cancel = new Button { Content = "Cancel", MinWidth = 90, IsDefault = true };

            void Validate()
            {
                string v = (vinBox.Text ?? string.Empty).Trim().ToUpperInvariant();
                bool ok = v.Length == 0 || IsAlnum(v, 17) || (!tcu && IsAlnum(v, 7));
                go.IsEnabled = ok;
                hint.Text = v.Length == 0
                    ? (tcu ? "Empty: the entry is written with the VIN already known, if there is one." : "Empty: the last entry's VIN is kept.")
                    : ok ? string.Empty : (tcu ? "A transmission entry needs all 17 characters." : "7 or 17 letters and digits.");
            }
            vinBox.TextChanged += (_, _) => Validate();
            Validate();

            go.Click += (_, _) => { result = true; dialog.Close(); };
            cancel.Click += (_, _) => { result = false; dialog.Close(); };

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = counter, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "VIN for the entry (optional)" },
                    vinBox,
                    hint,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, go }
                    }
                }
            };

            await ShowModalAsync(dialog);
            _aifVin = result ? (vinBox.Text ?? string.Empty).Trim().ToUpperInvariant() : string.Empty;
            return result;
        }

        /// <summary>
        /// Appends a programming entry to the DME after a flash. The job checks
        /// every argument's shape (lengths from the SGBD: VIN 7 or 17, ZB /
        /// software / approval 7 or 9, dealer 6, tester serial 5, program
        /// reference 12, date TT.MM.JJJJ), so the previous entry's values are
        /// carried forward and only what this flash changes is filled in.
        /// A refusal is reported, never fatal: the flash itself is done.
        /// </summary>
        private async Task WriteDmeAifAsync()
        {
            string vin = _aifVin;
            if (!IsAlnum(vin, 17) && !IsAlnum(vin, 7)) vin = Aif("AIF_FG_NR_LANG");
            if (!IsAlnum(vin, 17)) vin = Global.VIN ?? string.Empty;
            if (!IsAlnum(vin, 17) && !IsAlnum(vin, 7))
            {
                FlashLog.Note("AIF not written: no usable VIN (" + vin + ")");
                return;
            }

            string zb = SevenOrNine(Aif("AIF_ZB_NR"), "0000000");
            string sw = SevenOrNine(Aif("AIF_SW_NR"), SevenOrNine(Global.SW_Ref, "0000000"));
            string approval = SevenOrNine(Aif("AIF_BEHOERDEN_NR"), "0000000");
            string dealer = Digits(Aif("AIF_HAENDLER_NR"), 6) ? Aif("AIF_HAENDLER_NR") : "000000";
            string progRef = IsAlnum(Global.Prog_Ref, 12) ? Global.Prog_Ref
                           : IsAlnum(Aif("AIF_PROG_NR"), 12) ? Aif("AIF_PROG_NR") : "000000000000";

            // The entry stores the odometer in 600 km steps of one byte, so the
            // job refuses anything past 153 000 km; a higher reading is pinned.
            long km = 0;
            long.TryParse(Aif("AIF_KM"), NumberStyles.Integer, CultureInfo.InvariantCulture, out km);
            if (km < 0) km = 0;
            if (km > 152999) km = 152999;

            string args = string.Join(";", new[]
            {
                vin,
                DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                zb, sw, approval, dealer, TesterSerial,
                km.ToString(CultureInfo.InvariantCulture),
                progRef,
            });

            SetStatus("Writing the programming record (AIF)");
            string outcome = null;
            await Task.Run(() =>
            {
                using (SleepBlocker.Acquire())
                using (EdiabasNet ediabas = StartEdiabas())
                {
                    if (ediabas == null) { outcome = "AIF not written: EDIABAS did not start"; return; }
                    bool ok = ExecuteJob(ediabas, "AIF_SCHREIBEN", args);
                    string status = GetResult_String("JOB_STATUS", ediabas.ResultSets);
                    string number = GetResult_String("AIF_NUMMER", ediabas.ResultSets);
                    outcome = ok
                        ? "AIF written" + (string.IsNullOrEmpty(number) ? string.Empty : " (entry " + number + ")")
                        : "AIF not written: " + (string.IsNullOrEmpty(status) ? "job failed" : status);
                }
            });
            FlashLog.Note(outcome + " [" + args + "]");
            SetStatus(outcome);
        }

        /// <summary>
        /// Appends the transmission's entry right after a write, while the
        /// session is still unlocked. Carries the previous entry's numbers
        /// forward; the part numbers come from the files written when their
        /// names carry them, the program reference from the image tail, the
        /// VIN from the DME when it was identified this session (a donor
        /// transmission's own log names its old car). Runs on the write
        /// thread; a failure is logged and shown, never thrown.
        /// </summary>
        private void TryWriteTcuAif(Gs20CalWriter session, byte[] program, byte[] calibration)
        {
            if (!Global.WriteAif) return;
            try
            {
                string vin = _aifVin;
                if (!IsAlnum(vin, 17)) vin = Aif("AIF_FG_NR_LANG");
                if (!IsAlnum(vin, 17)) vin = Field(_tcuAif, "AIF_FG_NR");
                if (!IsAlnum(vin, 17))
                {
                    FlashLog.Note("AIF not written: no 17-character VIN known (type one at the counter question or identify the DME first)");
                    SetStatus("Written; no programming record (no 17-character VIN known)");
                    return;
                }

                // "G2210_0090C0" in a program's or calibration's tail: release
                // 90, suffix C0, which the factory entry stores as 00 90 C0.
                string ident = IdentTail(program) ?? IdentTail(calibration);
                byte[] progRef = ident != null
                    ? new[] { (byte)0, Convert.ToByte(ident.Substring(0, 2), 16), Convert.ToByte(ident.Substring(2, 2), 16) }
                    : new byte[3];

                // The numbers BMW's tools record are the data number and the
                // assembly it ships under, both known from the calibration's
                // own reference; a file named after its .0DA gives the data
                // number too. Otherwise the previous entry's numbers stand.
                var known = Gs20Aif.PartNumbers(calibration != null ? Gs20Checksum.ReadVersion(calibration) : null);
                int zb = known?.AssemblyNr ?? Gs20Aif.Number(Field(_tcuAif, "AIF_ZB_NR"));
                int sw = known?.DataNr ?? PartNumber(_tcuCalName) ?? Gs20Aif.Number(Field(_tcuAif, "AIF_SW_NR"));
                string index = Field(_tcuAif, "AIF_AENDERUNGS_INDEX");
                if (index.Length != 2) index = "00";

                byte[] record = Gs20Aif.Build(
                    vin, DateTime.Now, sw, index,
                    Gs20Aif.Number(Field(_tcuAif, "AIF_BEHOERDEN_NR")), zb, TesterSerial,
                    Gs20Aif.Number(Field(_tcuAif, "AIF_HAENDLER_NR")),
                    Gs20Aif.Number(Field(_tcuAif, "AIF_KM")), progRef);

                SetStatus("Writing the programming record (AIF)");
                int address = session.WriteAifRecord(record, out int slot, out int left);
                FlashLog.Note("AIF entry " + (slot + 1) + " written at 0x" + address.ToString("X6") +
                              " (" + left + " left): " + Ds2Telegram.ToHex(record, 46));
                _tcuAif["AIF_ANZ_FREI"] = left.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                FlashLog.Note("AIF not written: " + ex.Message);
                SetStatus("Written; the programming record was not: " + ex.Message);
            }
        }

        /// <summary>"90C0" from a G2210_0090C0 ident in an image's last 256 bytes.</summary>
        private static string IdentTail(byte[] image)
        {
            if (image == null || image.Length < 0x100) return null;
            string tail = System.Text.Encoding.ASCII.GetString(image, image.Length - 0x100, 0x100);
            var m = Regex.Match(tail, @"G2210_00(\d\d[A-Z0-9]\d)");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>The seven-digit BMW number in a file name such as 7552700A.0PA or A7558009.0DA.</summary>
        private static int? PartNumber(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var m = Regex.Match(fileName, @"(?<!\d)(\d{7})(?!\d)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        }

        private static bool IsAlnum(string s, int length) =>
            !string.IsNullOrEmpty(s) && s.Length == length && s.All(char.IsLetterOrDigit);

        private static bool Digits(string s, int length) =>
            !string.IsNullOrEmpty(s) && s.Length == length && s.All(char.IsDigit);

        private static string SevenOrNine(string s, string fallback) =>
            IsAlnum(s, 7) || IsAlnum(s, 9) ? s : fallback;
    }
}
