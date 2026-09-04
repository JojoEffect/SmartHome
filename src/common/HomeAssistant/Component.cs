namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// The Home Assistant MQTT components a Homie property can map onto.
    /// </summary>
    /// <remarks>
    /// Strings rather than an enum: this is the <c>&lt;component&gt;</c> level of the
    /// discovery topic verbatim, and every use of it is a topic concatenation. An enum
    /// would only add a conversion whose sole job is to give the string back.
    /// </remarks>
    public static class Component
    {
        public const string Sensor = "sensor";
        public const string BinarySensor = "binary_sensor";
        public const string Switch = "switch";
        public const string Number = "number";
        public const string Select = "select";
        public const string Text = "text";
    }
}
