using SmartHome.DeviceModel.Enums;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Home Assistant's device classes, what the model's <see cref="QuantityKind"/> maps
    /// onto, and the two tables Home Assistant validates that mapping against.
    /// </summary>
    /// <remarks>
    /// A device class is what makes Home Assistant treat a number as a temperature rather
    /// than as a number: it picks the icon, the graph, the unit conversion and the
    /// long-term statistics. It is also the one semantic the model carries mainly for
    /// this adapter -- the unit alone cannot say it, since <c>%</c> is humidity, battery
    /// charge and soil moisture alike.
    ///
    /// Both tables below were read from Home Assistant's own source at tag
    /// <c>2026.9.3</c> (<c>homeassistant/components/sensor/const.py</c>, cross-checked
    /// against <c>homeassistant/components/number/const.py</c>, whose sets are identical
    /// for every class named here) rather than recalled, because getting either wrong is
    /// silent: an entity that is rejected, or one kept out of long-term statistics, looks
    /// exactly like one that works until somebody goes looking for it.
    ///
    /// The unit strings are pinned by codepoint where they are not ASCII, for the reason
    /// <c>Units</c> in the model pins its own: visually identical characters exist, and
    /// a comparison against the wrong one refuses a unit Home Assistant accepts.
    /// </remarks>
    public static class DeviceClass
    {
        public const string Temperature = "temperature";
        public const string Humidity = "humidity";
        public const string Pressure = "pressure";
        public const string Battery = "battery";
        public const string Moisture = "moisture";
        public const string Power = "power";
        public const string Energy = "energy";
        public const string Voltage = "voltage";
        public const string Current = "current";
        public const string Volume = "volume";
        public const string Distance = "distance";
        public const string Illuminance = "illuminance";
        public const string Duration = "duration";

        /// <summary>
        /// A value from a declared set -- the device class of a read-only enum property.
        /// </summary>
        /// <remarks>
        /// Not produced by <see cref="FromQuantityKind"/>: it says what the *datatype*
        /// is, not what the number means, and Home Assistant forbids it a unit and a
        /// state class. See <c>DiscoveryMapper</c>.
        /// </remarks>
        public const string Enumeration = "enum";

        /// <summary>
        /// Something is wrong -- the device class of the diagnostic entity the alert set
        /// maps onto.
        /// </summary>
        public const string Problem = "problem";

        /// <summary>Home Assistant's state class for a value measured in the present.</summary>
        public const string MeasurementStateClass = "measurement";

        /// <summary>
        /// The device class a <see cref="QuantityKind"/> maps onto, or null when the kind
        /// is <see cref="QuantityKind.None"/>.
        /// </summary>
        /// <remarks>
        /// Every kind the model declares is named here. A kind added to the model later
        /// therefore falls through to null, and the caller refuses the property rather
        /// than publishing an entity with no device class -- a new semantic silently
        /// losing its meaning is exactly the failure this mapping exists to prevent.
        ///
        /// <see cref="QuantityKind.Pressure"/> maps to <c>pressure</c> rather than to
        /// <c>atmospheric_pressure</c>. Both accept the same units, so neither is
        /// refused; the model says what is measured and not at what scale, and reading a
        /// barometer into the narrower class would be this adapter inventing the
        /// distinction.
        /// </remarks>
        public static string? FromQuantityKind(QuantityKind kind) => kind switch
        {
            QuantityKind.None => null,
            QuantityKind.Temperature => Temperature,
            QuantityKind.Humidity => Humidity,
            QuantityKind.Pressure => Pressure,
            QuantityKind.Battery => Battery,
            QuantityKind.Moisture => Moisture,
            QuantityKind.Power => Power,
            QuantityKind.Energy => Energy,
            QuantityKind.Voltage => Voltage,
            QuantityKind.Current => Current,
            QuantityKind.Volume => Volume,
            QuantityKind.Distance => Distance,
            QuantityKind.Illuminance => Illuminance,
            QuantityKind.Duration => Duration,
            _ => null,
        };

        /// <summary>
        /// Whether Home Assistant accepts this unit for this device class.
        /// </summary>
        /// <remarks>
        /// This is a hard check on Home Assistant's side, not a hint: a discovery config
        /// whose unit is outside the class's set is refused whole
        /// (<c>validate_sensor_state_and_device_class_config</c> raises <c>vol.Invalid</c>),
        /// and the entity never appears. An empty unit fails here too -- Home Assistant
        /// does not refuse the config for it, but its sensor entity logs the pair as
        /// invalid on every start and shows a number with no unit, which is a
        /// mis-declaration the device can see at build time and the house cannot.
        /// </remarks>
        public static bool Accepts(string deviceClass, string unit)
        {
            var normalised = Normalise(unit);
            var units = UnitsFor(deviceClass);

            for (int i = 0; i < units.Length; i++)
            {
                if (units[i] == normalised)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The spelling Home Assistant will compare a unit under, which is not always the
        /// one it was written in.
        /// </summary>
        /// <remarks>
        /// Home Assistant rewrites a handful of units before it validates them
        /// (<c>AMBIGUOUS_UNITS</c>, applied by both the sensor and the number platform),
        /// and the pair that matters here is the two micro signs: the MICRO SIGN U+00B5
        /// that a keyboard produces, and the GREEK SMALL LETTER MU U+03BC that its unit
        /// table is written in. Comparing without this would refuse a unit Home Assistant
        /// accepts -- and refuse it at build time, where the device simply does not come
        /// up, which is a worse failure than the one the check exists to prevent.
        ///
        /// Only the entries whose result is in one of the sets above are here. Home
        /// Assistant's table is longer -- micrograms, microsiemens, reactive power -- but
        /// those spell units no device class this adapter produces accepts, so rewriting
        /// them would change nothing except how far this list has to be kept in step.
        ///
        /// Note what is deliberately NOT here: <c>µA</c> is not in Home Assistant's table
        /// either, so a current in the micro sign is refused by both. That asymmetry is
        /// Home Assistant's, and mirroring it is the point -- this method exists to
        /// compare the way Home Assistant compares, not to be more generous than it.
        ///
        /// The unit still goes on the wire exactly as the property declares it. Home
        /// Assistant applies the same rewrite when it receives the config, so publishing
        /// its spelling instead would only be this adapter editing a device's own
        /// statement about itself.
        /// </remarks>
        public static string Normalise(string unit) => unit switch
        {
            // U+00B5 V -> U+03BC V
            "µV" => "μV",
            // U+00B5 s -> U+03BC s
            "µs" => "μs",
            _ => unit,
        };

        /// <summary>
        /// The units Home Assistant accepts for a device class, in its own spelling.
        /// </summary>
        /// <remarks>
        /// Also the failure message's content: a refusal that names the units the class
        /// does accept is one a device author can act on without reading Home Assistant's
        /// source.
        /// </remarks>
        public static string[] UnitsFor(string deviceClass) => deviceClass switch
        {
            // Degree sign U+00B0.
            Temperature => new string[] { "°C", "°F", "K" },
            Humidity => new string[] { "%" },
            Battery => new string[] { "%" },
            Moisture => new string[] { "%" },
            // Subscript two U+2082 in inH2O.
            Pressure => new string[] { "mPa", "Pa", "hPa", "kPa", "bar", "cbar", "mbar", "mmHg", "inHg", "inH₂O", "psi" },
            // Note that BTU/h is NOT in Home Assistant's power set, though it is a unit
            // of power: the set is spelled out member by member there, not derived.
            Power => new string[] { "mW", "W", "kW", "MW", "GW", "TW" },
            Energy => new string[] { "J", "kJ", "MJ", "GJ", "mWh", "Wh", "kWh", "MWh", "GWh", "TWh", "cal", "kcal", "Mcal", "Gcal" },
            // Greek small letter mu U+03BC, not the micro sign U+00B5. Home Assistant
            // translates the micro sign to this one before validating, so both reach the
            // same entity, but only this spelling is in the set.
            Voltage => new string[] { "μV", "mV", "V", "kV", "MV" },
            Current => new string[] { "μA", "mA", "A" },
            // Superscript three U+00B3.
            Volume => new string[] { "ft³", "CCF", "MCF", "m³", "L", "mL", "gal", "fl. oz." },
            Distance => new string[] { "mm", "cm", "m", "km", "in", "ft", "yd", "mi", "nmi" },
            Illuminance => new string[] { "lx" },
            Duration => new string[] { "d", "h", "min", "s", "ms", "μs" },
            _ => new string[0],
        };

        /// <summary>
        /// Whether Home Assistant's <c>measurement</c> state class is valid for a device
        /// class.
        /// </summary>
        /// <remarks>
        /// A device class constrains its state class as well as its unit, in a separate
        /// table (<c>DEVICE_CLASS_STATE_CLASSES</c>), and the two are checked separately.
        /// Declaring a state class the class forbids does not fail the config -- it is
        /// reported per entity as impossible considering the device class, and the entity
        /// is kept out of long-term statistics, which is the one thing a state class is
        /// for.
        ///
        /// The classes that refuse it are the cumulative ones: Home Assistant reads an
        /// energy or a volume figure as a meter reading and allows only <c>total</c> and
        /// <c>total_increasing</c> there. Those two are the only ones this adapter can
        /// produce -- its own table is longer, and carries the same restriction for
        /// water, gas and reactive energy, none of which the model has a kind for.
        ///
        /// Which of <c>total</c> and <c>total_increasing</c> a cumulative value is cannot
        /// be inferred from anything the model carries -- a meter that never resets and
        /// one that does are the same declaration here -- so nothing is published rather
        /// than a guess.
        /// </remarks>
        public static bool AcceptsMeasurementStateClass(string? deviceClass)
            => deviceClass != Energy && deviceClass != Volume;
    }
}
