using HomieNano.Version4.Properties;
using System;

namespace HomieNano.Version4.Builder
{
    public class HomieNodeBuilder
    {
        private readonly HomieDeviceBuilder _deviceBuilder;
        private readonly string _name;
        private readonly string _topicId;
        private readonly string _type;
        private PropertyBase[] _properties = new PropertyBase[0];

        internal HomieNodeBuilder(HomieDeviceBuilder deviceBuilder, string topicId, string name, string type)
        {
            _deviceBuilder = deviceBuilder;
            _topicId = topicId;
            _name = name;
            _type = type;
        }

        public HomieDeviceBuilder BuildNode() => BuildNode(out _);

        public HomieDeviceBuilder BuildNode(out Node node)
        {
            node = new Node(_topicId, _name, _type);
            node.AddProperties(_properties);
            _deviceBuilder.PushNode(node);

            return _deviceBuilder;
        }

        public HomieStringPropertyBuilder AddStringProperty(string topicId, string name, string initialValue)
            => new(this, topicId, name, initialValue);

        public HomieIntegerPropertyBuilder AddIntegerProperty(string topicId, string name, int initialValue)
            => new(this, topicId, name, initialValue);

        public HomieFloatPropertyBuilder AddFloatProperty(string topicId, string name, double initialValue)
            => new(this, topicId, name, initialValue);

        public HomieBooleanPropertyBuilder AddBooleanProperty(string topicId, string name, bool initialValue)
            => new(this, topicId, name, initialValue);

        public HomieEnumPropertyBuilder AddEnumProperty(string topicId, string name, string initialValue)
            => new(this, topicId, name, initialValue);

        public HomieColorPropertyBuilder AddColorProperty(string topicId, string name, Properties.HomieColor initialValue)
            => new(this, topicId, name, initialValue);

        /// <summary>
        /// Adds the specified property to the collection of properties managed by this instance.
        /// </summary>
        /// <param name="property">The property to add to the collection. Cannot be null.</param>
        internal void PushProperty(PropertyBase property)
        {
            // Create and set a new array with the new property
            PropertyBase[] newProperties = new PropertyBase[_properties.Length + 1];
            Array.Copy(_properties, newProperties, _properties.Length);
            newProperties[_properties.Length] = property;
            _properties = newProperties;
        }
    }
}
