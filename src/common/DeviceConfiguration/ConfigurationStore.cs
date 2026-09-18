using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;
using System.IO;

namespace SmartHome.DeviceConfiguration
{
    /// <summary>
    /// Reads a device's installation data from a file on the device's own internal
    /// storage.
    /// </summary>
    /// <remarks>
    /// The point of the exercise: which room a sensor is in, which pin a valve is on, how
    /// deep a tank is, where the broker lives. None of that describes what the firmware
    /// does, so none of it should need a rebuild and a reflash to change -- and the ones
    /// that hurt most are the ones only measurable in place, where the edit-build-flash
    /// loop has to be walked to the hardware and back.
    ///
    /// The file is written by the host with <c>nanoff --filedeployment</c>, which needs no
    /// rebuild and no firmware update, and it is versioned in the repository rather than
    /// living only on the device: a map that exists in exactly one place has no history,
    /// no review, and no way back after a mass erase except walking the floor again.
    ///
    /// The default path is on the internal drive. On ESP32 that is the littlefs partition
    /// the firmware already carries and mounts -- the same storage the network
    /// configuration lives in, not a partition anything has to be re-flashed to create.
    /// </remarks>
    public class ConfigurationStore
    {
        /// <summary>
        /// Where a device's configuration lives unless it says otherwise: the internal
        /// drive's root.
        /// </summary>
        /// <remarks>
        /// One name across the fleet, because the deployment step is the same command for
        /// every device and the destination is the least interesting thing about it. A
        /// device that genuinely needs a second file passes its own path.
        /// </remarks>
        public const string DefaultPath = "I:\\configuration.json";

        private readonly ILogger _logger;

        public ConfigurationStore(string path = DefaultPath)
        {
            Path = path;
            _logger = LogDispatcher.GetLogger("SmartHome.DeviceConfiguration.ConfigurationStore");
        }

        /// <summary>The file this store reads.</summary>
        public string Path { get; }

        /// <summary>
        /// Reads the file and turns it into <paramref name="configurationType"/>, or into
        /// the reason it could not.
        /// </summary>
        /// <remarks>
        /// Never throws, and that is the contract rather than caution. Missing, locked,
        /// truncated, not JSON, JSON of the wrong shape: every one of those is a thing a
        /// hand-edited file on a device in a cupboard is routinely going to be, and the
        /// device's answer to all of them is the same -- come up, say so under one alert
        /// id, and announce nothing that would have come from the data it does not have.
        /// A throw here would instead reboot the device into exactly the same file.
        ///
        /// Reading the whole file into a string rather than streaming it into the parser:
        /// a configuration file is a few hundred bytes, the string is what makes the
        /// parsing half testable without storage, and a read that fails should fail as a
        /// read rather than halfway through a parse.
        /// </remarks>
        public ConfigurationResult Load(Type configurationType)
        {
            string json;

            try
            {
                if (!File.Exists(Path))
                {
                    return ConfigurationResult.Failed(
                        $"No configuration file at '{Path}'. Deploy it with scripts\\Deploy-DeviceConfig.ps1.");
                }

                json = File.ReadAllText(Path);
            }
            catch (Exception ex)
            {
                return ConfigurationResult.Failed(
                    $"The configuration file '{Path}' could not be read ({ex.GetType().Name}: {ex.Message}).");
            }

            var result = ConfigurationParser.Parse(json, configurationType, Path);

            if (result.IsValid)
            {
                _logger.LogInformation($"Read the device configuration from '{Path}'.");
            }
            else
            {
                // Logged here as well as published as the alert message. The wire carries
                // it for whoever is watching the broker; the log carries it for whoever
                // has the device on a cable, and the two say the same thing on purpose.
                _logger.LogError(result.FailureReason);
            }

            return result;
        }
    }
}
