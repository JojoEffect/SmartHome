namespace SmartHome.DeviceModel.Enums
{
    /// <summary>
    /// What kind of value a property holds.
    /// </summary>
    /// <remarks>
    /// Deliberately the union rather than an intersection: the model carries every kind
    /// of value a device might hold, so that a device can be described once and
    /// published by whichever adapter is compiled in. Conventions differ in what they
    /// can express, and an adapter whose convention cannot express one of these is
    /// expected to refuse such a property loudly when the device is built, rather than
    /// publish something that convention does not define.
    ///
    /// Deliberately no <c>GetString()</c> here. The token a datatype goes out as, and
    /// whether it goes out at all, are properties of the convention rather than of the
    /// model -- some publish a datatype attribute, others infer a component from the
    /// datatype and the settable flag and publish no datatype of their own. Naming is
    /// the adapter's job.
    /// </remarks>
    public enum DataType
    {
        Integer = 0,
        Float = 1,
        Boolean = 2,
        String = 3,
        Enum = 4,
        Color = 5,

        /// <summary>An instant in time. Not every convention carries this.</summary>
        DateTime = 6,

        /// <summary>An elapsed time. Not every convention carries this.</summary>
        Duration = 7,

        /// <summary>A JSON array or object. Not every convention carries this.</summary>
        Json = 8,
    }
}
