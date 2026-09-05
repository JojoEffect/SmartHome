namespace SmartHome.DeviceModel
{
    /// <summary>
    /// One raised alert: an id naming the condition, and a message describing it.
    /// </summary>
    /// <remarks>
    /// An alert says what is wrong, which is the whole reason it is not a lifecycle
    /// state. Homie v4's <c>$state = alert</c> can only say that *something* is, and a
    /// device with a flat battery and a device with an unreadable sensor were
    /// indistinguishable to a controller. Homie v5 publishes one topic per alert id
    /// carrying the message; Home Assistant renders the raised set as a diagnostic
    /// entity. Both need the id, so the model carries it.
    /// </remarks>
    public class Alert
    {
        internal Alert(string id, string message)
        {
            Id = id;
            Message = message;
        }

        /// <summary>
        /// What is wrong, as an id: <c>battery</c>, <c>sensor-unreadable</c>. Held to the
        /// same character rule as every other id, because Homie v5 puts it in a topic.
        /// </summary>
        public string Id { get; }

        /// <summary>The human-readable description, e.g. "Battery is low, at 8%".</summary>
        public string Message { get; }
    }
}
