namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// The Home Assistant components a property of this model can map onto.
    /// </summary>
    /// <remarks>
    /// Strings rather than an enum: this is the <c>&lt;component&gt;</c> level of the
    /// discovery topic verbatim, and every use of it is a topic concatenation. An enum
    /// would only add a conversion whose sole job is to give the string back.
    ///
    /// Home Assistant publishes no datatype attribute of its own -- it infers what an
    /// entity *is* from which component the config was published under, plus the keys in
    /// it. So the choice here is the counterpart of another convention's
    /// <c>$datatype</c>, and it is decided by the model's datatype together with whether
    /// the property is settable: the same datatype becomes a read-only entity or a
    /// control, and Home Assistant models those as different components rather than as
    /// one component with a flag. Getting it wrong is not cosmetic -- a <c>sensor</c>
    /// given a <c>command_topic</c> ignores it, so the control simply never appears.
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
