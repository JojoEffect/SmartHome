using nanoFramework.TestFramework;
using SmartHome.Devices.RainwaterCistern;
using System;

namespace SmartHome.UnitTests
{
    /// <summary>
    /// The conversion chain from burden-resistor voltage to litres in the cistern.
    /// </summary>
    /// <remarks>
    /// Worth testing off the device precisely because it cannot be checked on one: the
    /// probe reads whatever the water level happens to be, and there is no way to tell
    /// a wrong scale factor from a wrong water level by looking at the published value.
    /// </remarks>
    [TestClass]
    public class LevelCalculationTests
    {
        // The installation RainwaterCistern's Calibration currently describes. Repeated
        // here rather than referenced: Calibration is the device's configuration and is
        // expected to change when the tank does, and these numbers are fixtures that
        // must not move with it.
        private const double ProbeRangeMeters = 5.0;
        private const double ShuntOhms = 150.0;
        private const double FullDepthMeters = 2.0;

        private const double Tolerance = 1e-9;

        [TestMethod]
        public void VoltsToMilliAmps_Converts_The_Ends_Of_The_Loop_Span()
        {
            // 150 ohm was picked so that 4-20 mA lands on 0.600-3.000 V.
            AssertClose(4.0, LevelCalculation.VoltsToMilliAmps(0.6, ShuntOhms), "0.6 V over 150 ohm is 4 mA");
            AssertClose(20.0, LevelCalculation.VoltsToMilliAmps(3.0, ShuntOhms), "3.0 V over 150 ohm is 20 mA");
            AssertClose(0.0, LevelCalculation.VoltsToMilliAmps(0.0, ShuntOhms), "no voltage is no current");
        }

        [TestMethod]
        public void VoltsToMilliAmps_Rejects_A_Non_Positive_Shunt()
        {
            Assert.ThrowsException(typeof(ArgumentException), () => LevelCalculation.VoltsToMilliAmps(1.0, 0.0));
            Assert.ThrowsException(typeof(ArgumentException), () => LevelCalculation.VoltsToMilliAmps(1.0, -150.0));
        }

        [TestMethod]
        public void MilliAmpsToDepthMeters_Maps_The_Loop_Span_Onto_The_Probe_Range()
        {
            AssertClose(0.0, LevelCalculation.MilliAmpsToDepthMeters(4.0, ProbeRangeMeters), "4 mA is an empty probe");
            AssertClose(2.5, LevelCalculation.MilliAmpsToDepthMeters(12.0, ProbeRangeMeters), "12 mA is mid-scale");
            AssertClose(5.0, LevelCalculation.MilliAmpsToDepthMeters(20.0, ProbeRangeMeters), "20 mA is full scale");

            // The worked example from the commissioning procedure: submerge the probe
            // 1.00 m and a 5 m probe answers 7.2 mA. If this one moves, the instructions
            // for determining an unknown probe's range are wrong too.
            AssertClose(1.0, LevelCalculation.MilliAmpsToDepthMeters(7.2, ProbeRangeMeters), "7.2 mA is 1 m on a 5 m probe");
        }

        [TestMethod]
        public void MilliAmpsToDepthMeters_Clamps_Below_Zero_But_Not_Above_Range()
        {
            // Between the fault threshold and 4 mA is ordinary transmitter error on an
            // empty tank, and a negative depth would be a worse answer than zero.
            AssertClose(0.0, LevelCalculation.MilliAmpsToDepthMeters(3.9, ProbeRangeMeters), "just under 4 mA reads empty");

            // Over-range is left alone on purpose: a probe reading past its span is real
            // information, and IsLoopFault already says where it stops being plausible.
            AssertClose(5.15625, LevelCalculation.MilliAmpsToDepthMeters(20.5, ProbeRangeMeters), "over-range is reported, not clamped");
        }

        [TestMethod]
        public void MilliAmpsToDepthMeters_Rejects_A_Non_Positive_Range()
        {
            Assert.ThrowsException(typeof(ArgumentException), () => LevelCalculation.MilliAmpsToDepthMeters(12.0, 0.0));
            Assert.ThrowsException(typeof(ArgumentException), () => LevelCalculation.MilliAmpsToDepthMeters(12.0, -5.0));
        }

        [TestMethod]
        public void IsLoopFault_Separates_A_Broken_Loop_From_An_Extreme_Level()
        {
            Assert.IsTrue(LevelCalculation.IsLoopFault(0.0), "no current at all is a dead loop");
            Assert.IsTrue(LevelCalculation.IsLoopFault(3.4), "under 3.5 mA is a fault");
            Assert.IsTrue(LevelCalculation.IsLoopFault(21.0), "over 20.8 mA is a fault");

            Assert.IsFalse(LevelCalculation.IsLoopFault(3.9), "3.9 mA is an empty tank, not a fault");
            Assert.IsFalse(LevelCalculation.IsLoopFault(4.0), "4 mA is the healthy bottom of the span");
            Assert.IsFalse(LevelCalculation.IsLoopFault(12.0), "mid-scale is healthy");
            Assert.IsFalse(LevelCalculation.IsLoopFault(20.0), "20 mA is the healthy top of the span");
        }

        [TestMethod]
        public void DepthToPercent_Is_Relative_To_The_Usable_Column_And_Clamped()
        {
            AssertClose(0.0, LevelCalculation.DepthToPercent(0.0, FullDepthMeters), "empty is 0%");
            AssertClose(50.0, LevelCalculation.DepthToPercent(1.0, FullDepthMeters), "half the column is 50%");
            AssertClose(100.0, LevelCalculation.DepthToPercent(2.0, FullDepthMeters), "the full column is 100%");

            // Clamped at both ends, unlike the depth: a gauge showing 129% is noise.
            AssertClose(100.0, LevelCalculation.DepthToPercent(2.575, FullDepthMeters), "over-full still reads 100%");
            AssertClose(0.0, LevelCalculation.DepthToPercent(-0.5, FullDepthMeters), "a negative depth still reads 0%");
        }

        [TestMethod]
        public void DepthToPercent_Rejects_A_Non_Positive_Full_Depth()
        {
            Assert.ThrowsException(typeof(ArgumentException), () => LevelCalculation.DepthToPercent(1.0, 0.0));
        }

        [TestMethod]
        public void DepthToLitres_Fills_An_Upright_Cylinder()
        {
            // A 2 m cylinder holds pi * 1 m^2 per metre of depth, which is 3141.59 litres.
            AssertClose(3141.59265358979, LevelCalculation.DepthToLitres(1.0, 2.0), "one metre in a 2 m cylinder");
            AssertClose(6283.18530717958, LevelCalculation.DepthToLitres(2.0, 2.0), "volume is linear in depth");
            AssertClose(0.0, LevelCalculation.DepthToLitres(0.0, 2.0), "an empty tank holds nothing");
            AssertClose(0.0, LevelCalculation.DepthToLitres(-1.0, 2.0), "a negative depth holds nothing");
        }

        [TestMethod]
        public void DepthToLitres_Rejects_A_Non_Positive_Diameter()
        {
            Assert.ThrowsException(typeof(ArgumentException), () => LevelCalculation.DepthToLitres(1.0, 0.0));
        }

        [TestMethod]
        public void The_Whole_Chain_Turns_A_Shunt_Voltage_Into_A_Level()
        {
            // 1.8 V over 150 ohm is 12 mA, which is mid-scale on a 5 m probe: 2.5 m of
            // water, over a 2 m usable column, so the percentage clamps at full.
            var milliAmps = LevelCalculation.VoltsToMilliAmps(1.8, ShuntOhms);
            Assert.IsFalse(LevelCalculation.IsLoopFault(milliAmps), "12 mA is a healthy loop");

            var depth = LevelCalculation.MilliAmpsToDepthMeters(milliAmps, ProbeRangeMeters);

            AssertClose(12.0, milliAmps, "1.8 V over 150 ohm is 12 mA");
            AssertClose(2.5, depth, "12 mA is 2.5 m on a 5 m probe");
            AssertClose(100.0, LevelCalculation.DepthToPercent(depth, FullDepthMeters), "2.5 m over a 2 m column is full");
        }

        /// <summary>
        /// Compares two doubles within a tolerance.
        /// </summary>
        /// <remarks>
        /// nanoFramework.TestFramework 3.0.80 has no AreEqual overload taking a delta,
        /// and exact equality is the wrong assertion for a chain that divides by 16 and
        /// multiplies by pi. Math.Abs is avoided so the test assembly does not need to
        /// carry nanoFramework.System.Math for one subtraction.
        /// </remarks>
        private static void AssertClose(double expected, double actual, string because)
        {
            var difference = expected - actual;
            var within = difference <= Tolerance && difference >= -Tolerance;

            Assert.IsTrue(within, $"{because}: expected {expected}, was {actual}.");
        }
    }
}
