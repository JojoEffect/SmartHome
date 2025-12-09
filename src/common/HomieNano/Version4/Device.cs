using HomieNano.Version4.Attributes;
using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using HomieNano.Version4.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;
using System.Collections;

namespace HomieNano.Version4
{
    public delegate void DeviceStateChangeHandler(DeviceStateChangeEventArgs args);

    public class Device : NamedHomieEntityBase
    {
        private readonly object _lock = new();

        private readonly HomieAttribute _homieAttribute;
        private readonly NodesAttribute _nodesAttribute;
        private readonly StateAttribute _stateAttribute;
        private readonly ExtensionsAttribute _extensionsAttribute;
        private readonly IDictionary _nodes = new Hashtable();
        private readonly ILogger _logger;

        public Device(string topicId, string name, string[] extensions, string? implementation = null)
            : base(topicId, name)
        {
            _homieAttribute = new(this, Constants.Version4);
            _nodesAttribute = new(this, new string[0]);
            _stateAttribute = new(this, State.Disconnected);
            _extensionsAttribute = new(this, extensions);
            ImplementationAttribute = implementation == null ? null : new(this, implementation);
            _logger = this.GetCurrentClassLogger();
        }

        public event DeviceStateChangeHandler? OnDeviceStateChange;

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

        internal void AddNode(Node node) => AddNodes(new Node[] { node });

        internal void AddNodes(Node[] nodes)
        {
            lock (_lock)
            {
                foreach (var node in nodes)
                {
                    _logger.LogDebug($"Adding node '{node.TopicId}' to device '{TopicId}'.");
                    if (_nodes.Contains(node.TopicId))
                    {
                        throw new ArgumentException($"A node with the topic ID '{node.TopicId}' already exists in the device '{TopicId}'.");
                    }

                    node.Parent = this;
                    _nodesAttribute.Add(node.TopicId);
                    _nodes.Add(node.GetTopic(), node);
                }
            }
        }

        internal bool TryChangeState(State newState)
        {
            _logger.LogDebug($"Attempting to change state of device '{TopicId}' from '{_stateAttribute.Value.ToHomieString()}' to '{newState.ToHomieString()}'.");
            if (CanChangeState(newState))
            {
                var oldState = _stateAttribute.Value;
                _stateAttribute.Value = newState;

                _logger.LogInformation($"Device '{TopicId}' state changed from '{oldState.ToHomieString()}' to '{newState.ToHomieString()}'.");

                OnDeviceStateChange?.Invoke(new DeviceStateChangeEventArgs(this, oldState, newState));
                return true;
            }

            _logger.LogWarning($"Invalid state transition attempted for device '{TopicId}' from '{_stateAttribute.Value.ToHomieString()}' to '{newState.ToHomieString()}'.");

            return false;
        }

        private bool CanChangeState(State newState) => StateAttribute.Value switch
        {
            State.Init => newState == State.Ready || newState == State.Alert || newState == State.Disconnected,
            State.Ready => newState == State.Sleeping || newState == State.Alert || newState == State.Disconnected,
            State.Sleeping => newState == State.Ready || newState == State.Alert || newState == State.Disconnected,
            State.Alert => newState == State.Ready || newState == State.Disconnected,
            State.Disconnected => newState == State.Init || newState == State.Ready,
            State.Lost => newState == State.Init || newState == State.Ready,
            _ => false,
        };

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
