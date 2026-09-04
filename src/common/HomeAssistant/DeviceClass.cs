using SmartHome.Homie.V4.Enums;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Home Assistant sensor device classes, and what a Homie <c>$unit</c> implies about
    /// one.
    /// </summary>
    /// <remarks>
    /// A device class is what makes Home Assistant treat a number as a temperature rather
    /// than as a number: it picks the icon, the graph, the unit conversion and the
    /// long-term statistics. It is also validated -- Home Assistant checks the reported
    /// <c>unit_of_measurement</c> against a per-class set (<c>DEVICE_CLASS_UNITS</c> in
    /// <c>homeassistant/components/sensor/const.py</c>), and rejects the entity when the
    /// two disagree. So every inference below was checked against that table rather than
    /// assumed, and the Homie unit strings in <c>UnitExtensions.GetString</c> happen to be
    /// exactly the spellings Home Assistant's own unit enums use.
    /// </remarks>
    public static class DeviceClass
    {
        public const string Temperature = "temperature";
        public const string Humidity = "humidity";
        public const string AtmosphericPressure = "atmospheric_pressure";
        public const string Pressure = "pressure";
        public const string Voltage = "voltage";
        public const string Current = "current";
        public const string Power = "power";
        public const string Volume = "volume";
        public const string Distance = "distance";
        public const string Battery = "battery";
        public const string Moisture = "moisture";

        /// <summary>
        /// The device class a Homie <c>$unit</c> implies, or <c>null</c> when it implies
        /// none.
        /// </summary>
        /// <remarks>
        /// <c>null</c> is a real answer here, not a gap to fill in later. An entity with
        /// no device class still shows its value and its unit; an entity with the *wrong*
        /// one is either rejected outright (unit not in the class's set) or quietly
        /// mis-drawn and mis-converted. So this maps only what a unit determines on its
        /// own.
        ///
        /// <see cref="Unit.Percent"/> is the case that makes the point: '%' is the unit of
        /// humidity, of battery charge and of soil moisture alike, and Home Assistant has
        /// a distinct device class for each. Nothing in a Homie declaration says which,
        /// so this returns null and the app names it --
        /// <c>HomeAssistantAnnouncer.SetDeviceClass</c>. RoomSensor's humidity property is
        /// exactly that call.
        ///
        /// <see cref="Unit.Pascal"/> maps to <c>atmospheric_pressure</c> rather than
        /// <c>pressure</c> because Pa at room scale is a barometer reading; bar and psi
        /// map to <c>pressure</c> because at those scales it is a vessel or a line. Both
        /// classes accept all three units, so a wrong guess here is cosmetic rather than
        /// rejected -- and either can be overridden.
        /// </remarks>
        public static string? FromUnit(Unit unit) => unit switch
        {
            Unit.DegreeCelsius => Temperature,
            Unit.DegreeFahrenheit => Temperature,
            Unit.Pascal => AtmosphericPressure,
            Unit.Bar => Pressure,
            Unit.PSI => Pressure,
            Unit.Volts => Voltage,
            Unit.Ampere => Current,
            Unit.Watt => Power,
            Unit.Liter => Volume,
            Unit.Galon => Volume,
            Unit.Meter => Distance,
            Unit.Feet => Distance,
            // Percent: ambiguous, see above. Degree, CountOrAmount and None have no Home
            // Assistant device class at all.
            _ => null,
        };
    }
}
