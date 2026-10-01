using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EdiabasLib;

namespace BmwebFlasher
{
    /// <summary>
    /// Live Log: polls a few modules over the K-line as fast as they answer
    /// and writes every sample to a CSV, for chasing intermittent faults
    /// such as the DME losing ignition power. Three modules see the ignition
    /// switch from different places, which is what pins such a fault down:
    ///
    ///   DME      STATUS_MESSWERTE (rpm, battery voltage at the DME, temps,
    ///            pedal, air mass, speed) and STATUS_DIGITAL_1 (its own
    ///            terminal-15 input, "engine off").
    ///   GM5      STATUS_DIGITAL_GM3_KP (terminals R, 15, 50, 58 as the body
    ///            module sees them, straight off the ignition switch) and
    ///            STATUS_ANALOG_GM3 (its two terminal-30 supply voltages).
    ///   Cluster  STATUS_IO_LESEN (terminals 15, R, 30, 50 at the cluster).
    ///
    /// A module that stops answering is logged as such, with the time, and
    /// again when it comes back. If the DME alone drops off while the others
    /// still see terminal 15, its own supply or relay is the suspect; if all
    /// three lose terminal 15 together, the ignition switch is.
    /// </summary>
    public partial class MainWindow
    {
        private CancellationTokenSource _liveCancel;
        private string _livePath;

        public static string LiveLogDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bmweb-flasher", "livelogs");

        private const string Header =
            "time,module,ok,rpm,ub_v,kl15,klr,kl50,kl30,kl58,engine_off,tmot_c,tans_c,pedal_pct,maf_kgh,vspeed_kmh,u30l1_v,u30l2_v,note";

        /// <summary>One module's latest sample; blank fields are not reported by it.</summary>
        private class LiveSample
        {
            public string Module;
            public bool Ok;
            public string Rpm = "", Ub = "", Kl15 = "", KlR = "", Kl50 = "", Kl30 = "", Kl58 = "", EngineOff = "";
            public string Tmot = "", Tans = "", Pedal = "", Maf = "", Vspeed = "", U30L1 = "", U30L2 = "";
            public string Note = "";

            public string Csv(DateTime t) => string.Join(",", new[]
            {
                t.ToString("HH:mm:ss.fff"), Module, Ok ? "1" : "0", Rpm, Ub, Kl15, KlR, Kl50, Kl30, Kl58, EngineOff,
                Tmot, Tans, Pedal, Maf, Vspeed, U30L1, U30L2, Quote(Note)
            });

            public string Summary()
            {
                if (!Ok) return Module + ": NO RESPONSE";
                var parts = new List<string>();
                if (Rpm != "") parts.Add(Rpm + " rpm");
                if (Ub != "") parts.Add("UB " + Ub + " V");
                if (U30L1 != "") parts.Add("KL30 " + U30L1 + "/" + U30L2 + " V");
                if (Kl15 != "") parts.Add("KL15=" + Kl15);
                if (KlR != "") parts.Add("KLR=" + KlR);
                if (Kl50 != "") parts.Add("KL50=" + Kl50);
                if (Kl30 != "") parts.Add("KL30=" + Kl30);
                if (EngineOff != "") parts.Add("engine_off=" + EngineOff);
                if (Tmot != "") parts.Add("tmot " + Tmot);
                if (Pedal != "") parts.Add("pedal " + Pedal + "%");
                if (Vspeed != "") parts.Add(Vspeed + " km/h");
                return Module + ": " + string.Join("  ", parts);
            }

            private static string Quote(string s) => s.Contains(",") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        private async void LiveStart_Click(object sender, RoutedEventArgs e)
        {
            if (_liveCancel != null)
            {
                _liveCancel.Cancel();
                LiveStart.IsEnabled = false;      // until the loop reports it has stopped
                return;
            }

            string portProblem = CheckPort(Global.Port);
            if (portProblem != null)
            {
                SetStatus("Port unavailable");
                await MessageAsync(portProblem, "Live Log");
                return;
            }

            bool dme = LiveDme_CheckBox.IsChecked == true;
            bool gm5 = LiveGm5_CheckBox.IsChecked == true;
            bool kombi = LiveKombi_CheckBox.IsChecked == true;
            if (!dme && !gm5 && !kombi)
            {
                SetStatus("Pick at least one module to log");
                return;
            }

            Directory.CreateDirectory(LiveLogDir);
            _livePath = Path.Combine(LiveLogDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_livelog.csv");
            File.WriteAllText(_livePath, Header + Environment.NewLine);
            LiveEvents.Text = string.Empty;
            LiveSummary.Text = "starting...";
            SetStatus("Live log: " + Path.GetFileName(_livePath));

            _liveCancel = new CancellationTokenSource();
            CancellationToken token = _liveCancel.Token;
            LiveStart.Content = "Stop";
            LiveDme_CheckBox.IsEnabled = LiveGm5_CheckBox.IsEnabled = LiveKombi_CheckBox.IsEnabled = false;

            try
            {
                await Task.Run(() => LiveLoop(dme, gm5, kombi, token));
            }
            catch (Exception ex)
            {
                SetStatus("Live log stopped: " + ex.Message);
            }
            finally
            {
                _liveCancel.Dispose();
                _liveCancel = null;
                LiveStart.Content = "Start";
                LiveStart.IsEnabled = true;
                LiveDme_CheckBox.IsEnabled = LiveGm5_CheckBox.IsEnabled = LiveKombi_CheckBox.IsEnabled = true;
                SetStatus("Live log saved: " + Path.GetFileName(_livePath));
            }
        }

        private async void LiveShowFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(LiveLogDir);
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(LiveLogDir));
        }

        /// <summary>
        /// The polling loop. One EDIABAS instance; the SGBD is switched per
        /// module (EdiabasLib re-runs the module's init job on the next call
        /// after a switch, which costs a few hundred ms), so the DME's small
        /// terminal-15 telegram (STATUS_DIGITAL_1) is read every cycle and
        /// the slow things - the DME's full measurement block and the other
        /// modules - only every few cycles. A stall that the DME survives is
        /// only visible if its terminal-15 input is sampled fast; the first
        /// capture sampled it every 1.2 s and left a gap the fault fit into.
        /// Samples go to the CSV as they arrive; the summary and the event
        /// list are refreshed on the UI.
        /// </summary>
        private void LiveLoop(bool dme, bool gm5, bool kombi, CancellationToken token)
        {
            var latest = new Dictionary<string, LiveSample>();
            var wasOk = new Dictionary<string, bool>();
            var lastKl15 = new Dictionary<string, string>();
            string lastDmeRpm = null;
            long samples = 0, cycle = 0;
            bool gm5Analog = true;
            DateTime started = DateTime.Now;
            const int SlowEvery = 6;

            EdiabasNet ediabas = StartEdiabas();
            int consecutiveFailures = 0;
            const int ReconnectAfter = 5;

            using (SleepBlocker.Acquire())
            using (var csv = new StreamWriter(_livePath, true, new UTF8Encoding(false)))
            try
            {
                csv.AutoFlush = true;
                string current = Global.sgbd;

                // A K-line hiccup (an IFH error from the adapter) used to end
                // a capture: every later request failed the same way until
                // the log was stopped. After a few failures in a row the
                // port is closed and reopened, and the log carries on. A
                // module that is really gone keeps showing NOT RESPONDING,
                // now with a reconnect attempt every few seconds.
                void Reconnect()
                {
                    try { ediabas.Dispose(); } catch (Exception) { }
                    Thread.Sleep(500);
                    ediabas = StartEdiabas();
                    current = Global.sgbd;
                    Event("interface reopened after " + consecutiveFailures + " failed requests");
                    consecutiveFailures = 0;
                }

                bool Select(string sgbd)
                {
                    if (string.Equals(current, sgbd, StringComparison.OrdinalIgnoreCase)) return true;
                    try
                    {
                        ediabas.ResolveSgbdFile(sgbd);
                        current = sgbd;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Event("could not load " + sgbd + ": " + ex.Message);
                        return false;
                    }
                }

                string Raw() => BitConverter.ToString(GetResult_ByteArray("_TEL_ANTWORT", ediabas.ResultSets) ?? new byte[0]).Replace("-", "");

                void Record(LiveSample s)
                {
                    DateTime now = DateTime.Now;
                    csv.WriteLine(s.Csv(now));
                    samples++;
                    latest[s.Module] = s;
                    consecutiveFailures = s.Ok ? 0 : consecutiveFailures + 1;

                    // Transitions are what matter in a long log: a module going
                    // quiet or coming back, terminal 15 changing state, and the
                    // engine stopping while the DME still sees terminal 15.
                    if (wasOk.TryGetValue(s.Module, out bool ok) && ok != s.Ok)
                        Event(s.Module + (s.Ok ? " answering again" : " NOT RESPONDING") + (s.Note != "" ? " (" + s.Note + ")" : ""));
                    else if (!wasOk.ContainsKey(s.Module) && !s.Ok)
                        Event(s.Module + " NOT RESPONDING" + (s.Note != "" ? " (" + s.Note + ")" : ""));
                    wasOk[s.Module] = s.Ok;
                    if (s.Ok && s.Kl15 != "")
                    {
                        if (lastKl15.TryGetValue(s.Module, out string k) && k != s.Kl15)
                            Event(s.Module + " terminal 15 -> " + s.Kl15);
                        lastKl15[s.Module] = s.Kl15;
                    }
                    if (s.Ok && s.Module == "DME" && s.Rpm != "")
                    {
                        if (lastDmeRpm != null && lastDmeRpm != "0" && s.Rpm == "0")
                            Event("DME: engine stopped (was " + lastDmeRpm + " rpm), DME still answering, its terminal 15 = " +
                                  (s.Kl15 != "" ? s.Kl15 : "?"));
                        else if (lastDmeRpm == "0" && s.Rpm != "0")
                            Event("DME: engine running, " + s.Rpm + " rpm");
                        lastDmeRpm = s.Rpm;
                    }

                    var sb = new StringBuilder();
                    sb.Append((now - started).ToString(@"hh\:mm\:ss")).Append("  ").Append(samples).Append(" samples")
                      .Append(Environment.NewLine);
                    foreach (var kv in latest) sb.Append(kv.Value.Summary()).Append(Environment.NewLine);
                    string text = sb.ToString();
                    Dispatcher.UIThread.Post(() => LiveSummary.Text = text, DispatcherPriority.Background);
                }

                void Event(string what)
                {
                    string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what;
                    csv.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + ",event,,,,,,,,,,,,,,,,," + "\"" + what.Replace("\"", "\"\"") + "\"");
                    Dispatcher.UIThread.Post(() =>
                    {
                        LiveEvents.Text = (LiveEvents.Text.Length == 0 ? "" : LiveEvents.Text + Environment.NewLine) + line;
                    }, DispatcherPriority.Background);
                }

                Event("log started" + (dme ? " DME" : "") + (gm5 ? " GM5" : "") + (kombi ? " cluster" : ""));

                while (!token.IsCancellationRequested)
                {
                    bool slow = cycle % SlowEvery == 0;
                    bool others = cycle % SlowEvery == SlowEvery / 2;
                    cycle++;

                    if (dme && Select(Global.sgbd))
                    {
                        var s = new LiveSample { Module = "DME" };
                        // The last full measurement block carries over between
                        // its reads, so a row always has the slow values too.
                        if (latest.TryGetValue("DME", out LiveSample prev) && prev.Ok && !slow)
                        {
                            s.Rpm = prev.Rpm; s.Ub = prev.Ub; s.Tmot = prev.Tmot; s.Tans = prev.Tans;
                            s.Pedal = prev.Pedal; s.Maf = prev.Maf; s.Vspeed = prev.Vspeed;
                        }
                        s.Ok = ExecuteJob(ediabas, "STATUS_DIGITAL_1", string.Empty);
                        if (s.Ok)
                        {
                            var r = ediabas.ResultSets;
                            s.Kl15 = ResultText("STAT_KL15_EIN", r);
                            s.EngineOff = ResultText("STAT_MOTOR_AUS", r);
                            if (slow || lastDmeRpm == null)
                            {
                                if (ExecuteJob(ediabas, "STATUS_MESSWERTE", string.Empty))
                                {
                                    r = ediabas.ResultSets;
                                    s.Rpm = ResultText("STAT_NMOT_WERT", r);
                                    s.Ub = ResultText("STAT_UB_WERT", r);
                                    s.Tmot = ResultText("STAT_TMOT_WERT", r);
                                    s.Tans = ResultText("STAT_TANS_WERT", r);
                                    s.Pedal = ResultText("STAT_UPWG_WERT", r);
                                    s.Maf = ResultText("STAT_MSHFM_WERT", r);
                                    s.Vspeed = ResultText("STAT_VFZG_WERT", r);
                                }
                                else s.Note = "STATUS_MESSWERTE failed";
                            }
                        }
                        else s.Note = ErrorNote(ediabas);
                        Record(s);
                    }
                    if (token.IsCancellationRequested) break;

                    if (others && gm5 && Select("ZKE3_GM5.prg"))
                    {
                        var s = new LiveSample { Module = "GM5" };
                        s.Ok = ExecuteJob(ediabas, "STATUS_DIGITAL_GM3_KP", string.Empty);
                        if (s.Ok)
                        {
                            var r = ediabas.ResultSets;
                            s.KlR = ResultText("STAT_K_KLR_AKTIV", r);
                            s.Kl15 = ResultText("STAT_K_KL15_AKTIV", r);
                            s.Kl50 = ResultText("STAT_K_KL50_AKTIV", r);
                            s.Kl58 = ResultText("STAT_K_KL58_AKTIV", r);
                            s.Note = "raw " + Raw();
                            if (gm5Analog)
                            {
                                if (ExecuteJob(ediabas, "STATUS_ANALOG_GM3", string.Empty))
                                {
                                    r = ediabas.ResultSets;
                                    s.U30L1 = ResultText("STAT_U30L1_WERT", r);
                                    s.U30L2 = ResultText("STAT_U30L2_WERT", r);
                                }
                                else
                                {
                                    gm5Analog = false;     // this module does not have it; do not keep asking
                                    Event("GM5: STATUS_ANALOG_GM3 not supported, skipping it");
                                }
                            }
                        }
                        else s.Note = ErrorNote(ediabas);
                        Record(s);
                    }
                    if (token.IsCancellationRequested) break;

                    if (others && kombi && Select("kombi46.prg"))
                    {
                        var s = new LiveSample { Module = "cluster" };
                        s.Ok = ExecuteJob(ediabas, "STATUS_IO_LESEN", string.Empty);
                        if (s.Ok)
                        {
                            var r = ediabas.ResultSets;
                            s.Kl15 = ResultText("STAT_KL15_EIN", r);
                            s.KlR = ResultText("STAT_KLR_EIN", r);
                            s.Kl30 = ResultText("STAT_KL30_EIN", r);
                            s.Kl50 = ResultText("STAT_KL50_EIN", r);
                            s.Note = "raw " + BitConverter.ToString(GetResult_ByteArray("ANTWORT", ediabas.ResultSets) ?? new byte[0]).Replace("-", "");
                        }
                        else s.Note = ErrorNote(ediabas);
                        Record(s);
                    }

                    if (consecutiveFailures >= ReconnectAfter)
                        Reconnect();

                    Thread.Sleep(10);
                }

                Event("log stopped, " + samples + " samples");
            }
            finally
            {
                try { ediabas.Dispose(); } catch (Exception) { }
            }
        }

        private static string ErrorNote(EdiabasNet ediabas)
        {
            EdiabasNet.ErrorCodes code = ediabas.ErrorCodeLast;
            return code == EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE ? "job not OKAY" : code.ToString();
        }

        /// <summary>
        /// A result as text whatever its type: EDIABAS returns measured
        /// values as doubles or integers and states as integers or strings.
        /// </summary>
        private static string ResultText(string name, List<Dictionary<string, EdiabasNet.ResultData>> sets)
        {
            if (sets == null) return "";
            foreach (var set in sets)
            {
                if (!set.TryGetValue(name, out EdiabasNet.ResultData data) || data.OpData == null) continue;
                switch (data.OpData)
                {
                    case string str: return str.Trim();
                    case double d: return d.ToString("0.##", CultureInfo.InvariantCulture);
                    case float f: return f.ToString("0.##", CultureInfo.InvariantCulture);
                    case long l: return l.ToString(CultureInfo.InvariantCulture);
                    case int i: return i.ToString(CultureInfo.InvariantCulture);
                    case byte[] b: return BitConverter.ToString(b).Replace("-", "");
                    default: return data.OpData.ToString();
                }
            }
            return "";
        }
    }
}
