using SmartHome.Homie.V4.Attributes;
using SmartHome.Homie.V4.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;

namespace SmartHome.Homie.V4
{
    public class Node : NamedHomieEntityBase
    {
        private readonly object _lock = new();

        private readonly PropertiesAttribute _propertiesAttribute;
        private readonly TypeAttribute _typeAttribute;
        private PropertyBase[] _properties = new PropertyBase[0];
        private readonly ILogger _logger;

        public Node(string topicId, string name, string type)
            : base(topicId, name)
        {
            _typeAttribute = new TypeAttribute(this, type);
            _propertiesAttribute = new(this, TopicIds.GetTopicIds(_properties));
            _logger = this.GetCurrentClassLogger();
        }

        public TypeAttribute TypeAttribute => _typeAttribute;

        public PropertiesAttribute PropertiesAttribute => _propertiesAttribute;

        public PropertyBase[] Properties => _properties;

        internal void AddProperty(PropertyBase property) => AddProperties(new PropertyBase[] { property });

        internal void AddProperties(PropertyBase[] properties)
        {
            lock (_lock)
            {
                foreach (var property in properties)
                {
                    _logger.LogDebug($"Adding property '{property.TopicId}' to node '{TopicId}'.");
                    // Add the node to the NodesAttribute
                    _propertiesAttribute.Add(property.TopicId);
                    property.Parent = this;
                }
                // Create and set a new array with the new nodes
                PropertyBase[] newProperties = new PropertyBase[_properties.Length + properties.Length];
                Array.Copy(_properties, newProperties, _properties.Length);
                Array.Copy(properties, 0, newProperties, _properties.Length, properties.Length);
                _properties = newProperties;
            }
        }
    }
}
