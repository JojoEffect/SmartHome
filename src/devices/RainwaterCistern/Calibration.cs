namespace SmartHome.Devices.RainwaterCistern
{
    /// <summary>
    /// The numbers that describe the physical installation rather than the code: the
    /// probe, the burden resistor and the cistern itself.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Constants"/> on purpose. Those are Homie topic ids and
    /// names, and they change when the device is renamed; these change when somebody
    /// swaps a resistor or digs a different tank, and they are the only values that
    /// need touching then.
    ///
    /// NONE OF THESE HAS BEEN MEASURED. The device is not commissioned, so every value
    /// below is an assumption and the readings it produces are meaningless -- depths are
    /// scaled by an unknown constant. Issue #37 carries the measuring procedure and the
    /// definition of done; do not treat a published level as real until it is closed.
    /// </remarks>
    internal static class Calibration
    {
        /// <summary>
        /// Water column the probe spans between 4 mA and 20 mA, in metres.
        /// </summary>
        /// <remarks>
        /// NOT read off a datasheet. The probe carries only the codes EU536ZZJ730 and
        /// 02J335, which return nothing in any public catalogue, so 5 m is the common
        /// cistern range assumed here and it has to be confirmed before the readings
        /// mean anything. Measure it instead of guessing: feed the loop from ~24 V with
        /// a milliammeter in series, note the current with the probe in air (expect
        /// 4.00 mA), submerge it to a known depth, note the current again, then
        /// range = depth / ((I - 4 mA) / 16 mA).
        /// </remarks>
        public const double ProbeRangeMeters = 5.0;

        /// <summary>
        /// Burden resistor turning the 4-20 mA loop into a voltage, in ohms.
        /// </summary>
        /// <remarks>
        /// 150 ohm puts the measuring span at 0.600-3.000 V, which sits inside the
        /// ADS1115's +/-4.096 V range with room left for an over-range current. Use a
        /// 0.1% part with a low temperature coefficient: its tolerance lands directly
        /// on the published level, undivided.
        /// </remarks>
        public const double ShuntOhms = 150.0;

        /// <summary>ADS1115 programmable-gain full-scale range, in volts.</summary>
        public const double AdcFullScaleVolts = 4.096;

        /// <summary>Single-ended ADC input the shunt is wired to.</summary>
        public const byte AdcChannel = 0;

        /// <summary>
        /// Water depth over the probe when the cistern counts as full, in metres.
        /// </summary>
        /// <remarks>
        /// This is the reference for the published percentage, and it is not the same
        /// as <see cref="ProbeRangeMeters"/>: the probe hangs a few centimetres clear of
        /// the silt on the floor, and the tank overflows well below the top of its dome.
        /// Measure the real usable column rather than the tank's nominal height.
        /// </remarks>
        public const double FullDepthMeters = 2.0;

        /// <summary>
        /// Inner diameter of the cistern, in metres, assumed upright and cylindrical.
        /// </summary>
        /// <remarks>
        /// Only the published volume depends on this. A tank that is spherical or lying
        /// on its side needs a different function in
        /// <see cref="LevelCalculation"/> -- the depth and percentage stay correct
        /// either way.
        /// </remarks>
        public const double CisternInnerDiameterMeters = 2.0;
    }
}
