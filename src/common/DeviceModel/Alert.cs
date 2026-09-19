namespace SmartHome.DeviceModel
{
    /// <summary>
    /// One raised alert: an id naming the condition, and a message describing it.
    /// </summary>
    /// <remarks>
    /// An alert says what is wrong, which is the whole reason it is not a lifecycle
    /// state. A single "something is wrong" flag leaves a device with a flat battery and
    /// a device with an unreadable sensor indistinguishable to a controller. However an
    /// adapter chooses to render the raised set -- one topic per id, a single aggregate,
    /// a diagnostic entity -- it needs the id to do it, so the model carries it.
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
        /// same character rule as every other id, since an adapter may well carry it as
        /// a path segment.
        /// </summary>
        public string Id { get; }

        /// <summary>The human-readable description, e.g. "Battery is low, at 8%".</summary>
        public string Message { get; }
    }
}
