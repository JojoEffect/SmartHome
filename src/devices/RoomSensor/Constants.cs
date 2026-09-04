namespace SmartHome.Devices.RoomSensor
{
    internal class Constants
    {
        public const string DeviceTopicId = "room-sensor-office";
        public const string DeviceName = "Raumsensor Buero";
        public const string DeviceType = "Sensor";

        // Shown on the Home Assistant device page. Homie has no attribute for it -- the
        // closest, $type, is a *node* attribute and describes the sensor, not the board.
        public const string DeviceModel = "ESP32 + BMP280";
        
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
