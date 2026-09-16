namespace SmartHome.Homie.V4.Description
{
    /// <summary>
    /// What one model node looks like on the v4 wire: its three attributes and its
    /// properties, in the order they are announced.
    /// </summary>
    internal sealed class HomieNodeDescription
    {
        internal HomieNodeDescription(string id, HomieAttributeMessage[] attributes, HomiePropertyDescription[] properties)
        {
            Id = id;
            Attributes = attributes;
            Properties = properties;
        }

        /// <summary>The node's own id, for log lines.</summary>
        internal string Id { get; }

        /// <summary><c>$name</c>, <c>$type</c> and <c>$properties</c>, in announce order.</summary>
        internal HomieAttributeMessage[] Attributes { get; }

        /// <summary>The node's properties, in declaration order -- the order <c>$properties</c> lists them in.</summary>
        internal HomiePropertyDescription[] Properties { get; }
    }
}
