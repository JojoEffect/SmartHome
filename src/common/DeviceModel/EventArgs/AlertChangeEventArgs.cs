namespace SmartHome.DeviceModel.EventArgs
{
    /// <summary>
    /// An alert was raised, re-raised with a different message, or cleared.
    /// </summary>
    /// <remarks>
    /// Raised only when the set actually changed, so an adapter can publish straight from
    /// this without first comparing against what it published last time.
    /// </remarks>
    public class AlertChangeEventArgs : System.EventArgs
    {
        internal AlertChangeEventArgs(Device device, string alertId, string? message, bool isRaised)
        {
            Device = device;
            AlertId = alertId;
            Message = message;
            IsRaised = isRaised;
        }

        public Device Device { get; }

        /// <summary>Which alert changed.</summary>
        public string AlertId { get; }

        /// <summary>
        /// The alert's message, or null when it was cleared -- there is no message for an
        /// alert that is no longer raised.
        /// </summary>
        public string? Message { get; }

        /// <summary>True when the alert is now raised, false when it has been cleared.</summary>
        public bool IsRaised { get; }
    }
}
