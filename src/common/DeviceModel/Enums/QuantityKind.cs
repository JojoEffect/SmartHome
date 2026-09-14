namespace SmartHome.DeviceModel.Enums
{
    /// <summary>
    /// What a property's number *means*, independently of the unit it is expressed in.
    /// </summary>
    /// <remarks>
    /// The unit alone does not say. <c>%</c> is humidity, battery charge and soil
    /// moisture; each is a different thing to a consumer that wants to draw an icon,
    /// pick a colour, or decide what "low" means. Conventions differ on whether they
    /// carry the distinction at all: an adapter for one that does not simply ignores
    /// this, while one that has a semantic category of its own -- sometimes required,
    /// and sometimes cross-validated against the unit -- maps this onto it.
    ///
    /// Kept deliberately small and physical. It answers "what is measured", not "how
    /// should it be displayed" -- the latter differs per consumer and does not belong in
    /// a device's own description.
    /// </remarks>
    public enum QuantityKind
    {
        /// <summary>Not stated. The default, and always a valid answer.</summary>
        None = 0,

        Temperature = 1,
        Humidity = 2,
        Pressure = 3,
        Battery = 4,
        Moisture = 5,
        Power = 6,
        Energy = 7,
        Voltage = 8,
        Current = 9,
        Volume = 10,
        Distance = 11,
        Illuminance = 12,
        Duration = 13,
    }
}
