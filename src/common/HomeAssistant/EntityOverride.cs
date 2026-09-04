namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// What an app knows about one of its properties that the Homie declaration cannot say.
    /// </summary>
    /// <remarks>
    /// There are exactly two such things so far, and both are cases where a correct Homie
    /// declaration is still ambiguous to Home Assistant. A <c>%</c> unit could be
    /// humidity, battery charge or soil moisture (see <see cref="DeviceClass.FromUnit"/>);
    /// and a property that is meaningful to a controller may be noise on a dashboard.
    /// Anything an app could instead say by declaring the property properly belongs in the
    /// Homie model, not here.
    /// </remarks>
    public sealed class EntityOverride
    {
        /// <summary>
        /// Home Assistant device class to declare, overriding whatever the unit implies.
        /// </summary>
        public string? DeviceClass { get; set; }

        /// <summary>When true, no discovery message is published for this property.</summary>
        public bool Excluded { get; set; }
    }
}
