using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BmwebFlasher
{
    /// <summary>The control units there is an emulator for.</summary>
    public enum EmulatedUnit
    {
        /// <summary>The MS45.1 engine control unit (ms45-emu).</summary>
        Dme,
        /// <summary>The GS20 transmission control unit (gs20-emu).</summary>
        Tcu,
    }

    /// <summary>What an emulator is booted with.</summary>
    public sealed class EmulatorOptions
    {
        /// <summary>The emulator's checkout: the folder holding tools/kline.py.</summary>
        public string Folder { get; set; }
        /// <summary>DME: the 1 MB external flash. TCU: the 512 KB image.</summary>
        public string Image { get; set; }
        /// <summary>DME only: the 448 KB internal (MPC) flash that goes with the external one.</summary>
        public string Mpc { get; set; }
        /// <summary>
        /// One of BMW's program files (.0PA) to boot instead of the image's
        /// program, or null. It has no boot loader, so it is written over
        /// the image's: the DME then needs only its external flash, and the
        /// transmission can do without an image where gs20-emu has a stock
        /// one of its own.
        /// </summary>
        public string Program { get; set; }
        /// <summary>
        /// One of BMW's calibration files (.0DA) in place of the image's, or
        /// null. A program without one keeps the image's calibration when the
        /// two go together; otherwise the emulator takes the first .0DA in
        /// the program's folder that does.
        /// </summary>
        public string Calibration { get; set; }
        /// <summary>Boot on the flash the last session left instead of the files.</summary>
        public bool Resume { get; set; }
        /// <summary>DME only: answer faster than a real K line would.</summary>
        public bool Turbo { get; set; }
        /// <summary>Where a session's EEPROM and flash are kept between runs.</summary>
        public string StateDir { get; set; }
        /// <summary>
        /// The WebSocket the browser's BMWeb reaches the module on: null for
        /// the unit's usual port (8767 DME, 8770 TCU), 0 for none.
        /// </summary>
        public int? WsPort { get; set; }
    }

    /// <summary>The command line that boots an emulator, worked out without starting anything.</summary>
    public sealed class EmulatorCommand
    {
        public string FileName { get; set; }
        public List<string> Arguments { get; } = new List<string>();
        public string WorkingDirectory { get; set; }
        public Dictionary<string, string> Environment { get; } = new Dictionary<string, string>();
        /// <summary>Something the user should know about this start, or null.</summary>
        public string Note { get; set; }
        /// <summary>The WebSocket port the browser's BMWeb gets, or 0 for none.</summary>
        public int WsPort { get; set; }
    }

    /// <summary>
    /// One emulated control unit, run as a child process.
    ///
    /// The emulators are the Python projects ms45-emu and gs20-emu: each runs
    /// the module's own firmware and puts its K line on a pseudo-terminal,
    /// which this app then opens like a cable. The process is driven through
    /// its standard input (the ignition or power switch, quit) and watched
    /// through its output, which is where the pseudo-terminal's name and
    /// every change of state are announced.
    ///
    /// Nothing here touches the UI; events arrive on background threads.
    /// </summary>
    public sealed class EmulatorProcess : IDisposable
    {
        public const int DmeFlashSize = 0x100000, DmeMpcSize = 0x70000, TcuImageSize = 0x80000;

        private readonly object _gate = new object();
        private Process _process;

        public EmulatorProcess(EmulatedUnit unit) { Unit = unit; }

        public EmulatedUnit Unit { get; }

        /// <summary>"DME" or "TCU".</summary>
        public string Name => Unit == EmulatedUnit.Dme ? "DME" : "TCU";

        /// <summary>The project that emulates it.</summary>
        public string Project => Unit == EmulatedUnit.Dme ? "ms45-emu" : "gs20-emu";

        /// <summary>What its switch is: the DME has an ignition input, the TCU is simply powered.</summary>
        public string SwitchName => Unit == EmulatedUnit.Dme ? "ignition" : "power";

        /// <summary>The process is alive.</summary>
        public bool Running { get; private set; }

        /// <summary>The firmware has booted and the K line is there.</summary>
        public bool Ready { get; private set; }

        /// <summary>The ignition (DME) or the power (TCU) is on.</summary>
        public bool SwitchOn { get; private set; }

        /// <summary>The pseudo-terminal carrying the K line, once the emulator has named it.</summary>
        public string Port { get; private set; }

        /// <summary>Where the browser's BMWeb usually reaches the emulated module: a WebSocket on localhost.</summary>
        public static int DefaultWsPort(EmulatedUnit unit) => unit == EmulatedUnit.Dme ? 8767 : 8770;

        /// <summary>The WebSocket port this run was booted with, or 0 for none.</summary>
        public int WsPort { get; private set; }

        /// <summary>The gateway the browser's BMWeb takes as its cable, while the emulator runs with one; null otherwise.</summary>
        public string Gateway => Running && WsPort > 0 ? "ws://localhost:" + WsPort : null;

        /// <summary>BMWeb with this emulator as its cable, while it runs; null otherwise.</summary>
        public string BmwebUrl => Gateway != null ? BmwebSite + "?gateway=" + Gateway : null;

        /// <summary>The BMWeb that opens on the emulator.</summary>
        public const string BmwebSite = "https://bmweb.danner.ink/";

        /// <summary>One line on what the emulated module is doing.</summary>
        public string Status { get; private set; } = "not running";

        /// <summary>The files it was booted with, for the label.</summary>
        public string ImageName { get; private set; }

        /// <summary>Every line the emulator prints.</summary>
        public event Action<string> Line;

        /// <summary>Running, Ready, SwitchOn, Port or Status changed.</summary>
        public event Action Changed;

        // ---- working out how to start it ------------------------------------------

        /// <summary>
        /// The command that boots this unit with these options. Throws
        /// InvalidOperationException with a message for the user when something
        /// it needs is missing or the wrong size.
        /// </summary>
        public static EmulatorCommand BuildCommand(EmulatedUnit unit, EmulatorOptions o)
        {
            if (OperatingSystem.IsWindows())
                throw new InvalidOperationException("The emulators put their K line on a pseudo-terminal, which needs macOS or Linux.");
            if (o == null || string.IsNullOrWhiteSpace(o.Folder) || !File.Exists(Path.Combine(o.Folder, "tools", "kline.py")))
                throw new InvalidOperationException(
                    "The " + (unit == EmulatedUnit.Dme ? "ms45-emu" : "gs20-emu") + " folder is not set, or has no tools/kline.py in it.");
            string state = string.IsNullOrWhiteSpace(o.StateDir) ? Path.Combine(o.Folder, "images") : o.StateDir;

            var c = new EmulatorCommand { WorkingDirectory = o.Folder };
            c.Environment["PYTHONUNBUFFERED"] = "1";
            c.Arguments.Add("-u");
            c.Arguments.Add(Path.Combine("tools", "kline.py"));
            c.Arguments.Add("--attached");
            // The browser's BMWeb takes the emulator as its cable through this
            // WebSocket (?gateway=ws://localhost:PORT); both emulators speak
            // BMWeb's gateway protocol on it.
            int ws = o.WsPort ?? DefaultWsPort(unit);
            if (ws > 0) c.Arguments.AddRange(new[] { "--ws", ws.ToString() });
            c.WsPort = ws;

            if (unit == EmulatedUnit.Dme)
            {
                string eeprom = Path.Combine(state, "dme-eeprom.bin");
                bool resume = o.Resume && File.Exists(eeprom + ".flash.bin") && File.Exists(eeprom + ".mpc.bin");
                bool program = !string.IsNullOrWhiteSpace(o.Program);
                // A program file has no boot loader. It goes on the chosen read's, or, with
                // none chosen, on the one ms45-emu keeps for this.
                bool ownBoot = program && string.IsNullOrWhiteSpace(o.Image);
                if (ownBoot && !File.Exists(Path.Combine(o.Folder, "images", "stock_boot.bin")))
                    throw new InvalidOperationException(
                        "ms45-emu has no boot loader of its own to put the program on, which a .0PA does not have. " +
                        "Choose an external flash read, or keep the first 256 KB of one as images/stock_boot.bin in the ms45-emu folder.");
                if (!ownBoot) RequireFile(o.Image, DmeFlashSize, "external flash", "1 MB");
                // A program file brings the whole MPC flash with it; a calibration file does not.
                if (!program && !string.IsNullOrWhiteSpace(o.Calibration) && string.IsNullOrWhiteSpace(o.Mpc))
                    throw new InvalidOperationException(
                        "Choose a program file (.0PA) to go with the calibration: on its own a .0DA leaves the DME without a program and an MPC flash.");
                if (!program) RequireFile(o.Mpc, DmeMpcSize, "MPC flash", "448 KB");
                c.FileName = FirstExisting(Path.Combine(o.Folder, ".venv", "bin", "python")) ?? "python3";
                // The external flash alone tells the emulator to write the program over
                // this read, whatever pair it has in its own images folder; neither, to
                // use the boot loader it keeps.
                c.Environment["MS45_FLASH"] = ownBoot ? string.Empty : o.Image;
                c.Environment["MS45_MPC"] = program ? string.Empty : o.Mpc;
                c.Arguments.AddRange(new[] { "--pair", "stock", "--eeprom", eeprom });
                AddDaten(c, o);
                if (o.Turbo) c.Arguments.Add("--turbo");
                if (resume) c.Arguments.Add("--resume");
            }
            else
            {
                string saved = Path.Combine(state, "tcu-flash.bin");
                bool resume = o.Resume && File.Exists(saved);
                bool daten = !resume && !(string.IsNullOrWhiteSpace(o.Program) && string.IsNullOrWhiteSpace(o.Calibration));
                string image = resume ? saved : o.Image;
                // A program or calibration file can go on the stock image gs20-emu keeps for itself.
                if (daten && string.IsNullOrWhiteSpace(image))
                {
                    if (!File.Exists(Path.Combine(o.Folder, "images", "stock_512k.bin")))
                        throw new InvalidOperationException(
                            "gs20-emu has no stock image of its own (images/stock_512k.bin) to take a boot block from, which a .0PA or .0DA does not have.");
                    image = "stock";
                }
                else
                {
                    RequireFile(image, TcuImageSize, "image", "512 KB");
                }
                // The module runs eight million instructions a second. PyPy
                // keeps up; CPython manages about one.
                string pypy = FirstExisting(Path.Combine(o.Folder, ".pypy", "bin", "pypy3")) ?? OnPath("pypy3");
                c.FileName = pypy ?? FirstExisting(Path.Combine(o.Folder, ".venv", "bin", "python")) ?? "python3";
                if (pypy == null)
                    c.Note = "No PyPy found (gs20-emu/.pypy or pypy3 on the PATH): under plain Python the emulated " +
                             "transmission runs at about an eighth of its speed, and jobs will time out.";
                c.Arguments.AddRange(new[] { "--image", image, "--save", saved });
                // The saved flash is what the last session left: nothing is written over it.
                if (!resume) AddDaten(c, o);
            }
            return c;
        }

        private static void AddDaten(EmulatorCommand c, EmulatorOptions o)
        {
            foreach (var (option, path, what) in new[]
                     { ("--program", o.Program, "program file (.0PA)"), ("--calibration", o.Calibration, "calibration file (.0DA)") })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!File.Exists(path))
                    throw new InvalidOperationException("The " + what + " is not there any more: " + path);
                c.Arguments.Add(option);
                c.Arguments.Add(path);
            }
        }

        /// <summary>The files an emulator is booted on, in a few words: for the label and the log.</summary>
        public static string Describe(EmulatedUnit unit, EmulatorOptions o)
        {
            var parts = new List<string>();
            bool program = !string.IsNullOrWhiteSpace(o.Program);
            if (program) parts.Add(Path.GetFileName(o.Program));
            else if (!string.IsNullOrWhiteSpace(o.Image)) parts.Add(Path.GetFileName(o.Image));
            else if (unit == EmulatedUnit.Tcu && !string.IsNullOrWhiteSpace(o.Calibration)) parts.Add("the stock image");
            if (unit == EmulatedUnit.Dme && !program && !string.IsNullOrWhiteSpace(o.Mpc)) parts.Add(Path.GetFileName(o.Mpc));
            if (!string.IsNullOrWhiteSpace(o.Calibration)) parts.Add(Path.GetFileName(o.Calibration));
            string text = string.Join(" + ", parts);
            if (program)
                text += " on the boot " + (unit == EmulatedUnit.Dme ? "loader" : "block") +
                        (!string.IsNullOrWhiteSpace(o.Image) ? " of " + Path.GetFileName(o.Image)
                         : unit == EmulatedUnit.Dme ? " ms45-emu keeps" : " of the stock image");
            return text;
        }

        private static void RequireFile(string path, int size, string what, string sizeText)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("Choose the " + what + " file first.");
            // a link to the image (the emulators' own images folders are full of them) has the link's length
            var info = new FileInfo(path);
            long length = (info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info).Length;
            if (length != size)
                throw new InvalidOperationException(
                    Path.GetFileName(path) + " is " + length + " bytes; the " + what + " is " + sizeText + " (" + size + " bytes).");
        }

        private static string FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

        private static string OnPath(string program)
        {
            string path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            // A GUI app on macOS is not given the shell's PATH; Homebrew's folders are added by hand.
            foreach (string dir in path.Split(Path.PathSeparator).Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin" }))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, program);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        // ---- running it ---------------------------------------------------------------

        /// <summary>Boots the emulator. Returns the command's note for the user, if it has one.</summary>
        public string Start(EmulatorOptions options)
        {
            EmulatorCommand command = BuildCommand(Unit, options);
            lock (_gate)
            {
                if (Running) throw new InvalidOperationException("The emulated " + Name + " is already running.");
                WsPort = command.WsPort;
                Directory.CreateDirectory(string.IsNullOrWhiteSpace(options.StateDir)
                    ? Path.Combine(options.Folder, "images") : options.StateDir);

                var info = new ProcessStartInfo
                {
                    FileName = command.FileName,
                    WorkingDirectory = command.WorkingDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (string a in command.Arguments) info.ArgumentList.Add(a);
                foreach (var pair in command.Environment) info.Environment[pair.Key] = pair.Value;

                var process = new Process { StartInfo = info, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => { if (e.Data != null) Feed(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) Feed(e.Data); };
                process.Exited += (_, _) => OnExited(process);

                Port = null;
                Ready = false;
                SwitchOn = false;
                ImageName = Describe(Unit, options);
                Status = "booting";
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
                Running = true;
            }
            Changed?.Invoke();
            return command.Note;
        }

        /// <summary>Switches the ignition (DME) or the power (TCU).</summary>
        public void SetSwitch(bool on) => Send(SwitchName + (on ? " on" : " off"));

        /// <summary>Asks the emulator to quit, which is when it saves its flash, and kills it if it does not.</summary>
        public void Stop()
        {
            Process process;
            lock (_gate) process = _process;
            if (process == null) return;
            try
            {
                Send("q");
                if (!process.WaitForExit(4000))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // already gone
            }
        }

        private void Send(string command)
        {
            Process process;
            lock (_gate) process = _process;
            if (process == null || process.HasExited) return;
            try
            {
                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
            }
            catch (Exception)
            {
                // the pipe went with the process
            }
        }

        private void OnExited(Process process)
        {
            lock (_gate)
            {
                if (_process != process) return;
                _process = null;
                Running = false;
                Ready = false;
                SwitchOn = false;
                Port = null;
                if (!Status.StartsWith("stopped: ", StringComparison.Ordinal)) Status = "not running";
            }
            Changed?.Invoke();
        }

        // ---- reading what it says -----------------------------------------------------

        private static readonly Regex PtyName = new Regex(@"/dev/(ttys\d+|pts/\d+)", RegexOptions.Compiled);
        private static readonly Regex ChosenCalibration = new Regex(@"^calibration: (\S+\.0[dD][aA]),", RegexOptions.Compiled);

        /// <summary>
        /// Takes one line of the emulator's output and updates the state from
        /// it. Public so the parsing can be tested without a process.
        /// </summary>
        public void Feed(string line)
        {
            string text = line.Trim();
            bool changed = true;
            lock (_gate)
            {
                if (text.StartsWith("K line on ", StringComparison.Ordinal))
                {
                    Match m = PtyName.Match(text);
                    if (m.Success) Port = m.Value;
                }
                else if (text.StartsWith("DME running", StringComparison.Ordinal) ||
                         text.StartsWith("module running", StringComparison.Ordinal))
                {
                    Ready = true;
                    SwitchOn = true;
                    Status = "running, " + SwitchName + " on";
                }
                else if (text.StartsWith("resuming on the saved flash", StringComparison.Ordinal))
                {
                    Status = text;
                }
                else if (ChosenCalibration.Match(text) is { Success: true } chosen)
                {
                    // the emulator found a calibration for a program that came without one
                    string name = chosen.Groups[1].Value;
                    int at = (ImageName ?? string.Empty).IndexOf(" on the boot ", StringComparison.Ordinal);
                    ImageName = at < 0 ? ImageName + " + " + name : ImageName.Insert(at, " + " + name);
                    Status = "booting with " + name;
                }
                else if (text.StartsWith(SwitchName + " off", StringComparison.Ordinal))
                {
                    SwitchOn = false;
                    Status = Unit == EmulatedUnit.Dme && text.Contains("after-run")
                        ? "ignition off: after-run in progress"
                        : SwitchName + " off";
                }
                else if (text.StartsWith("DME off", StringComparison.Ordinal))
                {
                    Status = "ignition off, " + text.Substring(4);          // "off (after-run done in 35.1 s)"
                }
                else if (text.StartsWith(SwitchName + " on", StringComparison.Ordinal))
                {
                    SwitchOn = true;
                    Status = text;
                }
                else if (text.StartsWith("DME reset", StringComparison.Ordinal) ||
                         text.StartsWith("the module reset itself", StringComparison.Ordinal) ||
                         text.StartsWith("fault:", StringComparison.Ordinal) ||
                         text.StartsWith("flash changed", StringComparison.Ordinal))
                {
                    Status = text;
                }
                else if (text.Contains("Error:") || text.StartsWith("usage:", StringComparison.Ordinal))
                {
                    // Python's last line of a traceback: why it would not start
                    Status = "stopped: " + text;
                }
                else
                {
                    changed = false;
                }
            }
            Line?.Invoke(line);
            if (changed) Changed?.Invoke();
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// The emulators, and which of them the app is talking to instead of the
    /// cable. While one is connected every job the app runs goes to it:
    /// <see cref="Port"/> is what the rest of the app must use as its port.
    /// </summary>
    public static class Emulation
    {
        public static readonly EmulatorProcess Dme = new EmulatorProcess(EmulatedUnit.Dme);
        public static readonly EmulatorProcess Tcu = new EmulatorProcess(EmulatedUnit.Tcu);

        /// <summary>The emulator the app is connected to, or null for the cable.</summary>
        public static EmulatorProcess Connected { get; private set; }

        /// <summary>The app is talking to an emulator, not to a car.</summary>
        public static bool Active => Connected != null;

        /// <summary>Raised when the connection changes.</summary>
        public static event Action Changed;

        /// <summary>
        /// A port name that can never open: what the app is given while it is
        /// connected to an emulator that is not running, so that nothing falls
        /// through to a real cable by accident.
        /// </summary>
        public const string NotRunningPort = "/dev/null/emulator-not-running";

        /// <summary>The port to use in place of the cable's, or null when the cable is the connection.</summary>
        public static string Port => Connected == null ? null : (Connected.Port ?? NotRunningPort);

        public static void Connect(EmulatorProcess emulator)
        {
            if (Connected == emulator) return;
            Connected = emulator;
            Changed?.Invoke();
        }

        /// <summary>Whether a port name is an emulator's pseudo-terminal rather than a serial device.</summary>
        public static bool IsEmulatorPort(string port) =>
            !string.IsNullOrEmpty(port) &&
            (port == NotRunningPort || port == Dme.Port || port == Tcu.Port);

        /// <summary>"EMULATED DME" and what it runs, for the banner, the title and the logs.</summary>
        public static string Label =>
            Connected == null ? null
            : "EMULATED " + Connected.Name + " (" + Connected.Project +
              (string.IsNullOrEmpty(Connected.ImageName) ? "" : ", " + Connected.ImageName) + ")";

        /// <summary>Where the emulators keep a session's EEPROM and flash.</summary>
        public static string StateDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bmweb-flasher", "emulator");

        /// <summary>
        /// Looks for an emulator's checkout: next to this app's own checkout,
        /// then where the projects usually live. Null when it is not found.
        /// </summary>
        public static string FindFolder(string project)
        {
            var candidates = new List<string>();
            string env = Environment.GetEnvironmentVariable(project.ToUpperInvariant().Replace('-', '_') + "_DIR");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            // walk up from the executable: a build inside the repo has the sibling repos a few levels above
            for (DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                candidates.Add(Path.Combine(d.FullName, project));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "Development", "code", "projects", project));
            candidates.Add(Path.Combine(home, project));
            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "tools", "kline.py")));
        }

        /// <summary>Shuts both emulators down; called when the app closes.</summary>
        public static void StopAll()
        {
            Dme.Stop();
            Tcu.Stop();
        }
    }
}
