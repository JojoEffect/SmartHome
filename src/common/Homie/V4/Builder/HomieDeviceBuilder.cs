using SmartHome.Homie.V4.Attributes;
using System;

namespace SmartHome.Homie.V4.Builder
{
    public class HomieDeviceBuilder
    {
        private readonly string _topicId;
        private readonly string _name;
        private readonly string[] _extensions;
        private Node[] _nodes = new Node[0];
        private string? _implemnentation;

        public HomieDeviceBuilder(string topicId, string name, string[]? extensions = null)
        {
            // Validate here as well as in the entity itself: the builder defers
            // constructing the Device until BuildDevice(), and a bad id should be
            // rejected where it was written, not several calls later.
            NamedHomieEntityBase.ValidateTopicId(topicId);

            _topicId = topicId;
            _name = name;
            _extensions = extensions is null ? new string[0] : extensions;
        }

        public Device BuildDevice()
        {
            var device = new Device(_topicId, _name, _extensions);
            device.AddNodes(_nodes);

            if (!string.IsNullOrEmpty(_implemnentation))
            {
                device.ImplementationAttribute = new ImplementationAttribute(device, _implemnentation);
            }

            return device;
        }

        public HomieDeviceBuilder WithImplementation(string implementation)
        {
            _implemnentation = implementation;
            return this;
        }

        public HomieNodeBuilder AddNode(string topicId, string name, string type)
        {
            NamedHomieEntityBase.ValidateTopicId(topicId);

            return new HomieNodeBuilder(this, topicId, name, type);
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
