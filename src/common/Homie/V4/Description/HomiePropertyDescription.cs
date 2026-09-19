using SmartHome.DeviceModel.Properties;

namespace SmartHome.Homie.V4.Description
{
    /// <summary>
    /// What one model property looks like on the v4 wire: its topics, and the six
    /// attributes that describe it.
    /// </summary>
    internal sealed class HomiePropertyDescription
    {
        internal HomiePropertyDescription(PropertyBase property, string topic, string commandTopic, HomieAttributeMessage[] attributes)
        {
            Property = property;
            Topic = topic;
            CommandTopic = commandTopic;
            Attributes = attributes;
        }

        /// <summary>The property itself, which owns the value and its encoding.</summary>
        internal PropertyBase Property { get; }

        /// <summary>Where the value goes.</summary>
        internal string Topic { get; }

        /// <summary>
        /// Where a controller writes, whether or not the property is settable. A
        /// non-settable property is simply never subscribed on it.
        /// </summary>
        internal string CommandTopic { get; }

        /// <summary>
        /// <c>$name</c>, <c>$datatype</c>, <c>$format</c>, <c>$settable</c>,
        /// <c>$retained</c> and <c>$unit</c>, in announce order.
        /// </summary>
        /// <remarks>
        /// All six, always, even when the payload is empty: v4 lists them as the
        /// property's attributes, and the conformance run reads an empty <c>$format</c>
        /// or <c>$unit</c> off the live stream.
        /// </remarks>
        internal HomieAttributeMessage[] Attributes { get; }
    }
}
