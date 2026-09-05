namespace SmartHome.DeviceModel.Enums
{
    /// <summary>
    /// What kind of value a property holds.
    /// </summary>
    /// <remarks>
    /// All nine Homie v5 datatypes, which is a superset of Homie v4's six. The model
    /// carries the whole set so that a device can be described once and published by
    /// whichever adapter is compiled in; an adapter whose convention cannot express one
    /// of them -- Homie v4 has no <see cref="DateTime"/>, <see cref="Duration"/> or
    /// <see cref="Json"/> -- is expected to refuse such a property loudly when the
    /// device is built, rather than publish something the convention does not define.
    ///
    /// Deliberately no <c>GetString()</c> here. The tokens a datatype goes out as
    /// ("integer", "float", ...) are a property of the convention, not of the model, and
    /// Home Assistant's MQTT Discovery publishes no datatype at all -- it infers a
    /// component from the datatype and the settable flag. Naming is the adapter's job.
    /// </remarks>
    public enum DataType
    {
        Integer = 0,
        Float = 1,
        Boolean = 2,
        String = 3,
        Enum = 4,
        Color = 5,

        /// <summary>An instant in time. Homie v5 only.</summary>
        DateTime = 6,

        /// <summary>An elapsed time. Homie v5 only.</summary>
        Duration = 7,

        /// <summary>A JSON array or object. Homie v5 only.</summary>
        Json = 8,
    }
}
