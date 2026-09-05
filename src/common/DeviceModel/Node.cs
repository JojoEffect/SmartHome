using SmartHome.DeviceModel.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;

namespace SmartHome.DeviceModel
{
    /// <summary>
    /// A group of properties within a device -- a sensor, a relay bank, an engine.
    /// </summary>
    /// <remarks>
    /// Both Homie versions have this level; Home Assistant does not, and an HA adapter
    /// has to flatten it by folding the node id into each entity's id. That flattening is
    /// the adapter's problem precisely because the node is real in the model: throwing
    /// the level away here would lose information both Homie versions need.
    /// </remarks>
    public class Node : NamedEntityBase
    {
        private readonly object _lock = new();

        private PropertyBase[] _properties = new PropertyBase[0];
        private readonly ILogger _logger;

        public Node(string id, string name, string type)
            : base(id, name)
        {
            Type = type;
            _logger = this.GetCurrentClassLogger();
        }

        /// <summary>
        /// What kind of node this is, e.g. <c>"BMP280"</c>. Free-form, and carried
        /// through by the Homie adapters as their <c>type</c>.
        /// </summary>
        public string Type { get; }

        /// <summary>The node's properties, in the order they were declared.</summary>
        public PropertyBase[] Properties => _properties;

        internal void AddProperty(PropertyBase property) => AddProperties(new PropertyBase[] { property });

        internal void AddProperties(PropertyBase[] properties)
        {
            lock (_lock)
            {
                foreach (var property in properties)
                {
                    _logger.LogDebug($"Adding property '{property.Id}' to node '{Id}'.");
                    property.Parent = this;
                }

                // Create and set a new array with the new properties
                PropertyBase[] newProperties = new PropertyBase[_properties.Length + properties.Length];
                Array.Copy(_properties, newProperties, _properties.Length);
                Array.Copy(properties, 0, newProperties, _properties.Length, properties.Length);
                _properties = newProperties;
            }
        }
    }
}
