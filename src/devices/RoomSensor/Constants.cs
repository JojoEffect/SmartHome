namespace SmartHome.Devices.RoomSensor
{
    internal class Constants
    {
        // The device's own id and name are installation data and live in
        // config\room-sensor.json now (RoomSensorConfiguration). The two values below are
        // the fallback used only when that file could not be read at all, so that the
        // device still has an address to raise the configuration alert from -- a device
        // with no id cannot announce, and a device that cannot announce cannot say what
        // is wrong with it.
        //
        // Deliberately the same id and name the device normally carries, rather than
        // something recognisably a fallback. A distinct id would leave the real device's
        // retained tree sitting in the broker looking healthy while a second, phantom
        // device alerted somewhere else; alerting under the real id is what a controller
        // needs to see. The alert, not the identity, is what says the configuration is
        // missing.
        public const string FallbackDeviceTopicId = "room-sensor-office";
        public const string FallbackDeviceName = "Raumsensor Buero";

        public const string DeviceType = "Sensor";

        // The sensor node's shape is what the firmware is, not where it is installed: a
        // BMP280 reports temperature, humidity and pressure wherever it is screwed to the
        // wall. Only the wiring and the interval are configuration.
        public const string NodeSensorTopicId = "sensor";
        public const string NodeSensorName = "Sensor";
        public const string NodeSensorType = "BMP280";

        public const string PropertyTemperatureTopicId = "temperature";
        public const string PropertyTemperatureName = "Temperatur";

        public const string PropertyHumidityTopicId = "humidity";
        public const string PropertyHumidityName = "Luftfeuchte";

        public const string PropertyPressureTopicId = "pressure";
        public const string PropertyPressureName = "Luftdruck";
    }
}
