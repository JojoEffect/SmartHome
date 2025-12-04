using HomieNano.Version4.Attributes;
using HomieNano.Version4.Properties;
using System;

namespace HomieNano.Version4
{
    public class Node : NamedHomieEntityBase
    {
        private readonly object _lock = new();

        private readonly PropertiesAttribute _propertiesAttribute;
        private readonly TypeAttribute _typeAttribute;
        private PropertyBase[] _properties = new PropertyBase[0];

        public Node(string topicId, string name, string type)
            : base(topicId, name)
        {
            _typeAttribute = new TypeAttribute(this, type);
            _propertiesAttribute = new(this, Utils.GetTopicIds(_properties));
        }

        public TypeAttribute TypeAttribute => _typeAttribute;

        public PropertiesAttribute PropertiesAttribute => _propertiesAttribute;

        public PropertyBase[] Properties => _properties;

        public void AddProperty(PropertyBase property) => AddProperties(new PropertyBase[] { property });

        public void AddProperties(PropertyBase[] properties)
        {
            lock (_lock)
            {
                foreach (var property in properties)
                {
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
