// Annotated for nullable reference types explicitly, rather than inheriting the
// project's setting. This file is compiled twice: by RoomSensor, which is
// null-oblivious, and by the unit test project, which enables nullable and links it
// (see Unit.nfproj for why it is linked rather than referenced). Without the
// directive the annotations below are a warning in one context and their absence is
// a warning in the other.
#nullable enable

using SmartHome.DeviceConfiguration;

namespace SmartHome.Devices.RoomSensor
{
    /// <summary>
    /// Everything about this device that describes where it is installed rather than what
    /// it does.
    /// </summary>
    /// <remarks>
    /// Read from <c>I:\configuration.json</c> at boot. The file is versioned as
    /// <c>config\room-sensor.json</c> and deployed with
    /// <c>scripts\Deploy-DeviceConfig.ps1</c>, so moving the sensor to another room or the
    /// broker to another address is a JSON edit and one command, not an edit, a rebuild
    /// and a reflash.
    ///
    /// The property names are the JSON keys, exactly: the parser matches case-sensitively
    /// on purpose, so the file and this class read as the same thing and there is one
    /// correct spelling rather than two that can drift.
    ///
    /// Everything here is required. There is no optional field and no default, because a
    /// default would be this class quietly inventing an installation -- and a device that
    /// looks configured and is not is the failure this whole mechanism exists to prevent.
    /// </remarks>
    public class RoomSensorConfiguration : IValidatableConfiguration
    {
        /// <summary>The id the device is addressed by, e.g. <c>room-sensor-office</c>.</summary>
        public string? DeviceId { get; set; }

        /// <summary>The device's human-readable name.</summary>
        public string? DeviceName { get; set; }

        /// <summary>The MQTT broker's host name or address.</summary>
        public string? BrokerHost { get; set; }

        /// <summary>
        /// How the BMP280 is wired and how often it is read.
        /// </summary>
        /// <remarks>
        /// A block rather than four more fields at the top level, and that shape carries
        /// meaning: it is the sensor node's installation data, and the node is announced
        /// only when this whole configuration loaded. It is also the one nested custom
        /// object in the tree, which is what makes this device the proving ground for the
        /// deeper maps a window or irrigation controller needs.
        /// </remarks>
        public SensorConfiguration? Sensor { get; set; }

        /// <inheritdoc />
        public string? Validate()
        {
            if (IsBlank(DeviceId))
            {
                return "DeviceId is missing or empty.";
            }

            if (IsBlank(DeviceName))
            {
                return "DeviceName is missing or empty.";
            }

            if (IsBlank(BrokerHost))
            {
                return "BrokerHost is missing or empty.";
            }

            if (Sensor == null)
            {
                return "The Sensor block is missing.";
            }

            return Sensor.Validate();
        }

        /// <remarks>
        /// Null and empty answer the same way here, and they arrive by different routes:
        /// an absent key leaves the property null, and a key present with <c>""</c> leaves
        /// it empty. Neither is an installation.
        /// </remarks>
        internal static bool IsBlank(string? value) => value == null || value.Trim().Length == 0;
    }

    /// <summary>
    /// Where the BMP280 is wired and how often it is read.
    /// </summary>
    public class SensorConfiguration
    {
        /// <summary>The I2C bus the sensor is on.</summary>
        public int I2cBusId { get; set; }

        /// <summary>The GPIO pin carrying I2C data (SDA).</summary>
        public int DataPin { get; set; }

        /// <summary>The GPIO pin carrying the I2C clock (SCL).</summary>
        public int ClockPin { get; set; }

        /// <summary>How long to wait between readings.</summary>
        public int MeasurementIntervalMs { get; set; }

        /// <summary>
        /// The one bus this app can wire up. See <see cref="Validate"/>.
        /// </summary>
        public const int SupportedI2cBusId = 1;

        /// <remarks>
        /// Every bound here rejects zero, and that is the point rather than tidiness: a
        /// JSON key that is absent or misspelt leaves its field at zero, so "zero is not a
        /// legal value" is what turns a typo into an alert instead of into a device
        /// reading pin 0 as fast as it can.
        /// </remarks>
        public string? Validate()
        {
            // Not "a bus id the runtime has", which is 1 or 2: the one this app can
            // actually wire up. Program.SetupSensor assigns DeviceFunction.I2C1_DATA and
            // I2C1_CLOCK, so a configuration naming bus 2 would put the pins on bus 1 and
            // then open bus 2 -- two halves of two different buses, failing at the first
            // read with nothing to say why. Refused here instead, where the message names
            // the field.
            //
            // Supporting bus 2 is a pin-function map away (I2C2_DATA / I2C2_CLOCK exist),
            // and is deliberately not done: nothing in this repository has ever driven
            // bus 2 on this board, and advertising support measured nowhere is the same
            // mistake as a compiled-in default that looks plausible.
            if (I2cBusId != SupportedI2cBusId)
            {
                return $"Sensor.I2cBusId is {I2cBusId}; this device wires up I2C bus {SupportedI2cBusId} only.";
            }

            // Zero rejected, not merely negatives. An absent or misspelt key leaves a pin
            // at 0, and GPIO0 is a real pin -- so 0 would sail through a "not negative"
            // check and be configured, which is exactly the plausible-looking wrong
            // installation this whole mechanism exists to refuse. GPIO0 is also the
            // ESP32's boot-strapping pin (held low at reset it enters download mode), so
            // it is a bad choice for SDA or SCL on purpose as well as by accident.
            if (DataPin <= 0)
            {
                return $"Sensor.DataPin is {DataPin}; it must be a GPIO number greater than zero.";
            }

            if (ClockPin <= 0)
            {
                return $"Sensor.ClockPin is {ClockPin}; it must be a GPIO number greater than zero.";
            }

            if (DataPin == ClockPin)
            {
                return $"Sensor.DataPin and Sensor.ClockPin are both {DataPin}; one bus needs two pins.";
            }

            if (MeasurementIntervalMs <= 0)
            {
                return $"Sensor.MeasurementIntervalMs is {MeasurementIntervalMs}; it must be greater than zero.";
            }

            return null;
        }
    }
}
