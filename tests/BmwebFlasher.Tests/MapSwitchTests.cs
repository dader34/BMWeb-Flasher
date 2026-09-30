using System;
using System.IO;
using System.Text;
using Xunit;

namespace BmwebFlasher.Tests
{
    /// <summary>
    /// The synthetic tests build the smallest pair MapSwitch will accept: the
    /// right version strings, the stock instructions at every patch site, and
    /// empty free areas. They prove the patch lands where it should and nowhere
    /// else, not that it runs - that needs a car.
    ///
    /// The fixture tests use a real matching pair. Those images are BMW-derived
    /// and are not in the repo. Point MS45_MAPSWITCH_FLASH and MS45_MAPSWITCH_MPC
    /// at an unpatched 0044570LO02S pair to run them; MS45_MAPSWITCH_REF_FLASH
    /// and MS45_MAPSWITCH_REF_MPC may point at the output of the reference
    /// builder for the same pair to compare byte for byte.
    /// </summary>
    public class MapSwitchTests
    {
        private static readonly int[] LookupEntries =
        {
            0xCFC8, 0xD054, 0xD0E4, 0xD178, 0xD210, 0xD264, 0xD2C0, 0xD31C,
            0xD380, 0xD38C, 0xD39C, 0xD3B8, 0xD3DC, 0xD44C, 0xD4C0, 0xD660,
        };

        private static readonly uint[] LookupStock =
        {
            0x89830000, 0x88E30000, 0xA1830000, 0xA0E30000, 0x88A30000, 0x88A30000, 0xA0A30000, 0xA0A30000,
            0x898DD7C6, 0x898DD7C6, 0x898DD7C6, 0x898DD7C6, 0x38E30000, 0x38E30000, 0x88ADD7C4, 0x88ADD7C4,
        };

        private const int NvDescriptor = 0x28AC + 56 * 0x1C;
        private const int CodeStart = 0x6E550;

        private static void Put32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        private static uint Get32(byte[] data, int offset)
            => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) | data[offset + 3];

        private static byte[] SyntheticCalibration(byte fill)
        {
            var cal = new byte[MapSwitch.CalibrationLength];
            for (int i = 0; i < cal.Length; i++)
                cal[i] = 0xFF;
            for (int i = 0x200; i < 0x15000; i++)
                cal[i] = fill;
            Encoding.ASCII.GetBytes("0044570LO00S").CopyTo(cal, 0x10);
            return cal;
        }

        private static (byte[] Flash, byte[] Mpc) SyntheticPair()
        {
            var flash = new byte[MapSwitch.FullFlashLength];
            for (int i = 0; i < flash.Length; i++)
                flash[i] = 0xFF;
            for (int i = 0x60400; i < 0xDC000; i++)
                flash[i] = 0x11;
            Encoding.ASCII.GetBytes(MapSwitch.SupportedProgramVersion).CopyTo(flash, 0x6031C);
            SyntheticCalibration(0x22).CopyTo(flash, MapSwitch.CalibrationStart);

            var mpc = new byte[MapSwitch.MpcLength];
            for (int i = 0; i < CodeStart; i++)
                mpc[i] = 0x33;
            for (int i = CodeStart; i < mpc.Length; i++)
                mpc[i] = 0xFF;
            for (int i = 0; i < LookupEntries.Length; i++)
                Put32(mpc, LookupEntries[i], LookupStock[i]);
            Put32(mpc, 0x4B6C4, 0xB06DC4D8);
            Put32(mpc, NvDescriptor + 0, 0xFFFCA0F8);
            Put32(mpc, NvDescriptor + 4, 0xFFFCA104);
            Put32(mpc, NvDescriptor + 8, 0xFFFCA110);

            // The safety monitor's ranges, and the sum they give.
            for (int i = 0; i < RomTestRanges.Length; i++)
                Put32(flash, 0x60608 + 4 * i, RomTestRanges[i]);
            PutRomTestSum(flash, MapSwitch.RomTestSum(flash, mpc));
            return (flash, mpc);
        }

        private static readonly uint[] RomTestRanges =
        {
            0x0000BAE8, 0x0000F5F8, 0xFFF60630, 0xFFF68C2C, 0x00000140, 0x000002D4,
        };

        private static void PutRomTestSum(byte[] flash, ulong sum)
        {
            Put32(flash, 0x60600, (uint)(sum >> 32));
            Put32(flash, 0x60604, (uint)sum);
        }

        [Fact]
        public void AssemblerMatchesKnownEncodings()
        {
            // Encodings checked against a disassembler.
            Assert.Equal(0x880D89A5u, MapSwitch.Lbz(0, -0x765B, 13));
            Assert.Equal(0x70000001u, MapSwitch.AndiDot(0, 0, 1));
            Assert.Equal(0x54607C7Eu, MapSwitch.Srwi(0, 3, 17));
            Assert.Equal(0x28007FF2u, MapSwitch.Cmplwi(0, 0x7FF2));
            Assert.Equal(0x3C63000Au, MapSwitch.Addis(3, 3, 0xA));
            Assert.Equal(0xB06DC4D8u, MapSwitch.Sth(3, -0x3B28, 13));
            Assert.Equal(0x38601900u, MapSwitch.Li(3, 0x1900));
            Assert.Equal(0x48061588u, MapSwitch.Branch(0xCFC8, 0x6E550, false));
            Assert.Equal(0x4BF9EA5Cu, MapSwitch.Branch(0x6E570, 0xCFCC, false));
            Assert.Equal(0x480230CDu, MapSwitch.Branch(0x4B6C4, 0x6E790, true));
        }

        [Fact]
        public void BuildLeavesTheInputsAlone()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] flashBefore = (byte[])flash.Clone();
            byte[] mpcBefore = (byte[])mpc.Clone();

            MapSwitch.Build(flash, mpc, null, null);

            Assert.Equal(flashBefore, flash);
            Assert.Equal(mpcBefore, mpc);
        }

        [Fact]
        public void BuildTouchesOnlyThePatchSites()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);

            for (int i = 0; i < flash.Length; i++)
            {
                bool inMap2 = i >= MapSwitch.Map2Start && i < MapSwitch.Map2Start + MapSwitch.Map2Length;
                bool inRomTestSum = i >= 0x60600 && i < 0x60608;
                if (!inMap2 && !inRomTestSum)
                    Assert.True(flash[i] == r.Flash[i], "external flash changed at 0x" + i.ToString("X"));
            }

            for (int i = 0; i < mpc.Length; i++)
            {
                bool allowed = i >= CodeStart && i < CodeStart + r.CodeBytes;
                foreach (int entry in LookupEntries)
                    allowed |= i >= entry && i < entry + 4;
                allowed |= i >= 0x4B6C4 && i < 0x4B6C8;
                allowed |= i >= NvDescriptor && i < NvDescriptor + 12;
                if (!allowed)
                    Assert.True(mpc[i] == r.Mpc[i], "MPC changed at 0x" + i.ToString("X"));
            }
        }

        [Fact]
        public void EveryLookupHookRunsTheDisplacedInstructionAndReturns()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);

            for (int i = 0; i < LookupEntries.Length; i++)
            {
                uint stub = (uint)(CodeStart + 36 * i);
                Assert.Equal(MapSwitch.Branch((uint)LookupEntries[i], stub, false), Get32(r.Mpc, LookupEntries[i]));
                Assert.Equal(LookupStock[i], Get32(r.Mpc, (int)stub + 28));
                Assert.Equal(MapSwitch.Branch(stub + 32, (uint)LookupEntries[i] + 4, false),
                             Get32(r.Mpc, (int)stub + 32));
            }
        }

        [Fact]
        public void StoredDataRoutinesPointIntoThePatchArea()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);

            for (int i = 0; i < 3; i++)
            {
                uint target = Get32(r.Mpc, NvDescriptor + 4 * i);
                Assert.InRange(target, (uint)CodeStart, (uint)(CodeStart + r.CodeBytes - 4));
                Assert.Equal(0u, target & 3);
            }
        }

        [Fact]
        public void Map2DefaultsToACopyOfMap1()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);

            Assert.True(r.MapsIdentical);
            for (int i = 0; i < MapSwitch.Map2Length; i++)
                Assert.True(r.Flash[MapSwitch.CalibrationStart + i] == r.Flash[MapSwitch.Map2Start + i]);
        }

        [Fact]
        public void SuppliedMapsLandInTheirOwnSlots()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] one = SyntheticCalibration(0x44);
            byte[] two = SyntheticCalibration(0x55);

            MapSwitch.Result r = MapSwitch.Build(flash, mpc, one, two);

            Assert.False(r.MapsIdentical);
            Assert.Equal(0x44, r.Flash[MapSwitch.CalibrationStart + 0x1000]);
            Assert.Equal(0x55, r.Flash[MapSwitch.Map2Start + 0x1000]);
        }

        [Fact]
        public void TheSafetyMonitorSumFollowsTheHookedCode()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);

            // The hooks sit inside a summed range, so the sum must move,
            // and the stored value must be the one the patched code gives.
            Assert.NotEqual(MapSwitch.ReadRomTestSum(flash), MapSwitch.ReadRomTestSum(r.Flash));
            Assert.Equal(MapSwitch.RomTestSum(r.Flash, r.Mpc), MapSwitch.ReadRomTestSum(r.Flash));
        }

        [Fact]
        public void APatchedPairWithTheStockSumIsCorrected()
        {
            // What builds before the correction produced: patched code,
            // stock sum.
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);
            byte[] stale = (byte[])r.Flash.Clone();
            PutRomTestSum(stale, MapSwitch.ReadRomTestSum(flash));

            MapSwitch.Result again = MapSwitch.Build(stale, r.Mpc, null, null);

            Assert.Equal(r.Flash, again.Flash);
        }

        [Fact]
        public void AnUnpatchedPairWithAWrongSumIsRefused()
        {
            var (flash, mpc) = SyntheticPair();
            PutRomTestSum(flash, MapSwitch.ReadRomTestSum(flash) + 1);

            Assert.Throws<InvalidOperationException>(() => MapSwitch.Build(flash, mpc, null, null));
        }

        [Fact]
        public void APatchedPairIsRecognisedAndOnlyItsMapsChange()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result first = MapSwitch.Build(flash, mpc, null, null);

            Assert.False(MapSwitch.IsAlreadyPatched(mpc));
            Assert.True(MapSwitch.IsAlreadyPatched(first.Mpc));
            Assert.Null(MapSwitch.BlockedReason(first.Flash, first.Mpc));

            MapSwitch.Result second = MapSwitch.Build(first.Flash, first.Mpc, null, SyntheticCalibration(0x66));

            Assert.True(second.WasAlreadyPatched);
            Assert.Equal(first.Mpc, second.Mpc);
            Assert.Equal(0x66, second.Flash[MapSwitch.Map2Start + 0x1000]);
        }

        [Fact]
        public void TheEarlierVersionIsRecognisedAndUpdated()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result old = MapSwitch.Build(flash, mpc, null, null, MapSwitch.StartupIndication.Delayed);
            MapSwitch.Result fresh = MapSwitch.Build(flash, mpc, null, null);

            Assert.NotEqual(old.Mpc, fresh.Mpc);
            Assert.True(MapSwitch.IsAlreadyPatched(old.Mpc));
            Assert.False(MapSwitch.IsCurrentVersion(old.Mpc));
            Assert.True(MapSwitch.IsCurrentVersion(fresh.Mpc));
            Assert.Null(MapSwitch.BlockedReason(old.Flash, old.Mpc));
            Assert.True(MapSwitch.HasMap2(old.Flash, old.Mpc));

            // Updating the earlier version gives exactly what a fresh build gives.
            MapSwitch.Result updated = MapSwitch.Build(old.Flash, old.Mpc, null, null);

            Assert.True(updated.WasAlreadyPatched);
            Assert.True(updated.WasUpdated);
            Assert.Equal(fresh.Mpc, updated.Mpc);
            Assert.Equal(fresh.Flash, updated.Flash);
        }

        [Fact]
        public void EveryEarlierVersionUpdatesToTheSameImage()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result fresh = MapSwitch.Build(flash, mpc, null, null);

            foreach (var version in EarlierVersions)
            {
                MapSwitch.Result old = MapSwitch.Build(flash, mpc, null, null, version);
                Assert.True(MapSwitch.IsAlreadyPatched(old.Mpc));
                Assert.False(MapSwitch.IsCurrentVersion(old.Mpc));

                MapSwitch.Result updated = MapSwitch.Build(old.Flash, old.Mpc, null, null);
                Assert.True(updated.WasUpdated);
                Assert.Equal(fresh.Mpc, updated.Mpc);
                Assert.Equal(fresh.Flash, updated.Flash);
            }
        }

        // The earlier pedal-triggered versions, by their start-up indication.
        // (Pedals with ImmediateLong is still built today, as the other trigger.)
        private static readonly MapSwitch.StartupIndication[] EarlierVersions =
        {
            MapSwitch.StartupIndication.Immediate, MapSwitch.StartupIndication.Delayed,
            MapSwitch.StartupIndication.None,
        };

        [Fact]
        public void EitherTriggerCanBeBuiltAndTheyReplaceEachOther()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result dsc = MapSwitch.Build(flash, mpc, null, null, MapSwitch.Trigger.DscButton);
            MapSwitch.Result pedals = MapSwitch.Build(flash, mpc, null, null, MapSwitch.Trigger.Pedals);

            Assert.Equal(MapSwitch.Trigger.DscButton, MapSwitch.DefaultTrigger);
            Assert.Equal(dsc.Mpc, MapSwitch.Build(flash, mpc, null, null).Mpc);
            Assert.NotEqual(dsc.Mpc, pedals.Mpc);

            // Both are current versions, told apart by their trigger.
            foreach (var r in new[] { dsc, pedals })
            {
                Assert.True(MapSwitch.IsAlreadyPatched(r.Mpc));
                Assert.True(MapSwitch.IsCurrentVersion(r.Mpc));
                Assert.Null(MapSwitch.BlockedReason(r.Flash, r.Mpc));
            }
            Assert.Equal(MapSwitch.Trigger.DscButton, MapSwitch.InstalledTrigger(dsc.Mpc));
            Assert.Equal(MapSwitch.Trigger.Pedals, MapSwitch.InstalledTrigger(pedals.Mpc));
            Assert.Null(MapSwitch.InstalledTrigger(mpc));

            // Building the other trigger over a pair replaces its code.
            MapSwitch.Result changed = MapSwitch.Build(dsc.Flash, dsc.Mpc, null, null, MapSwitch.Trigger.Pedals);
            Assert.True(changed.WasUpdated);
            Assert.Equal(pedals.Mpc, changed.Mpc);
            Assert.Equal(pedals.Flash, changed.Flash);

            MapSwitch.Result back = MapSwitch.Build(changed.Flash, changed.Mpc, null, null, MapSwitch.Trigger.DscButton);
            Assert.True(back.WasUpdated);
            Assert.Equal(dsc.Mpc, back.Mpc);

            // The same trigger again leaves the code alone.
            MapSwitch.Result same = MapSwitch.Build(pedals.Flash, pedals.Mpc, null, null, MapSwitch.Trigger.Pedals);
            Assert.False(same.WasUpdated);
            Assert.Equal(pedals.Mpc, same.Mpc);
        }

        [Fact]
        public void TheFirstDscBuildIsRecognisedAsEarlierAndUpdated()
        {
            var (flash, mpc) = SyntheticPair();
            var first = new MapSwitch.Version(MapSwitch.Trigger.DscButton,
                MapSwitch.StartupIndication.ImmediateLong, MapSwitch.DscWatch.First);
            MapSwitch.Result old = MapSwitch.Build(flash, mpc, null, null, first);
            MapSwitch.Result fresh = MapSwitch.Build(flash, mpc, null, null);

            // It watched the last byte of the frame; the current build the first.
            uint[] tach = TachRoutine(old.Mpc);
            Assert.Contains(MapSwitch.Lbz(12, -0x3B3D, 13), tach);
            Assert.Contains(MapSwitch.AndiDot(12, 12, 0x0C), tach);
            Assert.NotEqual(old.Mpc, fresh.Mpc);

            Assert.True(MapSwitch.IsAlreadyPatched(old.Mpc));
            Assert.False(MapSwitch.IsCurrentVersion(old.Mpc));
            Assert.Equal(MapSwitch.Trigger.DscButton, MapSwitch.InstalledTrigger(old.Mpc));
            Assert.Null(MapSwitch.BlockedReason(old.Flash, old.Mpc));

            var area = new byte[MapSwitch.CarCheckLength];
            Array.Copy(old.Mpc, MapSwitch.CarCheckOffset, area, 0, area.Length);
            Assert.Equal(MapSwitch.CarState.Earlier, MapSwitch.StateOnCar(area));
            Assert.Equal(MapSwitch.Trigger.DscButton, MapSwitch.TriggerOnCar(area));

            MapSwitch.Result updated = MapSwitch.Build(old.Flash, old.Mpc, null, null);
            Assert.True(updated.WasUpdated);
            Assert.Equal(fresh.Mpc, updated.Mpc);
        }

        [Fact]
        public void TheTriggerOnTheCarIsToldFromTheFreeArea()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] Area(byte[] image)
            {
                var area = new byte[MapSwitch.CarCheckLength];
                Array.Copy(image, MapSwitch.CarCheckOffset, area, 0, area.Length);
                return area;
            }

            byte[] dsc = MapSwitch.Build(flash, mpc, null, null, MapSwitch.Trigger.DscButton).Mpc;
            byte[] pedals = MapSwitch.Build(flash, mpc, null, null, MapSwitch.Trigger.Pedals).Mpc;
            byte[] old = MapSwitch.Build(flash, mpc, null, null, MapSwitch.StartupIndication.Delayed).Mpc;

            Assert.Equal(MapSwitch.CarState.Current, MapSwitch.StateOnCar(Area(dsc)));
            Assert.Equal(MapSwitch.CarState.Current, MapSwitch.StateOnCar(Area(pedals)));
            Assert.Equal(MapSwitch.CarState.Earlier, MapSwitch.StateOnCar(Area(old)));
            Assert.Equal(MapSwitch.Trigger.DscButton, MapSwitch.TriggerOnCar(Area(dsc)));
            Assert.Equal(MapSwitch.Trigger.Pedals, MapSwitch.TriggerOnCar(Area(pedals)));
            Assert.Equal(MapSwitch.Trigger.Pedals, MapSwitch.TriggerOnCar(Area(old)));
            Assert.Null(MapSwitch.TriggerOnCar(Area(mpc)));
        }

        // Instructions of the tach routine: from its entry to the first
        // stored-data routine.
        private static uint[] TachRoutine(byte[] image)
        {
            uint entry = Get32(image, 0x4B6C4);
            Assert.Equal(0x48000001u, entry & 0xFC000003);      // bl
            uint start = 0x4B6C4 + (entry & 0x03FFFFFC), end = Get32(image, NvDescriptor);
            var words = new uint[(end - start) / 4];
            for (int i = 0; i < words.Length; i++)
                words[i] = Get32(image, (int)start + 4 * i);
            return words;
        }

        [Fact]
        public void TheCurrentVersionWatchesTheDscStateAndNotThePedals()
        {
            var (flash, mpc) = SyntheticPair();
            uint[] tach = TachRoutine(MapSwitch.Build(flash, mpc, null, null).Mpc);

            // The first byte of the last 0x153 frame, masked to the DSC bit,
            // is compared with the bit kept in the flag; the pedals are not read.
            Assert.Contains(MapSwitch.Lbz(12, -0x3B44, 13), tach);
            Assert.Contains(MapSwitch.AndiDot(12, 12, 0x02), tach);
            Assert.Contains(MapSwitch.Cmpw(12, 10), tach);
            Assert.Contains(MapSwitch.Cmplwi(12, 4), tach);
            Assert.DoesNotContain(MapSwitch.Lbz(12, -0x4001, 13), tach);
            Assert.DoesNotContain(MapSwitch.Lbz(12, -0x4061, 13), tach);

            // The window between presses is 2 s at 100 calls a second.
            Assert.Equal(4, MapSwitch.DscPresses);
            Assert.Equal(2, MapSwitch.DscPressWindowSeconds);
            Assert.Contains(MapSwitch.Li(10, 200), tach);
            Assert.DoesNotContain(MapSwitch.Cmplwi(12, 500), tach);
        }

        [Fact]
        public void ThePedalVersionsReadThePedalsAndNotTheDscState()
        {
            var (flash, mpc) = SyntheticPair();
            var images = new System.Collections.Generic.List<byte[]>
                { MapSwitch.Build(flash, mpc, null, null, MapSwitch.Trigger.Pedals).Mpc };
            foreach (var version in EarlierVersions)
                images.Add(MapSwitch.Build(flash, mpc, null, null, version).Mpc);

            foreach (byte[] image in images)
            {
                uint[] tach = TachRoutine(image);
                Assert.Contains(MapSwitch.Lbz(12, -0x4001, 13), tach);
                Assert.Contains(MapSwitch.Lbz(12, -0x4061, 13), tach);
                Assert.Contains(MapSwitch.Cmplwi(12, 500), tach);
                Assert.DoesNotContain(MapSwitch.Lbz(12, -0x3B44, 13), tach);
            }
        }

        // li r12,N / li r11,N in the power-up routines (init and restore).
        private static int PowerUpLoads(byte[] image, uint value)
        {
            int count = 0;
            uint init = Get32(image, NvDescriptor), save = Get32(image, NvDescriptor + 8);
            for (uint a = init; a < save; a += 4)
            {
                uint w = Get32(image, (int)a);
                if (w == (0x39800000 | value) || w == (0x39600000 | value))
                    count++;
            }
            return count;
        }

        [Fact]
        public void TheCurrentVersionShowsTheMapForThreeSecondsAtPowerUp()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] image = MapSwitch.Build(flash, mpc, null, null).Mpc;

            Assert.Equal(3, MapSwitch.StartupDisplaySeconds);

            // Both power-up routines load 300 into the display countdown,
            // which is 16 bits wide here: 300 does not fit in a byte.
            Assert.Equal(2, PowerUpLoads(image, 300));
            Assert.Equal(0, PowerUpLoads(image, 150));
            Assert.Equal(0, PowerUpLoads(image, 600));

            uint init = Get32(image, NvDescriptor), save = Get32(image, NvDescriptor + 8);
            int wideStores = 0, byteStores = 0;
            uint sth12 = MapSwitch.Sth(12, (int)MapSwitch.RamWideDisplayCounter - 0x4017F0, 13);
            uint sth11 = MapSwitch.Sth(11, (int)MapSwitch.RamWideDisplayCounter - 0x4017F0, 13);
            uint stb12 = MapSwitch.Stb(12, (int)MapSwitch.RamDisplayCounter - 0x4017F0, 13);
            uint stb11 = MapSwitch.Stb(11, (int)MapSwitch.RamDisplayCounter - 0x4017F0, 13);
            for (uint a = (uint)CodeStart; a < save; a += 4)
            {
                uint w = Get32(image, (int)a);
                if (w == sth12 || w == sth11) wideStores++;
                if (w == stb12 || w == stb11) byteStores++;
            }

            // Gesture, countdown and engine-running in the tach routine,
            // then the two power-up routines. The byte at the old display
            // counter's address now counts DSC presses: a press, the toggle,
            // the window running out and engine-running in the tach routine,
            // then the two power-up routines.
            Assert.Equal(5, wideStores);
            Assert.Equal(MapSwitch.RamDisplayCounter, MapSwitch.RamPressCounter);
            Assert.Equal(6, byteStores);
        }

        [Fact]
        public void TheShortImmediateVersionShowsTheMapAtPowerUp()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] image = MapSwitch.Build(flash, mpc, null, null, MapSwitch.StartupIndication.Immediate).Mpc;

            Assert.Equal(2, PowerUpLoads(image, 150));
            Assert.Equal(0, PowerUpLoads(image, 600));
        }

        [Fact]
        public void TheStateOnTheCarIsToldFromTheFreeArea()
        {
            var (flash, mpc) = SyntheticPair();

            byte[] Area(byte[] image)
            {
                var area = new byte[MapSwitch.CarCheckLength];
                Array.Copy(image, MapSwitch.CarCheckOffset, area, 0, area.Length);
                return area;
            }

            Assert.Equal(MapSwitch.CarState.NotInstalled, MapSwitch.StateOnCar(Area(mpc)));
            Assert.Equal(MapSwitch.CarState.Current,
                MapSwitch.StateOnCar(Area(MapSwitch.Build(flash, mpc, null, null).Mpc)));

            foreach (var version in EarlierVersions)
                Assert.Equal(MapSwitch.CarState.Earlier,
                    MapSwitch.StateOnCar(Area(MapSwitch.Build(flash, mpc, null, null, version).Mpc)));

            byte[] other = Area(MapSwitch.Build(flash, mpc, null, null).Mpc);
            other[0x20] ^= 0x01;
            Assert.Equal(MapSwitch.CarState.Unrecognised, MapSwitch.StateOnCar(other));

            Assert.Throws<ArgumentException>(() => MapSwitch.StateOnCar(new byte[12]));
        }

        [Fact]
        public void Map2ReadFromTheCarIsTheTuneThatWasStored()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] tune = SyntheticCalibration(0x66);
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, tune);

            var area = new byte[MapSwitch.Map2Length];
            Array.Copy(r.Flash, MapSwitch.Map2Start, area, 0, area.Length);
            Assert.Equal(tune, MapSwitch.Map2AsCalibration(area));

            // An empty area holds no tune.
            for (int i = 0; i < area.Length; i++) area[i] = 0xFF;
            Assert.Null(MapSwitch.Map2AsCalibration(area));

            Assert.Throws<ArgumentException>(() => MapSwitch.Map2AsCalibration(new byte[0x100]));
        }

        [Fact]
        public void TheDataVersionOfMap2IsReadFromItsField()
        {
            var (flash, mpc) = SyntheticPair();
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);

            var field = new byte[MapSwitch.DataVersionFieldLength];
            Array.Copy(r.Flash, MapSwitch.Map2DataVersionOffset, field, 0, field.Length);
            Assert.Equal("0044570LO00S", MapSwitch.DataVersionFrom(field));

            // An empty map 2 area reads as 0xFF.
            for (int i = 0; i < field.Length; i++) field[i] = 0xFF;
            Assert.Null(MapSwitch.DataVersionFrom(field));
        }

        [Fact]
        public void TheDelayedVersionWaitsSixSecondsBeforeShowingTheMap()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] image = MapSwitch.Build(flash, mpc, null, null, MapSwitch.StartupIndication.Delayed).Mpc;

            Assert.Equal(6, MapSwitch.StartupDelaySeconds);
            Assert.Equal(2, PowerUpLoads(image, 600));
            Assert.Equal(0, PowerUpLoads(image, 150));
        }

        [Fact]
        public void WrongProgramVersionIsRefused()
        {
            var (flash, mpc) = SyntheticPair();
            Encoding.ASCII.GetBytes("0044570LO00S").CopyTo(flash, 0x6031C);

            Assert.NotNull(MapSwitch.BlockedReason(flash, mpc));
            Assert.Throws<InvalidOperationException>(() => MapSwitch.Build(flash, mpc, null, null));
        }

        [Fact]
        public void AModifiedPatchSiteIsRefused()
        {
            var (flash, mpc) = SyntheticPair();
            Put32(mpc, 0xD4C0, 0x60000000);
            Assert.NotNull(MapSwitch.BlockedReason(flash, mpc));

            (flash, mpc) = SyntheticPair();
            Put32(mpc, 0x4B6C4, 0x60000000);
            Assert.NotNull(MapSwitch.BlockedReason(flash, mpc));

            (flash, mpc) = SyntheticPair();
            Put32(mpc, NvDescriptor + 4, 0xFFFC0000);
            Assert.NotNull(MapSwitch.BlockedReason(flash, mpc));
        }

        [Fact]
        public void OccupiedFreeAreasAreRefused()
        {
            var (flash, mpc) = SyntheticPair();
            mpc[0x6F000] = 0x00;
            Assert.NotNull(MapSwitch.BlockedReason(flash, mpc));

            (flash, mpc) = SyntheticPair();
            flash[0xE1234] = 0x00;
            Assert.NotNull(MapSwitch.BlockedReason(flash, mpc));
        }

        [Fact]
        public void AMap2ThatDoesNotFitIsRefused()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] two = SyntheticCalibration(0x55);
            two[MapSwitch.Map2Length + 4] = 0x00;

            Assert.Throws<InvalidOperationException>(() => MapSwitch.Build(flash, mpc, null, two));
        }

        [Fact]
        public void AMap2ForAnotherDataVersionIsRefused()
        {
            var (flash, mpc) = SyntheticPair();
            byte[] two = SyntheticCalibration(0x55);
            Encoding.ASCII.GetBytes("0044570LM00S").CopyTo(two, 0x10);

            Assert.Throws<InvalidOperationException>(() => MapSwitch.Build(flash, mpc, null, two));
        }

        [Fact]
        public void ExtractCalibrationAcceptsPartialsAndFullImages()
        {
            var (flash, _) = SyntheticPair();
            byte[] fromFull = MapSwitch.ExtractCalibration(flash);
            byte[] fromPartial = MapSwitch.ExtractCalibration(SyntheticCalibration(0x22));

            Assert.Equal(MapSwitch.CalibrationLength, fromFull.Length);
            Assert.Equal(fromPartial, fromFull);
            Assert.Throws<InvalidOperationException>(() => MapSwitch.ExtractCalibration(new byte[0x1000]));
        }

        // --- Fixture tests ------------------------------------------------

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(v) && File.Exists(v) ? v : null;
        }

        [SkippableFact]
        public void RealPairIsAcceptedAndChecksumsCorrectly()
        {
            string flashPath = Env("MS45_MAPSWITCH_FLASH"), mpcPath = Env("MS45_MAPSWITCH_MPC");
            Skip.If(flashPath == null || mpcPath == null,
                "Set MS45_MAPSWITCH_FLASH and MS45_MAPSWITCH_MPC to run this.");

            byte[] flash = File.ReadAllBytes(flashPath);
            byte[] mpc = File.ReadAllBytes(mpcPath);

            Assert.Null(MapSwitch.BlockedReason(flash, mpc));
            MapSwitch.Result r = MapSwitch.Build(flash, mpc, null, null);
            Assert.True(MapSwitch.IsAlreadyPatched(r.Mpc));

            // The writer's checksum must change (the image changed) and must be
            // stable when applied a second time.
            var cs = new Checksums_Signatures();
            uint before = Get32(flash, 0x60000);
            byte[] once = cs.CorrectProgramChecksums((byte[])r.Flash.Clone(), r.Mpc);
            byte[] twice = cs.CorrectProgramChecksums((byte[])once.Clone(), r.Mpc);

            Assert.NotEqual(before, Get32(once, 0x60000));
            Assert.Equal(Get32(once, 0x60000), Get32(once, 0x60340));
            Assert.Equal(once, twice);

            // The stock pair carries the sum its code gives, the build
            // moves it, and checksumming and signing leave it alone.
            Assert.Equal(MapSwitch.RomTestSum(flash, mpc), MapSwitch.ReadRomTestSum(flash));
            Assert.NotEqual(MapSwitch.ReadRomTestSum(flash), MapSwitch.ReadRomTestSum(r.Flash));
            byte[] signed = cs.SignMS45Program((byte[])once.Clone(), r.Mpc);
            Assert.Equal(MapSwitch.RomTestSum(signed, r.Mpc), MapSwitch.ReadRomTestSum(signed));
        }

        [SkippableFact]
        public void RealPairMatchesTheReferenceBuilder()
        {
            string flashPath = Env("MS45_MAPSWITCH_FLASH"), mpcPath = Env("MS45_MAPSWITCH_MPC");
            string refFlash = Env("MS45_MAPSWITCH_REF_FLASH"), refMpc = Env("MS45_MAPSWITCH_REF_MPC");
            Skip.If(flashPath == null || mpcPath == null || refFlash == null || refMpc == null,
                "Set the four MS45_MAPSWITCH_* variables to run this.");

            MapSwitch.Result r = MapSwitch.Build(
                File.ReadAllBytes(flashPath), File.ReadAllBytes(mpcPath), null, null);
            byte[] corrected = new Checksums_Signatures().CorrectProgramChecksums(r.Flash, r.Mpc);

            Assert.Equal(File.ReadAllBytes(refMpc), r.Mpc);
            Assert.Equal(File.ReadAllBytes(refFlash), corrected);
        }
    }
}
