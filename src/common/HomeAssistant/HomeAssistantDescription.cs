using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Properties;
using System.Collections;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// A device rendered for Home Assistant: its discovery configurations, the topic
    /// every value goes to, and the topic every command arrives on.
    /// </summary>
    /// <remarks>
    /// Built once, by <see cref="HomeAssistantClient"/>'s constructor, and that is the
    /// whole point of the type. Everything here is fixed the moment the device is built
    /// -- nodes and properties cannot be added afterwards -- while an announce re-runs on
    /// every reconnect and on every Home Assistant restart. Rendering it per announce
    /// would redo the same string and JSON work each time, on the device.
    ///
    /// It is also where a device is refused, because <see cref="DiscoveryMapper.Map"/>
    /// runs here: a property Home Assistant cannot carry throws while the device is being
    /// built, which is the last moment the failure belongs to a developer rather than to
    /// a house.
    /// </remarks>
    internal sealed class HomeAssistantDescription
    {
        /// <exception cref="System.ArgumentException">
        /// A property cannot be published to Home Assistant. The message names it by its
        /// state topic.
        /// </exception>
        internal HomeAssistantDescription(Device device, HomeAssistantSettings settings)
        {
            DeviceId = device.Id;
            AvailabilityTopic = HomeAssistantTopics.Availability(device);
            AlertStateTopic = HomeAssistantTopics.Problem(device);
            AlertAttributesTopic = HomeAssistantTopics.Alerts(device);
            Configs = DiscoveryMapper.Map(device, settings);

            var described = new ArrayList();
            var settable = new ArrayList();

            // Declaration order, the same order the configurations were mapped in, so
            // that what a broker log shows is the order the device's author wrote.
            var nodes = device.Nodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                var properties = nodes[i].Properties;
                for (int j = 0; j < properties.Length; j++)
                {
                    var property = properties[j];
                    var describedProperty = new DescribedProperty(
                        property,
                        HomeAssistantTopics.Of(property),
                        property.Settable ? HomeAssistantTopics.Command(property) : null);

                    described.Add(describedProperty);

                    if (property.Settable)
                    {
                        settable.Add(describedProperty);
                    }
                }
            }

            Properties = (DescribedProperty[])described.ToArray(typeof(DescribedProperty));
            SettableProperties = (DescribedProperty[])settable.ToArray(typeof(DescribedProperty));
        }

        /// <summary>The device's own id, for log lines.</summary>
        internal string DeviceId { get; }

        /// <summary>
        /// Where <c>online</c> and <c>offline</c> go, which is also the last will's topic.
        /// </summary>
        internal string AvailabilityTopic { get; }

        /// <summary>Where the diagnostic entity reads whether anything is wrong.</summary>
        internal string AlertStateTopic { get; }

        /// <summary>Where that entity reads the raised alerts' ids and messages.</summary>
        internal string AlertAttributesTopic { get; }

        /// <summary>
        /// One retained discovery configuration per property, in declaration order, plus
        /// the device's own diagnostic entity last.
        /// </summary>
        internal DiscoveryEntity[] Configs { get; }

        /// <summary>Every property, with the topic its value goes to.</summary>
        internal DescribedProperty[] Properties { get; }

        /// <summary>
        /// The settable properties, in the order the tree was walked -- which is the
        /// insertion order of the subscription table, and therefore the order of the
        /// topics in the SUBSCRIBE packet.
        /// </summary>
        internal DescribedProperty[] SettableProperties { get; }

        /// <summary>
        /// The state topic of a property in this tree, or null if it is not in it.
        /// </summary>
        /// <remarks>
        /// A linear scan over an array fixed at construction, called from the property's
        /// own update handler. A device has a handful of properties, the scan allocates
        /// nothing, and reference identity is the only key available: ids are unique
        /// within a node, not within a device.
        /// </remarks>
        internal string? TopicOf(PropertyBase property)
        {
            for (int i = 0; i < Properties.Length; i++)
            {
                if (Properties[i].Property == property)
                {
                    return Properties[i].StateTopic;
                }
            }

            return null;
        }

        /// <summary>One property and the two topics this adapter gives it.</summary>
        internal sealed class DescribedProperty
        {
            internal DescribedProperty(PropertyBase property, string stateTopic, string? commandTopic)
            {
                Property = property;
                StateTopic = stateTopic;
                CommandTopic = commandTopic;
            }

            internal PropertyBase Property { get; }

            /// <summary>Where this property's value is published.</summary>
            internal string StateTopic { get; }

            /// <summary>Where a controller writes to it, or null when it is not settable.</summary>
            internal string? CommandTopic { get; }
        }
    }
}
