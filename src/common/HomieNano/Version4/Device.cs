using HomieNano.Version4.Attributes;
using HomieNano.Version4.Enums;
using HomieNano.Version4.Properties;
using System;
using System.Collections;

namespace HomieNano.Version4
{
    public class Device : NamedHomieEntityBase
    {
        private readonly object _lock = new();

        private readonly HomieAttribute _homieAttribute;
        private readonly NodesAttribute _nodesAttribute;
        private readonly StateAttribute _stateAttribute;
        private readonly ExtensionsAttribute _extensionsAttribute;
        private readonly IDictionary _nodes = new Hashtable();

        public Device(string topicId, string name, string[] extensions, string? implementation = null)
            : base(topicId, name)
        {
            _homieAttribute = new(this, Constants.Version4);
            _nodesAttribute = new(this, new string[0]);
            _stateAttribute = new(this, State.Init);
            _extensionsAttribute = new(this, extensions);
            ImplementationAttribute = implementation == null ? null : new(this, implementation);
        }

        public HomieAttribute HomieAttribute => _homieAttribute;

        public NodesAttribute NodesAttribute => _nodesAttribute;

        public StateAttribute StateAttribute => _stateAttribute;

        public ExtensionsAttribute ExtensionsAttribute => _extensionsAttribute;

        public ImplementationAttribute? ImplementationAttribute { get; internal set; }

        public Node[] Nodes
        {
            get
            {
                lock (_lock) 
                {
                    Node[] nodes = new Node[_nodes.Count];
                    _nodes.Values.CopyTo(nodes, 0);
                    return nodes;
                }
            }
        }

        public void AddNode(Node node) => AddNodes(new Node[] { node });

        public void AddNodes(Node[] nodes)
        {
            lock (_lock)
            {
                foreach (var node in nodes)
                {
                    if(_nodes.Contains(node.TopicId))
                    {
                        throw new ArgumentException($"A node with the topic ID '{node.TopicId}' already exists in the device '{this.TopicId}'.");
                    }

                    node.Parent = this;
                    _nodesAttribute.Add(node.TopicId);
                    _nodes.Add(node.GetTopic(), node);
                }
            }
        }

        internal PropertyBase[] GetAllSettableProperties()
        {
            var properties = new ArrayList();
            foreach (var node in Nodes)
            {
                foreach (var property in node.Properties)
                {
                    if (property.SettableAttribute.Value)
                    {
                        properties.Add(property);
                    }
                }
            }

            return (PropertyBase[])properties.ToArray(typeof(PropertyBase));
        }
    }
}
