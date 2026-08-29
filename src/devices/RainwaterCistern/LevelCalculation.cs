using System;

namespace SmartHome.Devices.RainwaterCistern
{
    /// <summary>
    /// Turns a 4-20 mA current-loop reading into a water depth, a percentage and a
    /// volume.
    /// </summary>
    /// <remarks>
    /// Deliberately pure: no I2C, no logging, no configuration. Everything the maths
    /// needs arrives as an argument, so the whole conversion chain from shunt voltage
    /// to litres is testable off the device. This file is linked into
    /// SmartHome.UnitTests rather than referenced, the same way Bmp280Check links
    /// IntegrationTest.cs -- a device application is an executable and pulling one into
    /// the test assembly would put the whole app on the device to test arithmetic.
    /// </remarks>
    public static class LevelCalculation
    {
        /// <summary>Loop current representing the bottom of the probe's range.</summary>
        public const double ZeroCurrentMilliAmps = 4.0;

        /// <summary>Loop current representing the top of the probe's range.</summary>
        public const double FullScaleCurrentMilliAmps = 20.0;

        /// <summary>
        /// Below this the loop is faulted rather than merely reading empty.
        /// </summary>
        /// <remarks>
        /// A live transmitter never falls under 4 mA by more than its own error, so
        /// 3.5 mA separates "empty cistern" from "broken wire, dead transmitter or
        /// missing loop supply" -- the case where the honest answer is no reading at
        /// all. Values between here and 4 mA are ordinary tolerance and clamp to zero.
        /// </remarks>
        public const double UnderRangeFaultMilliAmps = 3.5;

        /// <summary>
        /// Above this the loop is faulted: the probe is over-ranged or shorted.
        /// </summary>
        public const double OverRangeFaultMilliAmps = 20.8;

        private const double Pi = 3.14159265358979;

        private const double CubicMetresToLitres = 1000.0;

        /// <summary>
        /// Converts a burden-resistor voltage into the loop current that produced it.
        /// </summary>
        public static double VoltsToMilliAmps(double volts, double shuntOhms)
        {
            if (shuntOhms <= 0.0)
            {
                throw new ArgumentException($"The shunt resistance must be positive, was {shuntOhms}.");
            }

            return volts / shuntOhms * 1000.0;
        }

        /// <summary>
        /// True when the loop current says the measurement chain is broken rather than
        /// reporting an extreme level.
        /// </summary>
        public static bool IsLoopFault(double milliAmps) =>
            milliAmps < UnderRangeFaultMilliAmps || milliAmps > OverRangeFaultMilliAmps;

        /// <summary>
        /// Converts a loop current into the water column standing over the probe, in
        /// metres.
        /// </summary>
        /// <remarks>
        /// Clamped at zero on the low side: between <see cref="UnderRangeFaultMilliAmps"/>
        /// and 4 mA the reading is a healthy empty tank plus transmitter error, and a
        /// negative depth would be a worse answer than zero. Not clamped on the high
        /// side -- a value over the probe's range is real information, and
        /// <see cref="IsLoopFault"/> already draws the line where it stops being
        /// plausible.
        /// </remarks>
        public static double MilliAmpsToDepthMeters(double milliAmps, double probeRangeMeters)
        {
            if (probeRangeMeters <= 0.0)
            {
                throw new ArgumentException($"The probe range must be positive, was {probeRangeMeters}.");
            }

            var span = FullScaleCurrentMilliAmps - ZeroCurrentMilliAmps;
            var depth = (milliAmps - ZeroCurrentMilliAmps) / span * probeRangeMeters;

            return depth < 0.0 ? 0.0 : depth;
        }

        /// <summary>
        /// Expresses a water depth as a percentage of the cistern's usable column,
        /// clamped to 0-100.
        /// </summary>
        /// <remarks>
        /// Clamped at both ends here, unlike the depth: a percentage is what a
        /// controller draws a gauge from, and 104% of a full tank is noise rather than
        /// news. The unclamped depth is published alongside it for anyone who needs the
        /// raw truth.
        /// </remarks>
        public static double DepthToPercent(double depthMeters, double fullDepthMeters)
        {
            if (fullDepthMeters <= 0.0)
            {
                throw new ArgumentException($"The full depth must be positive, was {fullDepthMeters}.");
            }

            var percent = depthMeters / fullDepthMeters * 100.0;

            if (percent < 0.0)
            {
                return 0.0;
            }

            return percent > 100.0 ? 100.0 : percent;
        }

        /// <summary>
        /// Volume standing in an upright cylindrical cistern at a given depth, in litres.
        /// </summary>
        public static double DepthToLitres(double depthMeters, double innerDiameterMeters)
        {
            if (innerDiameterMeters <= 0.0)
            {
                throw new ArgumentException($"The cistern diameter must be positive, was {innerDiameterMeters}.");
            }

            if (depthMeters <= 0.0)
            {
                return 0.0;
            }

            var radius = innerDiameterMeters / 2.0;

            return Pi * radius * radius * depthMeters * CubicMetresToLitres;
        }
    }
}
