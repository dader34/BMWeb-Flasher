using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using BmwebFlasher;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The Emulator screen's machinery, without the window: how an emulator
    /// is started, how its output is read, and what the app is given as its
    /// port while one is connected. The last two tests boot the real
    /// emulators and talk to them, and are skipped where those are not
    /// checked out next to this repository.
    /// </summary>
    public class EmulationTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmweb-emu-" + Guid.NewGuid().ToString("N"));

        public EmulationTests() { Directory.CreateDirectory(_dir); }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private string Folder(string name, params string[] files)
        {
            string folder = Path.Combine(_dir, name);
            foreach (string f in files.Append(Path.Combine("tools", "kline.py")))
            {
                string path = Path.Combine(folder, f);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "");
            }
            return folder;
        }

        private string FileOfSize(string name, long size)
        {
            string path = Path.Combine(_dir, name);
            using (var f = File.Create(path)) f.SetLength(size);
            return path;
        }

        // ---- the command line ----------------------------------------------------

        [SkippableFact]
        public void Dme_command_runs_the_emulators_python_on_the_chosen_pair()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Folder("ms45-emu", Path.Combine(".venv", "bin", "python"));
            string flash = FileOfSize("flash.bin", 0x100000), mpc = FileOfSize("mpc.bin", 0x70000);

            EmulatorCommand c = EmulatorProcess.BuildCommand(EmulatedUnit.Dme, new EmulatorOptions
            {
                Folder = folder, Image = flash, Mpc = mpc, Turbo = true, Resume = true, StateDir = _dir,
            });

            Assert.Equal(Path.Combine(folder, ".venv", "bin", "python"), c.FileName);
            Assert.Equal(folder, c.WorkingDirectory);
            Assert.Equal(flash, c.Environment["MS45_FLASH"]);
            Assert.Equal(mpc, c.Environment["MS45_MPC"]);
            Assert.Equal(new[] { "-u", Path.Combine("tools", "kline.py"), "--attached", "--ws", "8767", "--pair", "stock",
                                 "--eeprom", Path.Combine(_dir, "dme-eeprom.bin"), "--turbo" }, c.Arguments);
            // nothing was saved by an earlier session, so there is nothing to resume on
            Assert.DoesNotContain("--resume", c.Arguments);

            File.WriteAllText(Path.Combine(_dir, "dme-eeprom.bin.flash.bin"), "");
            File.WriteAllText(Path.Combine(_dir, "dme-eeprom.bin.mpc.bin"), "");
            c = EmulatorProcess.BuildCommand(EmulatedUnit.Dme, new EmulatorOptions
            {
                Folder = folder, Image = flash, Mpc = mpc, Resume = true, StateDir = _dir,
            });
            Assert.Contains("--resume", c.Arguments);
            Assert.DoesNotContain("--turbo", c.Arguments);
        }

        [SkippableFact]
        public void Tcu_command_prefers_pypy_and_resumes_on_the_saved_flash()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Folder("gs20-emu", Path.Combine(".pypy", "bin", "pypy3"), Path.Combine(".venv", "bin", "python"));
            string image = FileOfSize("tcu.bin", 0x80000);

            EmulatorCommand c = EmulatorProcess.BuildCommand(EmulatedUnit.Tcu, new EmulatorOptions
            {
                Folder = folder, Image = image, Resume = true, StateDir = _dir,
            });
            Assert.Equal(Path.Combine(folder, ".pypy", "bin", "pypy3"), c.FileName);
            Assert.Null(c.Note);
            Assert.Equal(new[] { "-u", Path.Combine("tools", "kline.py"), "--attached", "--ws", "8770", "--image", image,
                                 "--save", Path.Combine(_dir, "tcu-flash.bin") }, c.Arguments);

            string saved = FileOfSize("tcu-flash.bin", 0x80000);
            c = EmulatorProcess.BuildCommand(EmulatedUnit.Tcu, new EmulatorOptions
            {
                Folder = folder, Image = image, Resume = true, StateDir = _dir,
            });
            Assert.Equal(saved, c.Arguments[c.Arguments.IndexOf("--image") + 1]);
        }

        [SkippableFact]
        public void A_missing_folder_or_a_file_of_the_wrong_size_is_refused_with_a_reason()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Folder("ms45-emu");
            string flash = FileOfSize("flash.bin", 0x100000), mpc = FileOfSize("mpc.bin", 0x70000);

            var e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(
                EmulatedUnit.Dme, new EmulatorOptions { Folder = Path.Combine(_dir, "nowhere"), Image = flash, Mpc = mpc }));
            Assert.Contains("ms45-emu", e.Message);

            e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(
                EmulatedUnit.Dme, new EmulatorOptions { Folder = folder, Image = mpc, Mpc = mpc }));
            Assert.Contains("external flash", e.Message);

            e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(
                EmulatedUnit.Dme, new EmulatorOptions { Folder = folder, Image = flash, Mpc = null }));
            Assert.Contains("MPC", e.Message);

            e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(
                EmulatedUnit.Tcu, new EmulatorOptions { Folder = Folder("gs20-emu"), Image = flash }));
            Assert.Contains("512 KB", e.Message);
        }

        [SkippableFact]
        public void A_program_file_goes_on_the_dmes_external_flash_alone()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Folder("ms45-emu");
            string flash = FileOfSize("flash.bin", 0x100000);
            string program = FileOfSize("7549388A.0PA", 10), calibration = FileOfSize("P7561941.0DA", 10);

            var options = new EmulatorOptions { Folder = folder, Image = flash, Program = program, StateDir = _dir };
            EmulatorCommand c = EmulatorProcess.BuildCommand(EmulatedUnit.Dme, options);
            Assert.Equal(flash, c.Environment["MS45_FLASH"]);
            Assert.Equal(string.Empty, c.Environment["MS45_MPC"]);          // the program brings the MPC flash
            Assert.Equal(new[] { "--pair", "stock", "--eeprom", Path.Combine(_dir, "dme-eeprom.bin"), "--program", program },
                         c.Arguments.Skip(5)); // after -u, kline.py, --attached, --ws, PORT
            Assert.Equal("7549388A.0PA on the boot loader of flash.bin", EmulatorProcess.Describe(EmulatedUnit.Dme, options));

            options.Calibration = calibration;
            c = EmulatorProcess.BuildCommand(EmulatedUnit.Dme, options);
            Assert.Equal(new[] { "--program", program, "--calibration", calibration }, c.Arguments.Skip(c.Arguments.Count - 4));

            // a .0PA has no boot loader: with no read chosen it goes on the one the emulator keeps, if it does
            var alone = new EmulatorOptions { Folder = folder, Program = program, StateDir = _dir };
            var e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(EmulatedUnit.Dme, alone));
            Assert.Contains("stock_boot.bin", e.Message);
            alone.Folder = Folder("ms45-with-boot", Path.Combine("images", "stock_boot.bin"));
            c = EmulatorProcess.BuildCommand(EmulatedUnit.Dme, alone);
            Assert.Equal(string.Empty, c.Environment["MS45_FLASH"]);
            Assert.Equal(string.Empty, c.Environment["MS45_MPC"]);
            Assert.Equal("--program", c.Arguments[c.Arguments.Count - 2]);
            Assert.Equal("7549388A.0PA on the boot loader ms45-emu keeps", EmulatorProcess.Describe(EmulatedUnit.Dme, alone));

            // and a calibration alone has no program or MPC flash to go with
            e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(
                EmulatedUnit.Dme, new EmulatorOptions { Folder = folder, Image = flash, Calibration = calibration }));
            Assert.Contains("program file", e.Message);

            e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(
                EmulatedUnit.Dme, new EmulatorOptions { Folder = folder, Image = flash, Program = Path.Combine(_dir, "gone.0PA") }));
            Assert.Contains("gone.0PA", e.Message);
        }

        [SkippableFact]
        public void A_program_file_goes_on_the_transmissions_image_or_on_the_emulators_stock_one()
        {
            Skip.If(OperatingSystem.IsWindows());
            string program = FileOfSize("7526396A.0PA", 10), calibration = FileOfSize("A7528453.0DA", 10);
            string image = FileOfSize("read.bin", 0x80000);

            // no image chosen, and the emulator has none of its own
            string bare = Folder("gs20-bare");
            var e = Assert.Throws<InvalidOperationException>(() => EmulatorProcess.BuildCommand(
                EmulatedUnit.Tcu, new EmulatorOptions { Folder = bare, Program = program, StateDir = _dir }));
            Assert.Contains("boot block", e.Message);

            string folder = Folder("gs20-emu", Path.Combine("images", "stock_512k.bin"));
            var options = new EmulatorOptions { Folder = folder, Program = program, StateDir = _dir };
            EmulatorCommand c = EmulatorProcess.BuildCommand(EmulatedUnit.Tcu, options);
            Assert.Equal(new[] { "--image", "stock", "--save", Path.Combine(_dir, "tcu-flash.bin"), "--program", program },
                         c.Arguments.Skip(5)); // after -u, kline.py, --attached, --ws, PORT
            Assert.Equal("7526396A.0PA on the boot block of the stock image", EmulatorProcess.Describe(EmulatedUnit.Tcu, options));

            // a calibration alone goes on the stock image too
            var alone = new EmulatorOptions { Folder = folder, Calibration = calibration, StateDir = _dir };
            c = EmulatorProcess.BuildCommand(EmulatedUnit.Tcu, alone);
            Assert.Equal(new[] { "--image", "stock", "--save", Path.Combine(_dir, "tcu-flash.bin"), "--calibration", calibration },
                         c.Arguments.Skip(5)); // after -u, kline.py, --attached, --ws, PORT
            Assert.Equal("the stock image + A7528453.0DA", EmulatorProcess.Describe(EmulatedUnit.Tcu, alone));

            options.Image = image;
            options.Calibration = calibration;
            c = EmulatorProcess.BuildCommand(EmulatedUnit.Tcu, options);
            Assert.Equal(new[] { "--image", image, "--save", Path.Combine(_dir, "tcu-flash.bin"),
                                 "--program", program, "--calibration", calibration }, c.Arguments.Skip(5)); // after -u, kline.py, --attached, --ws, PORT
            Assert.Equal("7526396A.0PA + A7528453.0DA on the boot block of read.bin", EmulatorProcess.Describe(EmulatedUnit.Tcu, options));

            // the last session's flash is booted as it was left: nothing is written over it
            string saved = FileOfSize("tcu-flash.bin", 0x80000);
            options.Resume = true;
            c = EmulatorProcess.BuildCommand(EmulatedUnit.Tcu, options);
            Assert.Equal(new[] { "--image", saved, "--save", saved }, c.Arguments.Skip(5)); // after -u, kline.py, --attached, --ws, PORT
        }

        // ---- reading the emulator's output -----------------------------------------

        [Fact]
        public void The_dme_emulators_output_is_followed()
        {
            var dme = new EmulatorProcess(EmulatedUnit.Dme);
            int changes = 0;
            dme.Changed += () => changes++;

            dme.Feed("K line on /dev/ttys012, ws://localhost:8766 (also images/kline.tty)");
            Assert.Equal("/dev/ttys012", dme.Port);
            Assert.False(dme.Ready);

            dme.Feed("booting...");
            dme.Feed("DME running (ignition on, engine off); q to quit, i for the ignition");
            Assert.True(dme.Ready);
            Assert.True(dme.SwitchOn);

            dme.Feed("ignition off: the program starts its after-run");
            Assert.False(dme.SwitchOn);
            Assert.Contains("after-run", dme.Status);
            dme.Feed("DME off (after-run done in 35.1 s)");
            Assert.Contains("35.1 s", dme.Status);

            dme.Feed("ignition on: DME reset; it will start its program");
            Assert.True(dme.SwitchOn);
            Assert.Contains("its program", dme.Status);

            dme.Feed("    5.084 (dme     5.585)  tester> b812f1021a80c3");     // a trace line changes nothing
            Assert.Equal(5, changes);
        }

        [Fact]
        public void The_tcu_emulators_output_is_followed()
        {
            var tcu = new EmulatorProcess(EmulatedUnit.Tcu);
            tcu.Feed("K line on /dev/ttys006, ws://localhost:8770");
            tcu.Feed("module running on stock (pypy); q to quit, p for the power");
            Assert.Equal("/dev/ttys006", tcu.Port);
            Assert.True(tcu.Ready && tcu.SwitchOn);

            tcu.Feed("power off");
            Assert.False(tcu.SwitchOn);
            tcu.Feed("power on: the module starts from its reset vector");
            Assert.True(tcu.SwitchOn);

            tcu.Feed("fault: undefined opcode 0xF8 at 0x0A003C");
            Assert.Contains("0x0A003C", tcu.Status);
            tcu.Feed("the module reset itself (3 software resets so far)");
            Assert.Contains("reset itself", tcu.Status);
        }

        [Fact]
        public void A_calibration_the_emulator_finds_is_added_to_the_label()
        {
            var tcu = new EmulatorProcess(EmulatedUnit.Tcu);
            typeof(EmulatorProcess).GetProperty(nameof(EmulatorProcess.ImageName))
                .SetValue(tcu, "7526396A.0PA on the boot block of the stock image");
            tcu.Feed("calibration: A7528453.0DA, the first one next to the program that the boot block takes with it (name another with a .0DA)");
            Assert.Equal("7526396A.0PA + A7528453.0DA on the boot block of the stock image", tcu.ImageName);
            tcu.Feed("module running on 7526396A.0PA + A7528453.0DA on the stock boot block (pypy); q to quit, p for the power");
            Assert.True(tcu.Ready);
        }

        [Fact]
        public void A_start_that_fails_says_why()
        {
            var dme = new EmulatorProcess(EmulatedUnit.Dme);
            dme.Feed("Traceback (most recent call last):");
            dme.Feed("ValueError: stock: program is b'0044570LO00S', expected b'0044570LO02S'");
            Assert.StartsWith("stopped: ValueError", dme.Status);
        }

        // ---- what the app is given as its port ----------------------------------------

        [Fact]
        public void A_connected_emulator_that_is_not_running_gives_a_port_that_cannot_open()
        {
            try
            {
                Assert.Null(Emulation.Port);                       // the cable
                Emulation.Connect(Emulation.Tcu);
                Assert.True(Emulation.Active);
                Assert.Equal(Emulation.NotRunningPort, Emulation.Port);
                Assert.True(Emulation.IsEmulatorPort(Emulation.Port));
                Assert.False(Emulation.IsEmulatorPort("/dev/cu.usbserial-AH01BRM5"));
                Assert.StartsWith("EMULATED TCU (gs20-emu", Emulation.Label);
                Assert.False(File.Exists(Emulation.NotRunningPort));
            }
            finally
            {
                Emulation.Connect(null);
            }
            Assert.Null(Emulation.Port);
            Assert.Null(Emulation.Label);
        }

        // ---- the real emulators --------------------------------------------------------

        private static void WaitFor(Func<bool> condition, int seconds, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < until, "timed out waiting for " + what);
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Boots the emulated transmission the way the Emulator screen does and
        /// works on it with the app's own DS2 link: identification, the change
        /// to 125000 baud that a pseudo-terminal cannot follow, a power cut
        /// and a power-up.
        /// </summary>
        [SkippableFact]
        public void The_emulated_transmission_boots_identifies_and_follows_its_power_switch()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Emulation.FindFolder("gs20-emu");
            Skip.If(folder == null, "gs20-emu is not checked out next to this repository");
            string image = Path.Combine(folder, "images", "stock_512k.bin");
            Skip.IfNot(File.Exists(image), "no stock image in gs20-emu/images");
            Skip.IfNot(File.Exists(Path.Combine(folder, ".pypy", "bin", "pypy3")), "gs20-emu has no PyPy: it would be too slow to answer in time");

            var log = new StringBuilder();
            using var tcu = new EmulatorProcess(EmulatedUnit.Tcu);
            tcu.Line += line => { lock (log) log.AppendLine(line); };
            tcu.Start(new EmulatorOptions { Folder = folder, Image = image, StateDir = _dir, WsPort = 0 }); // no WebSocket: the app may hold the port
            try
            {
                WaitFor(() => tcu.Ready || !tcu.Running, 30, "the emulator to boot\n" + log);
                Assert.True(tcu.Running, "the emulator stopped: " + tcu.Status + "\n" + log);
                Assert.StartsWith("/dev/", tcu.Port);
                Thread.Sleep(1500);                                 // the module wants a quiet line before its first telegram

                // The app's link has to know the port for an emulator's, as it does once the emulator is its own.
                Assert.False(Emulation.IsEmulatorPort(tcu.Port));
                byte[] ident = Ds2Telegram.Build(Ds2Telegram.TcuAddress, new byte[] { 0x00 });
                using (var link = new EmulatorLink(tcu))
                {
                    byte[] reply = FirstAnswer(link.Link, ident);
                    Assert.Equal(Ds2Telegram.StatusOk, Ds2Telegram.Status(reply));
                    Assert.Equal("7552700", Encoding.ASCII.GetString(reply, 3, 7));

                    link.Link.SwitchBaud(Ds2SerialLink.FastBaud);
                    Assert.Equal(Ds2SerialLink.FastBaud, link.Link.Baud);
                    Assert.Equal(Ds2Telegram.StatusOk, Ds2Telegram.Status(link.Link.Transfer(ident, 2000)));
                    link.Link.SwitchBaud(Ds2SerialLink.DefaultBaud);

                    tcu.SetSwitch(false);
                    WaitFor(() => !tcu.SwitchOn, 5, "the power to go off");
                    Assert.Throws<TimeoutException>(() => link.Link.Transfer(ident, 700));

                    tcu.SetSwitch(true);
                    WaitFor(() => tcu.SwitchOn, 5, "the power to come back");
                    Assert.Equal(Ds2Telegram.StatusOk, Ds2Telegram.Status(FirstAnswer(link.Link, ident)));
                }
            }
            finally
            {
                tcu.Stop();
            }
            WaitFor(() => !tcu.Running, 6, "the emulator to stop");
            Assert.Null(tcu.Port);
        }

        /// <summary>BMW's data files on this machine: a folder of SP-Daten, named by a variable or where they are kept here.</summary>
        private static string DatenFile(string variable, string folder, string name)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Environment.GetEnvironmentVariable(variable) ?? Path.Combine(home, "Desktop", "e46bins", folder);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir).FirstOrDefault(
                f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// An older program straight from SP-Daten, with nothing else chosen:
        /// the emulator puts it on its stock image's boot block, finds a
        /// calibration that goes with it in the same folder, and the module
        /// that answers is that release.
        /// </summary>
        [SkippableFact]
        public void The_emulated_transmission_boots_from_a_program_file()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Emulation.FindFolder("gs20-emu");
            Skip.If(folder == null, "gs20-emu is not checked out next to this repository");
            Skip.IfNot(File.Exists(Path.Combine(folder, "images", "stock_512k.bin")), "no stock image in gs20-emu/images");
            Skip.IfNot(File.Exists(Path.Combine(folder, ".pypy", "bin", "pypy3")), "gs20-emu has no PyPy: it would be too slow to answer in time");
            string program = DatenFile("GS20_DATEN", Path.Combine("GS20-gearbox", "sp-daten", "GD20"), "7526396A.0PA");
            Skip.If(program == null, "no SP-Daten for the GS20 on this machine (GS20_DATEN)");

            var log = new StringBuilder();
            using var tcu = new EmulatorProcess(EmulatedUnit.Tcu);
            tcu.Line += line => { lock (log) log.AppendLine(line); };
            tcu.Start(new EmulatorOptions { Folder = folder, Program = program, StateDir = _dir, WsPort = 0 }); // no WebSocket: the app may hold the port
            try
            {
                WaitFor(() => tcu.Ready || !tcu.Running, 30, "the emulator to boot\n" + log);
                Assert.True(tcu.Running, "the emulator stopped: " + tcu.Status + "\n" + log);
                Assert.Matches(@"^7526396A\.0PA \+ \S+\.0DA on the boot block of the stock image$", tcu.ImageName);
                Thread.Sleep(1500);

                using (var link = new EmulatorLink(tcu))
                {
                    byte[] reply = FirstAnswer(link.Link, Ds2Telegram.Build(Ds2Telegram.TcuAddress, new byte[] { 0x00 }));
                    Assert.Equal(Ds2Telegram.StatusOk, Ds2Telegram.Status(reply));
                    Assert.Equal("7526396", Encoding.ASCII.GetString(reply, 3, 7));
                }
            }
            finally
            {
                tcu.Stop();
            }
            WaitFor(() => !tcu.Running, 6, "the emulator to stop");
        }

        /// <summary>
        /// The same for the DME: an older program from SP-Daten, with nothing
        /// else chosen where ms45-emu keeps a boot loader (and on that of an
        /// external flash read where it does not), answers with its own part
        /// number.
        /// </summary>
        [SkippableFact]
        public void The_emulated_dme_boots_from_a_program_file()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Emulation.FindFolder("ms45-emu");
            Skip.If(folder == null, "ms45-emu is not checked out next to this repository");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string flash = Environment.GetEnvironmentVariable("MS45_FLASH")
                ?? Path.Combine(home, "Desktop", "e46bins", "MS45-DME", "RPM_DME_BACKUP", "NJ87379_0044570_Flash.bin");
            if (File.Exists(Path.Combine(folder, "images", "stock_boot.bin"))) flash = null;
            Skip.IfNot(flash == null || File.Exists(flash), "no MS45.1 boot loader or external flash read on this machine");
            string program = DatenFile("MS45_DATEN", Path.Combine("MS45-DME", "sp-daten", "MDS451"), "7549388A.0PA");
            Skip.If(program == null, "no SP-Daten for the MS45.1 on this machine (MS45_DATEN)");

            var log = new StringBuilder();
            using var dme = new EmulatorProcess(EmulatedUnit.Dme);
            dme.Line += line => { lock (log) log.AppendLine(line); };
            dme.Start(new EmulatorOptions { Folder = folder, Image = flash, Program = program, StateDir = _dir, WsPort = 0 }); // no WebSocket: the app may hold the port
            try
            {
                WaitFor(() => dme.Ready || !dme.Running, 60, "the emulator to boot\n" + log);
                Assert.True(dme.Running, "the emulator stopped: " + dme.Status + "\n" + log);
                Assert.Contains("0044570LN00S", log.ToString());

                using (var port = new SerialPort(dme.Port, 9600, Parity.Even, 8, StopBits.One) { ReadTimeout = 5000 })
                {
                    port.Open();
                    byte[] request = { 0xB8, 0x12, 0xF1, 0x02, 0x1A, 0x80, 0xC3 };
                    port.Write(request, 0, request.Length);
                    var answer = new byte[request.Length + 12];
                    for (int got = 0; got < answer.Length;) got += port.Read(answer, got, answer.Length - got);
                    Assert.Equal(0x5A, answer[request.Length + 4]);                     // positive answer to 1A
                    // the part number in the answer, as BCD: 7549388, not the 7561382 of the read's own program
                    Assert.Equal(new byte[] { 0x07, 0x54, 0x93, 0x88 }, answer.Skip(request.Length + 8).Take(4));
                }
            }
            finally
            {
                dme.Stop();
            }
            WaitFor(() => !dme.Running, 8, "the emulator to stop");
        }

        /// <summary>
        /// The first answer of a module that has just been started. How long
        /// it takes to be listening depends on how busy the machine is (the
        /// emulator is slow until its interpreter has warmed up), so it is
        /// asked until it answers, as a tester would.
        /// </summary>
        private static byte[] FirstAnswer(Ds2SerialLink link, byte[] telegram)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return link.Transfer(telegram, 1500);
                }
                catch (TimeoutException) when (attempt < 8)
                {
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>The app's DS2 link on an emulator, with that emulator connected for as long as it lives.</summary>
        private sealed class EmulatorLink : IDisposable
        {
            public readonly Ds2SerialLink Link;

            public EmulatorLink(EmulatorProcess emulator)
            {
                // Only the two emulators the app owns are known to it, so stand this one in for the TCU's.
                typeof(EmulatorProcess).GetProperty(nameof(EmulatorProcess.Port)).SetValue(Emulation.Tcu, emulator.Port);
                Emulation.Connect(Emulation.Tcu);
                Link = new Ds2SerialLink(Emulation.Port);
            }

            public void Dispose()
            {
                Link.Dispose();
                Emulation.Connect(null);
                typeof(EmulatorProcess).GetProperty(nameof(EmulatorProcess.Port)).SetValue(Emulation.Tcu, null);
            }
        }

        /// <summary>
        /// Boots the emulated DME on a pair and asks it who it is with a raw
        /// KWP2000* telegram on its pseudo-terminal, then turns its ignition
        /// off. (EDIABAS jobs on the same line are what tools/EmuFlash runs.)
        /// </summary>
        [SkippableFact]
        public void The_emulated_dme_boots_identifies_and_follows_its_ignition_switch()
        {
            Skip.If(OperatingSystem.IsWindows());
            string folder = Emulation.FindFolder("ms45-emu");
            Skip.If(folder == null, "ms45-emu is not checked out next to this repository");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string flash = Environment.GetEnvironmentVariable("MS45_FLASH")
                ?? Path.Combine(home, "Desktop", "e46bins", "MS45-DME", "RPM_DME_BACKUP", "NJ87379_0044570_Flash.bin");
            string mpc = Environment.GetEnvironmentVariable("MS45_MPC")
                ?? Path.Combine(home, "Desktop", "e46bins", "MS45-DME", "RPM_DME_BACKUP", "NJ87379_0044570_MPC.bin");
            Skip.IfNot(File.Exists(flash) && File.Exists(mpc), "no MS45.1 pair on this machine");

            var log = new StringBuilder();
            using var dme = new EmulatorProcess(EmulatedUnit.Dme);
            dme.Line += line => { lock (log) log.AppendLine(line); };
            dme.Start(new EmulatorOptions { Folder = folder, Image = flash, Mpc = mpc, StateDir = _dir, WsPort = 0 }); // no WebSocket: the app may hold the port
            try
            {
                WaitFor(() => dme.Ready || !dme.Running, 60, "the emulator to boot\n" + log);
                Assert.True(dme.Running, "the emulator stopped: " + dme.Status + "\n" + log);

                using (var port = new SerialPort(dme.Port, 9600, Parity.Even, 8, StopBits.One) { ReadTimeout = 3000 })
                {
                    port.Open();
                    byte[] request = { 0xB8, 0x12, 0xF1, 0x02, 0x1A, 0x80, 0xC3 };
                    port.Write(request, 0, request.Length);
                    var answer = new byte[request.Length + 5];
                    for (int got = 0; got < answer.Length;) got += port.Read(answer, got, answer.Length - got);
                    Assert.Equal(request, answer.Take(request.Length));                 // the K line's echo
                    Assert.Equal(new byte[] { 0xB8, 0xF1, 0x12 }, answer.Skip(request.Length).Take(3));
                    Assert.Equal(0x5A, answer[request.Length + 4]);                     // positive answer to 1A
                }

                dme.SetSwitch(false);
                WaitFor(() => !dme.SwitchOn, 5, "the ignition to go off");
                Assert.Contains("after-run", dme.Status);
            }
            finally
            {
                dme.Stop();
            }
            WaitFor(() => !dme.Running, 8, "the emulator to stop");
        }
    }
}
