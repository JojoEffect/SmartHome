using System;

namespace SmartHome.DeviceModel.Builder
{
    /// <summary>
    /// Builds a device description, one node and property at a time.
    /// </summary>
    /// <remarks>
    /// Neutral by design: nothing here names a convention, so a device written against
    /// this builder is published by whichever adapter it is handed to, and swapping that
    /// adapter is the whole of what it takes to speak a different one. A device author
    /// never names a convention either.
    /// </remarks>
    public class DeviceBuilder
    {
        private readonly string _id;
        private readonly string _name;
        private Node[] _nodes = new Node[0];
        private bool _built;

        public DeviceBuilder(string id, string name)
        {
            // Validate here as well as in the entity itself: the builder defers
            // constructing the Device until BuildDevice(), and a bad id should be
            // rejected where it was written, not several calls later.
            NamedEntityBase.ValidateId(id);

            _id = id;
            _name = name;
        }

        /// <summary>
        /// Builds the device. A builder builds once; calling this again throws.
        /// </summary>
        /// <remarks>
        /// The nodes collected here are handed to the device rather than copied into it,
        /// and <see cref="Device.AddNodes"/> re-parents each one. A second call would
        /// therefore attach the *same* node instances to a second device and silently
        /// repoint the first device's nodes at it. That matters more here than it would
        /// have in the model this replaces: there is deliberately no <c>GetTopic()</c>
        /// any more, so an adapter names every entity by walking
        /// <see cref="EntityBase.Parent"/>, and the first device's whole tree would go
        /// out under the second device's id. Refused rather than deep-copied: a builder
        /// is a description of one device, and nothing needs to build two.
        /// </remarks>
        /// <exception cref="InvalidOperationException">This builder has already built.</exception>
        public Device BuildDevice()
        {
            if (_built)
            {
                throw new InvalidOperationException($"The device '{_id}' has already been built; a builder builds once.");
            }

            var device = new Device(_id, _name);
            device.AddNodes(_nodes);

            _built = true;

            return device;
        }

        public NodeBuilder AddNode(string id, string name, string type)
        {
            NamedEntityBase.ValidateId(id);

            return new NodeBuilder(this, id, name, type);
        }

        /// <summary>
        /// Adds the specified node to the end of the internal node collection.
        /// </summary>
        /// <param name="node">The node to add to the collection. Cannot be null.</param>
        internal void PushNode(Node node)
        {
            // Create and set a new array with the new node
            Node[] newNodes = new Node[_nodes.Length + 1];
            Array.Copy(_nodes, newNodes, _nodes.Length);
            newNodes[_nodes.Length] = node;
            _nodes = newNodes;
        }
    }
}
