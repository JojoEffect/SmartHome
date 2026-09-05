namespace SmartHome.DeviceModel
{
    /// <summary>
    /// Well-known unit strings.
    /// </summary>
    /// <remarks>
    /// A unit is a <c>string</c> in this model, and this class only spells the common
    /// ones so that two properties measuring the same thing cannot disagree about how to
    /// write it. Nothing here is a closed set: a property may carry any unit string, and
    /// these are simply the ones every Homie consumer is expected to recognise.
    ///
    /// That openness is the point. The enum this replaces could not express <c>kWh</c>,
    /// <c>Hz</c>, <c>m³</c> or <c>rpm</c>, all of which the convention lists, and a
    /// closed enum makes adding one a change to a shared library rather than to the
    /// device that needs it.
    ///
    /// The values below are the recommended list from the Homie v5 convention
    /// (homieiot/convention, <c>convention.md</c>, "Units"), which is a superset of
    /// Homie v4's. The convention pins the non-ASCII ones by codepoint because
    /// visually identical characters exist -- degree is U+00B0, the cubed sign is
    /// U+00B3, and mired's characters are U+207B and U+00B9 -- so these constants must
    /// stay byte-for-byte what is written here.
    /// </remarks>
    public static class Units
    {
        /// <summary>No unit. The default, and a valid answer for a count or a state.</summary>
        public const string None = "";

        /// <summary>Degree Celsius. The degree sign is U+00B0.</summary>
        public const string DegreeCelsius = "°C";

        /// <summary>Degree Fahrenheit. The degree sign is U+00B0.</summary>
        public const string DegreeFahrenheit = "°F";

        /// <summary>Degree, of angle. U+00B0.</summary>
        public const string Degree = "°";

        /// <summary>Kelvin.</summary>
        public const string Kelvin = "K";

        /// <summary>Litre.</summary>
        public const string Liter = "L";

        /// <summary>Gallon.</summary>
        public const string Gallon = "gal";

        /// <summary>Cubic metre. The cubed sign is U+00B3.</summary>
        public const string CubicMeter = "m³";

        /// <summary>Volt.</summary>
        public const string Volts = "V";

        /// <summary>Watt.</summary>
        public const string Watt = "W";

        /// <summary>Kilowatt.</summary>
        public const string Kilowatt = "kW";

        /// <summary>Kilowatt-hour.</summary>
        public const string KilowattHour = "kWh";

        /// <summary>Ampere.</summary>
        public const string Ampere = "A";

        /// <summary>Hertz.</summary>
        public const string Hertz = "Hz";

        /// <summary>Revolutions per minute.</summary>
        public const string RevolutionsPerMinute = "rpm";

        /// <summary>Percent.</summary>
        public const string Percent = "%";

        /// <summary>Parts per million.</summary>
        public const string PartsPerMillion = "ppm";

        /// <summary>Metre.</summary>
        public const string Meter = "m";

        /// <summary>Foot.</summary>
        public const string Feet = "ft";

        /// <summary>Metres per second.</summary>
        public const string MetersPerSecond = "m/s";

        /// <summary>Knot.</summary>
        public const string Knots = "kn";

        /// <summary>Pascal.</summary>
        public const string Pascal = "Pa";

        /// <summary>Pounds per square inch.</summary>
        public const string Psi = "psi";

        /// <summary>Second.</summary>
        public const string Second = "s";

        /// <summary>Minute.</summary>
        public const string Minute = "min";

        /// <summary>Hour.</summary>
        public const string Hour = "h";

        /// <summary>Lux.</summary>
        public const string Lux = "lx";

        /// <summary>Mired. The characters are U+207B and U+00B9.</summary>
        public const string Mired = "MK⁻¹";

        /// <summary>Count or amount.</summary>
        public const string CountOrAmount = "#";

        /// <summary>
        /// Bar, of pressure. Not in the convention's recommended list, and kept because
        /// the closed enum this replaces carried it and some implementations use it.
        /// </summary>
        public const string Bar = "bar";
    }
}
