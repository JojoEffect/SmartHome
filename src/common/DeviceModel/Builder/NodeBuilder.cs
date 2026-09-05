using SmartHome.DeviceModel.Properties;
using System;

namespace SmartHome.DeviceModel.Builder
{
    public class NodeBuilder
    {
        private readonly DeviceBuilder _deviceBuilder;
        private readonly string _name;
        private readonly string _id;
        private readonly string _type;
        private PropertyBase[] _properties = new PropertyBase[0];

        internal NodeBuilder(DeviceBuilder deviceBuilder, string id, string name, string type)
        {
            _deviceBuilder = deviceBuilder;
            _id = id;
            _name = name;
            _type = type;
        }

        public DeviceBuilder BuildNode() => BuildNode(out _);

        public DeviceBuilder BuildNode(out Node node)
        {
            node = new Node(_id, _name, _type);
            node.AddProperties(_properties);
            _deviceBuilder.PushNode(node);

            return _deviceBuilder;
        }

        public StringPropertyBuilder AddStringProperty(string id, string name, string initialValue)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name, initialValue);
        }

        public IntegerPropertyBuilder AddIntegerProperty(string id, string name, int initialValue)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name, initialValue);
        }

        public FloatPropertyBuilder AddFloatProperty(string id, string name, double initialValue)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name, initialValue);
        }

        public BooleanPropertyBuilder AddBooleanProperty(string id, string name, bool initialValue)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name, initialValue);
        }

        public EnumPropertyBuilder AddEnumProperty(string id, string name, string initialValue)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name, initialValue);
        }

        public ColorPropertyBuilder AddColorProperty(string id, string name, ColorValue initialValue)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name, initialValue);
        }

        /// <summary>
        /// Adds a Homie v5 datetime property. A Homie v4 adapter cannot publish one and
        /// is expected to refuse the device rather than invent a spelling.
        /// </summary>
        public DateTimePropertyBuilder AddDateTimeProperty(string id, string name)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name);
        }

        /// <summary>
        /// Adds a Homie v5 duration property. A Homie v4 adapter cannot publish one and
        /// is expected to refuse the device rather than invent a spelling.
        /// </summary>
        public DurationPropertyBuilder AddDurationProperty(string id, string name)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name);
        }

        /// <summary>
        /// Adds a Homie v5 JSON property. A Homie v4 adapter cannot publish one and is
        /// expected to refuse the device rather than invent a spelling.
        /// </summary>
        public JsonPropertyBuilder AddJsonProperty(string id, string name, string initialValue)
        {
            NamedEntityBase.ValidateId(id);

            return new(this, id, name, initialValue);
        }

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
