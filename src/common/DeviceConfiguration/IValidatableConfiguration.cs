namespace SmartHome.DeviceConfiguration
{
    /// <summary>
    /// Implemented by a device's own configuration class to say whether what was read is
    /// actually usable.
    /// </summary>
    /// <remarks>
    /// The parser can only tell that the text was JSON and that its members matched the
    /// type. Whether a pin number is a pin, whether an interval is positive, whether the
    /// block describing a piece of hardware is present at all -- only the device knows,
    /// and only the device can say which field is at fault.
    ///
    /// This matters more than it looks: a JSON member that is absent leaves its field at
    /// the type's default, so a mistyped key produces a pin of 0 and an interval of 0
    /// rather than an error. Without this, that device would come up looking healthy and
    /// behaving wrongly, which is the failure mode the whole mechanism is here to
    /// prevent.
    /// </remarks>
    public interface IValidatableConfiguration
    {
        /// <summary>
        /// Returns why the configuration cannot be used, or <see langword="null"/> when
        /// it can.
        /// </summary>
        /// <remarks>
        /// The text is published as the alert message and read by whoever has to correct
        /// the file, so it names the offending field rather than saying that something is
        /// wrong. One reason is enough: a person fixing a configuration file is looking at
        /// the whole of it anyway.
        /// </remarks>
        string? Validate();
    }
}
