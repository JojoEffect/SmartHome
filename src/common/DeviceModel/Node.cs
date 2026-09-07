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
        /// <remarks>
        /// A fresh array each time, for the reason <c>Device.Nodes</c> and
        /// <c>EnumOptions.Values</c> copy too: the stored one is this node's own state,
        /// and handing it out would let a caller rewrite a tree the device may already
        /// have announced.
        /// </remarks>
        public PropertyBase[] Properties
        {
            get
            {
                lock (_lock)
                {
                    PropertyBase[] properties = new PropertyBase[_properties.Length];
                    Array.Copy(_properties, properties, _properties.Length);
                    return properties;
                }
            }
        }

        internal void AddProperty(PropertyBase property) => AddProperties(new PropertyBase[] { property });

        internal void AddProperties(PropertyBase[] properties)
        {
            lock (_lock)
            {
                // Grown one at a time rather than in one block copy, so that the guard
                // below sees the batch's own earlier entries as well as what the node
                // already held -- otherwise two duplicates arriving in a single call
                // would check each other's absence and both land.
                foreach (var property in properties)
                {
                    _logger.LogDebug($"Adding property '{property.Id}' to node '{Id}'.");

                    // Same guard Device.AddNodes holds one level up, for the same reason:
                    // two properties sharing an id are two entities an adapter announces
                    // under one name and subscribes one command topic for, so a single
                    // /set lands on both while only one of them is ever published from.
                    if (ContainsProperty(property.Id))
                    {
                        throw new ArgumentException($"A property with the id '{property.Id}' already exists in the node '{Id}'.");
                    }

                    property.Parent = this;

                    PropertyBase[] grown = new PropertyBase[_properties.Length + 1];
                    Array.Copy(_properties, grown, _properties.Length);
                    grown[_properties.Length] = property;
                    _properties = grown;
                }
            }
        }

        /// <remarks>
        /// Called only from inside <see cref="_lock"/>; it does not take the lock itself.
        /// </remarks>
        private bool ContainsProperty(string id)
        {
            for (int i = 0; i < _properties.Length; i++)
            {
                if (_properties[i].Id == id)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
