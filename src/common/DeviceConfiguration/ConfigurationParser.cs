using nanoFramework.Json;
using System;

namespace SmartHome.DeviceConfiguration
{
    /// <summary>
    /// Turns configuration text into a device's configuration object, or into the reason
    /// it could not.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="ConfigurationStore"/> so that everything except the file
    /// read is provable without a device: this half runs on the virtual CLR in CI, which
    /// has no storage to read from. The same split, for the same reason, as
    /// <c>Read-SmartHomeDeploymentGeometry</c> on the host side.
    ///
    /// Nothing here throws. A configuration that cannot be read is an ordinary thing for
    /// a device to find at boot -- the file was never deployed, or it was edited by hand
    /// at a tank in the rain -- and the device's answer to it is to come up and say so,
    /// not to crash and reboot into the same state.
    /// </remarks>
    public static class ConfigurationParser
    {
        /// <remarks>
        /// <c>ThrowExceptionWhenPropertyNotFound</c> is the point of having options at
        /// all. Silence about a member that did not match is how <c>"borkerHost"</c>
        /// becomes a device with no broker address and no complaint, and a misspelt key
        /// is the single most likely mistake in a file whose whole purpose is to be
        /// hand-edited. <see cref="IValidatableConfiguration.Validate"/> catches the same
        /// class of mistake from the other side; both, because a configuration this
        /// repository cannot see is worse than one it rejects twice.
        ///
        /// Case-sensitive on purpose: the file is generated from, and reviewed against,
        /// the class, so there is exactly one correct spelling and accepting a second one
        /// only makes the two drift.
        /// </remarks>
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            ThrowExceptionWhenPropertyNotFound = true,
        };

        /// <summary>
        /// Parses <paramref name="json"/> into <paramref name="configurationType"/> and
        /// validates it.
        /// </summary>
        /// <param name="json">The configuration text.</param>
        /// <param name="configurationType">
        /// The device's own configuration class. Implementing
        /// <see cref="IValidatableConfiguration"/> is how it gets to reject what merely
        /// parsed.
        /// </param>
        /// <param name="source">
        /// What to call the text in a failure message -- a file path, normally. A person
        /// reading the alert needs to know which file to open.
        /// </param>
        public static ConfigurationResult Parse(string? json, Type? configurationType, string? source)
        {
            var origin = source == null || source.Length == 0 ? "The configuration" : $"The configuration in '{source}'";

            if (configurationType == null)
            {
                return ConfigurationResult.Failed($"{origin} was not read: no configuration type was given.");
            }

            if (json == null || json.Trim().Length == 0)
            {
                return ConfigurationResult.Failed($"{origin} is empty.");
            }

            object? parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject(json, configurationType, Options);
            }
            catch (Exception ex)
            {
                // The exception type is deliberately in the message. A
                // DeserializationException and a NullReferenceException out of the same
                // call mean different things about the file, and the person holding the
                // alert has no debugger.
                return ConfigurationResult.Failed($"{origin} could not be parsed ({ex.GetType().Name}: {ex.Message}).");
            }

            if (parsed == null)
            {
                return ConfigurationResult.Failed($"{origin} parsed to nothing.");
            }

            // Not required: a configuration with no rule worth stating is a legitimate
            // thing to have, and forcing every device to write an empty Validate() would
            // make the interface noise rather than a contract.
            if (parsed is IValidatableConfiguration validatable)
            {
                var reason = validatable.Validate();
                if (reason != null && reason.Length > 0)
                {
                    return ConfigurationResult.Failed($"{origin} is not usable: {reason}");
                }
            }

            return ConfigurationResult.Loaded(parsed);
        }
    }
}
