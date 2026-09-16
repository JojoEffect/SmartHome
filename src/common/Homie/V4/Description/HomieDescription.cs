using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using SmartHome.Homie.V4.Settings;
using SmartHome.Text;
using System;
using System.Collections;

namespace SmartHome.Homie.V4.Description
{
    /// <summary>
    /// A model device rendered as Homie v4: every topic, every attribute payload, and
    /// the order they are announced in.
    /// </summary>
    /// <remarks>
    /// Built once, by <see cref="HomieClient"/>'s constructor, and that is the whole
    /// point of the type. Everything in here is fixed the moment the device is built --
    /// the model's <c>AddNodes</c> and <c>AddProperties</c> are internal to the model,
    /// so no node or property can appear afterwards -- while an announce re-runs on
    /// every reconnect. Rendering it per announce would redo the same string work on
    /// every broker restart.
    ///
    /// It is also where the device is refused. A property v4 cannot express -- a
    /// datatype the convention has no token for, a range it has no spelling for, a bound
    /// it cannot render -- throws here, when the device is built, rather than publishing
    /// an approximation a controller would misread. The model carries the union of what
    /// any convention might hold precisely so that each adapter can draw this line for
    /// itself.
    /// </remarks>
    internal sealed class HomieDescription
    {
        private readonly HomiePropertyDescription[] _properties;

        /// <exception cref="ArgumentException">
        /// A property cannot be expressed in Homie v4. The message names it by topic.
        /// </exception>
        internal HomieDescription(Device device, HomieDeviceSettings deviceSettings)
        {
            DeviceId = device.Id;
            StateTopic = HomieTopics.Attribute(device, Constants.StateAttributeTopicId);

            // Declaration order, which is also the order $nodes lists them in. For a
            // device with more than one node this differs from what the predecessor
            // published: it kept nodes in a Hashtable and announced them in its
            // enumeration order while listing them in $nodes in declaration order, so
            // the two disagreed. Both in-tree devices have a single node, so nothing
            // moves for them; for a multi-node device the announce and the list now
            // agree, which is the behaviour to keep.
            var nodes = device.Nodes;
            var nodeIds = new string[nodes.Length];
            var nodeDescriptions = new HomieNodeDescription[nodes.Length];
            var allProperties = new ArrayList();
            var settableProperties = new ArrayList();

            for (int i = 0; i < nodes.Length; i++)
            {
                nodeIds[i] = nodes[i].Id;
                nodeDescriptions[i] = DescribeNode(nodes[i], allProperties, settableProperties);
            }

            DeviceAttributes = DescribeDevice(device, nodeIds, deviceSettings);
            Nodes = nodeDescriptions;
            SettableProperties = (HomiePropertyDescription[])settableProperties.ToArray(typeof(HomiePropertyDescription));
            _properties = (HomiePropertyDescription[])allProperties.ToArray(typeof(HomiePropertyDescription));
        }

        /// <summary>
        /// <c>$homie</c>, <c>$name</c>, <c>$nodes</c>, <c>$extensions</c> and, when the
        /// device names one, <c>$implementation</c> -- in announce order.
        /// </summary>
        /// <remarks>
        /// <c>$state</c> is not among them: it is the one device attribute whose payload
        /// moves while the device runs, so it is published from the lifecycle rather
        /// than from a precomputed message.
        /// </remarks>
        internal HomieAttributeMessage[] DeviceAttributes { get; }

        /// <summary>The device's own id, for log lines.</summary>
        internal string DeviceId { get; }

        /// <summary>Where <c>$state</c> goes, which is also the last will's topic.</summary>
        internal string StateTopic { get; }

        /// <summary>The device's nodes, in declaration order.</summary>
        internal HomieNodeDescription[] Nodes { get; }

        /// <summary>
        /// The settable properties, in exactly the order
        /// <c>Device.GetAllSettableProperties()</c> walks them -- nodes in declaration
        /// order, then each node's properties.
        /// </summary>
        /// <remarks>
        /// The order matters beyond tidiness: it is the insertion order of the
        /// subscription table, and a Hashtable's enumeration order is what ends up in
        /// the SUBSCRIBE packet.
        /// </remarks>
        internal HomiePropertyDescription[] SettableProperties { get; }

        /// <summary>
        /// The value topic of a property in this tree, or null if it is not in it.
        /// </summary>
        /// <remarks>
        /// A linear scan over an array fixed at construction, called from the property's
        /// own update handler. A device has a handful of properties, the scan allocates
        /// nothing, and reference identity is the only key available: the model's ids
        /// are unique within a node, not within a device.
        /// </remarks>
        internal string? TopicOf(PropertyBase property)
        {
            for (int i = 0; i < _properties.Length; i++)
            {
                if (_properties[i].Property == property)
                {
                    return _properties[i].Topic;
                }
            }

            return null;
        }

        private static HomieAttributeMessage[] DescribeDevice(Device device, string[] nodeIds, HomieDeviceSettings deviceSettings)
        {
            var attributes = new ArrayList();
            attributes.Add(new HomieAttributeMessage(HomieTopics.Attribute(device, Constants.HomieAttributeTopicId), Constants.Version4));
            attributes.Add(new HomieAttributeMessage(HomieTopics.Attribute(device, Constants.NameAttributeTopicId), OrEmpty(device.Name)));
            attributes.Add(new HomieAttributeMessage(HomieTopics.Attribute(device, Constants.NodesAttributeTopicId), Join(nodeIds)));
            // $extensions is mandatory: "The following device attributes are mandatory
            // and MUST be send, even if it is just an empty string."
            attributes.Add(new HomieAttributeMessage(HomieTopics.Attribute(device, Constants.ExtensionAttributeTopicId), Join(deviceSettings.Extensions)));

            // $implementation is optional, and omitted rather than published empty: an
            // empty retained payload deletes whatever a previous run left in the store,
            // which says something different from "this device does not name one".
            var implementation = deviceSettings.Implementation;
            if (implementation != null && implementation.Length > 0)
            {
                attributes.Add(new HomieAttributeMessage(HomieTopics.Attribute(device, Constants.ImplementationAttributeTopicId), implementation));
            }

            return (HomieAttributeMessage[])attributes.ToArray(typeof(HomieAttributeMessage));
        }

        private static HomieNodeDescription DescribeNode(Node node, ArrayList allProperties, ArrayList settableProperties)
        {
            var properties = node.Properties;
            var propertyIds = new string[properties.Length];
            var propertyDescriptions = new HomiePropertyDescription[properties.Length];

            for (int i = 0; i < properties.Length; i++)
            {
                propertyIds[i] = properties[i].Id;
                propertyDescriptions[i] = DescribeProperty(properties[i]);
                allProperties.Add(propertyDescriptions[i]);

                if (properties[i].Settable)
                {
                    settableProperties.Add(propertyDescriptions[i]);
                }
            }

            var attributes = new HomieAttributeMessage[]
            {
                new HomieAttributeMessage(HomieTopics.Attribute(node, Constants.NameAttributeTopicId), OrEmpty(node.Name)),
                new HomieAttributeMessage(HomieTopics.Attribute(node, Constants.TypeAttributeTopicId), OrEmpty(node.Type)),
                new HomieAttributeMessage(HomieTopics.Attribute(node, Constants.PropertiesAttributeTopicId), Join(propertyIds)),
            };

            return new HomieNodeDescription(node.Id, attributes, propertyDescriptions);
        }

        private static HomiePropertyDescription DescribeProperty(PropertyBase property)
        {
            var topic = HomieTopics.Of(property);

            var attributes = new HomieAttributeMessage[]
            {
                new HomieAttributeMessage(HomieTopics.Attribute(property, Constants.NameAttributeTopicId), OrEmpty(property.Name)),
                new HomieAttributeMessage(HomieTopics.Attribute(property, Constants.DataTypeAttributeTopicId), RenderDataType(property, topic)),
                new HomieAttributeMessage(HomieTopics.Attribute(property, Constants.FormatAttributeTopicId), RenderFormat(property, topic)),
                new HomieAttributeMessage(HomieTopics.Attribute(property, Constants.SettableAttributeTopicId), Flag(property.Settable)),
                new HomieAttributeMessage(HomieTopics.Attribute(property, Constants.RetainedAttributeTopicId), Flag(property.Retained)),
                // The unit goes out verbatim. The model holds it as a free string, and
                // v4 says only that it "SHOULD be one of" a recommended list -- so
                // anything else is still a legal unit, and translating would be this
                // adapter inventing a rule the convention does not have.
                new HomieAttributeMessage(HomieTopics.Attribute(property, Constants.UnitAttributeTopicId), property.Unit),
            };

            return new HomiePropertyDescription(property, topic, HomieTopics.Command(property), attributes);
        }

        /// <remarks>
        /// The datatype tokens are the adapter's vocabulary; the model deliberately has
        /// no rendering for them, since a convention may publish a datatype, infer a
        /// component from it, or say nothing at all.
        /// </remarks>
        private static string RenderDataType(PropertyBase property, string topic) => property.DataType switch
        {
            DataType.Integer => "integer",
            DataType.Float => "float",
            DataType.Boolean => "boolean",
            DataType.String => "string",
            DataType.Enum => "enum",
            DataType.Color => "color",
            // Refused loudly rather than spelled as something near it. v4 has six
            // datatypes and no room for these three; publishing a made-up token, or
            // quietly demoting one to 'string', would advertise a payload grammar no
            // controller shares and the property itself does not enforce.
            DataType.DateTime => throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: the convention has no datatype for an instant in time."),
            DataType.Duration => throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: the convention has no datatype for an elapsed time."),
            DataType.Json => throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: the convention has no datatype for a JSON array or object."),
            _ => throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: it declares a datatype this adapter does not know."),
        };

        /// <remarks>
        /// Rendered from the parsed format the property holds, never from the text a
        /// device author may have written: the model keeps the structured value and
        /// nothing else, so a declaration that failed to parse declares nothing and goes
        /// out as an empty <c>$format</c>.
        /// </remarks>
        private static string RenderFormat(PropertyBase property, string topic)
        {
            if (property is IntegerProperty integerProperty)
            {
                return RenderIntegerFormat(integerProperty, topic);
            }

            if (property is FloatProperty floatProperty)
            {
                return RenderFloatFormat(floatProperty, topic);
            }

            if (property is EnumProperty enumProperty)
            {
                // Already trimmed and comma-joined in declaration order by the model,
                // which is exactly v4's spelling.
                return enumProperty.Options == null ? string.Empty : enumProperty.Options.ToString();
            }

            if (property is ColorProperty colorProperty)
            {
                // v4 declares exactly one encoding, so the rest of the model's ordered
                // list goes unsaid on this wire.
                return colorProperty.Formats == null ? string.Empty : colorProperty.Formats.Preferred;
            }

            // Boolean and string: v4 defines no $format for either. A boolean's labels
            // are dropped outright -- they name the two values for a human and do not
            // define the payloads, so there is nothing here they could be mapped onto
            // without changing what the device claims to accept.
            return string.Empty;
        }

        private static string RenderIntegerFormat(IntegerProperty property, string topic)
        {
            var range = property.Range;
            if (range == null)
            {
                return string.Empty;
            }

            EnsureClosedAndStepless(range, topic);

            return $"{RenderIntegerBound(range.Minimum, topic)}:{RenderIntegerBound(range.Maximum, topic)}";
        }

        private static string RenderFloatFormat(FloatProperty property, string topic)
        {
            var range = property.Range;
            if (range == null)
            {
                return string.Empty;
            }

            EnsureClosedAndStepless(range, topic);

            return $"{RenderFloatBound(property, range.Minimum, topic)}:{RenderFloatBound(property, range.Maximum, topic)}";
        }

        /// <remarks>
        /// v4's numeric <c>$format</c> is <c>from:to</c>, both ends present and no step.
        /// An open end or a step therefore has no spelling here at all, and rendering
        /// one without the part that does not fit would publish a declaration saying
        /// something the device did not.
        /// </remarks>
        private static void EnsureClosedAndStepless(NumericRange range, string topic)
        {
            if (!range.HasMinimum || !range.HasMaximum)
            {
                throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: its $format is 'from:to' with both ends present, and the range declares only one.");
            }

            if (range.HasStep)
            {
                throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: its $format has no step, and dropping one would publish a declaration the device did not make.");
            }
        }

        /// <remarks>
        /// v4's integer <c>$format</c> is <c>from:to</c> of whole numbers, so a bound
        /// that is not one has no rendering rather than a rounded one -- rounding would
        /// advertise a bound the property itself does not enforce.
        /// </remarks>
        private static string RenderIntegerBound(double bound, string topic)
        {
            if (!IsCastableToLong(bound))
            {
                throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: the range bound {bound} is too large to render as a whole number.");
            }

            long truncated = (long)bound;
            if (truncated != bound)
            {
                throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: its integer $format is 'from:to' of whole numbers, and the bound {bound} is not one.");
            }

            return truncated.ToString();
        }

        /// <remarks>
        /// A whole-number bound renders as the whole number, whatever the property's
        /// decimals. That is what keeps a range written as <c>"0:100"</c> going out as
        /// <c>0:100</c> for an integer and a float alike, which is what the devices in
        /// this tree already publish.
        /// </remarks>
        private static string RenderFloatBound(FloatProperty property, double bound, string topic)
        {
            if (IsCastableToLong(bound))
            {
                long truncated = (long)bound;
                if (truncated == bound)
                {
                    // Via long rather than the fixed-decimal formatter, so a negative
                    // zero bound comes out as "0" and a whole number carries no decimals.
                    return truncated.ToString();
                }
            }

            if (bound >= FloatProperty.MaxPublishableMagnitude || bound <= -FloatProperty.MaxPublishableMagnitude)
            {
                throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: the range bound {bound} has no fixed-decimal rendering that fits the runtime's format buffer.");
            }

            // The property's own encoding, never NumericRange.ToString(): that one
            // renders its bounds with "G", which on this runtime prints 21.5 as
            // 21.499999999999999. A declared bound and the values published against it
            // have to be written the same way, or a controller can reject its own
            // device's readings.
            var rendered = property.FormatValue(bound);

            EnsureRenderableAtDecimals(property, rendered, bound, topic);

            return rendered;
        }

        /// <remarks>
        /// A bound the property cannot write exactly at its own precision is refused
        /// rather than rounded into the declaration: a rounded bound is a different
        /// bound, and the property would go on enforcing the one it was given, so
        /// exactly the payloads at the edge would be advertised as acceptable and then
        /// rejected.
        ///
        /// Asked by round-tripping the exact string this method's caller is about to
        /// publish, rather than by comparing the scaled bound against a tolerance. A
        /// tolerance has to be generous enough to absorb the inexactness of the scaling
        /// multiplication itself -- 1234567890.12 at two decimals lands ~2e-5 from a
        /// whole number -- and any tolerance that does is larger than the worst real
        /// rounding error (0.5) once the bound is big enough, which made the check
        /// vacuous above roughly 5e11 at two decimals and let exactly the bounds it
        /// exists to refuse through. The round trip has no such window: it is exact at
        /// every magnitude, it needs no skip threshold, and <c>double.TryParse</c> here
        /// is the same parse a <c>/set</c> payload goes through, so "the rendered bound
        /// parses back to the declared bound" is literally "a controller echoing the
        /// advertised bound is accepted".
        /// </remarks>
        private static void EnsureRenderableAtDecimals(FloatProperty property, string rendered, double bound, string topic)
        {
            if (!double.TryParse(rendered, out var roundTripped) || roundTripped != bound)
            {
                throw new ArgumentException($"Property '{topic}' cannot be published as Homie v4: the range bound {bound} cannot be written exactly at the {property.Decimals} decimal places the property publishes.");
            }
        }

        /// <remarks>
        /// Checked before any cast, never after: the runtime's double-to-long conversion
        /// is an unchecked C cast, so an out-of-range value does not throw -- it hands
        /// back whatever the hardware produced, and the "is this a whole number" test
        /// below it would then be answering about a different number.
        /// </remarks>
        private static bool IsCastableToLong(double value) => value > -9.2e18 && value < 9.2e18;

        /// <summary>A missing name or node type is the empty payload, not a dropped attribute.</summary>
        private static string OrEmpty(string? value) => value ?? string.Empty;

        /// <summary>
        /// <c>$settable</c> and <c>$retained</c> are the literal <c>true</c> and
        /// <c>false</c>, never <c>bool.ToString()</c>, which returns "True"/"False".
        /// </summary>
        private static string Flag(bool value) => value ? "true" : "false";

        /// <remarks>
        /// The one place <c>$nodes</c>, <c>$properties</c> and <c>$extensions</c> are
        /// turned into their wire format. Nothing declared is the empty payload rather
        /// than an error -- <c>StringUtils.Join</c> throws on null, and all three of
        /// these attributes are mandatory whether or not there is anything to list.
        /// </remarks>
        private static string Join(string[]? values)
            => values == null || values.Length == 0 ? string.Empty : StringUtils.Join(",", values);
    }
}
