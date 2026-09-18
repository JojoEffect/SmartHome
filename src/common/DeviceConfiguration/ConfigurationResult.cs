using SmartHome.Protocol;

namespace SmartHome.DeviceConfiguration
{
    /// <summary>
    /// The outcome of reading a device's installation data: either the configuration
    /// object, or the reason there isn't one.
    /// </summary>
    /// <remarks>
    /// There is no third answer and no partial one. A configuration that parsed but is
    /// missing a value the device needs is a failure here, not an object with a zero in
    /// it -- a wrong-but-plausible installation is the failure this whole mechanism
    /// exists to make impossible, and a default-valued field is exactly that.
    ///
    /// <see cref="ReportTo"/> is the contract every device honours identically: when the
    /// configuration could not be read, the device says so on the wire under one id, and
    /// announces nothing that would have been derived from the data it does not have.
    /// Written once here rather than in each device app, because five hand-written copies
    /// of a rule drift -- which is the observable history of the five compiled-in broker
    /// addresses this replaces.
    /// </remarks>
    public sealed class ConfigurationResult
    {
        /// <summary>
        /// The id every device raises the configuration failure under.
        /// </summary>
        /// <remarks>
        /// One id across the fleet, so a controller watching several devices sees the
        /// same condition spelled the same way. It follows the model's id rule (lower
        /// case, digits and hyphens) because an adapter is free to carry an alert id as a
        /// path segment.
        /// </remarks>
        public const string AlertId = "configuration";

        private ConfigurationResult(object? value, string? failureReason)
        {
            Value = value;
            FailureReason = failureReason;
        }

        /// <summary>Whether <see cref="Value"/> holds a usable configuration.</summary>
        public bool IsValid => FailureReason == null;

        /// <summary>
        /// Why there is no configuration, or <see langword="null"/> when there is one.
        /// </summary>
        /// <remarks>
        /// Written for whoever has to fix it rather than for a log grep: it names the
        /// file, or the field, or the text that would not parse. It is published as the
        /// alert message, so for a convention with room for the detail this is what a
        /// controller shows.
        /// </remarks>
        public string? FailureReason { get; }

        /// <summary>
        /// The configuration, or <see langword="null"/> when the load failed. Cast it to
        /// the type that was asked for.
        /// </summary>
        public object? Value { get; }

        /// <summary>A successful load.</summary>
        public static ConfigurationResult Loaded(object value) => new ConfigurationResult(value, null);

        /// <summary>A failed load, with the reason a person would need to fix it.</summary>
        public static ConfigurationResult Failed(string? reason)
            => new ConfigurationResult(null, reason == null || reason.Length == 0 ? "The configuration could not be read." : reason);

        /// <summary>
        /// Raises the shared alert when the load failed, and does nothing when it did not.
        /// </summary>
        /// <remarks>
        /// Call this <em>before</em> connecting. An alert raised beforehand is recorded on
        /// the device and is therefore already part of what the announcement says, so a
        /// misconfigured device never advertises a healthy state it is not in, not even
        /// for the one publish it would take to correct itself. Raising it afterwards
        /// would work too, and would be visibly wrong for that one publish.
        ///
        /// Nothing is cleared on the success path. A device reads its configuration once,
        /// at boot, with nothing raised yet; clearing an alert that was never raised is a
        /// no-op that reads as though re-reading were a thing this supports.
        /// </remarks>
        public void ReportTo(IDeviceProtocol protocol)
        {
            if (IsValid)
            {
                return;
            }

            protocol.RaiseAlert(AlertId, FailureReason!);
        }
    }
}
