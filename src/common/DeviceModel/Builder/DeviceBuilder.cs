using System;

namespace SmartHome.DeviceModel.Builder
{
    /// <summary>
    /// Builds a device description, one node and property at a time.
    /// </summary>
    /// <remarks>
    /// Neutral by design: nothing here names a convention, so a device written against
    /// this builder can be published as Homie v4, Homie v5 or Home Assistant MQTT
    /// Discovery by swapping the adapter it is handed to, and a device that will only
    /// ever speak to Home Assistant never types the word Homie.
    /// </remarks>
    public class DeviceBuilder
    {
        private readonly string _id;
        private readonly string _name;
        private Node[] _nodes = new Node[0];

        public DeviceBuilder(string id, string name)
        {
            // Validate here as well as in the entity itself: the builder defers
            // constructing the Device until BuildDevice(), and a bad id should be
            // rejected where it was written, not several calls later.
            NamedEntityBase.ValidateId(id);

            _id = id;
            _name = name;
        }

        public Device BuildDevice()
        {
            var device = new Device(_id, _name);
            device.AddNodes(_nodes);

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
