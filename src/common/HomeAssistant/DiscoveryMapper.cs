using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using System;
using System.Collections;
using System.Text;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Turns a device description into Home Assistant MQTT discovery configurations.
    /// </summary>
    /// <remarks>
    /// The reason this library is worth having rather than declaring entities by hand: a
    /// property already declares its datatype, its unit, what it may hold, what its
    /// number means and whether a controller may write to it, which is nearly everything
    /// a component configuration needs. Nothing is duplicated and nothing can drift --
    /// add a property to the device and Home Assistant gets an entity for it.
    ///
    /// A pure function, deliberately: every topic, every payload key and every device
    /// class is decided here, with no broker and no device, so the whole mapping is
    /// asserted in the unit suite where CI can run it. The client that publishes these is
    /// a separate concern.
    ///
    /// **This is also where a device is refused.** A property Home Assistant cannot carry
    /// -- a datatype with no entity, a quantity kind on something that holds no number, a
    /// unit its device class rejects, a settable number with no declared range -- throws
    /// here, naming the property by its state topic, rather than publishing a
    /// configuration that is dropped by a validator inside Home Assistant and logged
    /// somewhere nobody is looking. The model carries the union of what any convention
    /// might hold precisely so that each adapter can draw this line for itself.
    /// </remarks>
    public static class DiscoveryMapper
    {
        /// <summary>
        /// The QoS every entity is told to use, for its own state subscription and for
        /// the commands it publishes.
        /// </summary>
        /// <remarks>
        /// Home Assistant's default is 0 (<c>DEFAULT_QOS</c> in
        /// <c>homeassistant/components/mqtt/const.py</c>), which makes a command
        /// fire-and-forget: a controller press that the broker drops leaves no trace at
        /// either end. The device subscribes its command topics at least once, so this
        /// says the same thing from the other side.
        /// </remarks>
        public const int QosLevel = 1;

        /// <summary>
        /// The smallest step Home Assistant's number entity accepts.
        /// </summary>
        /// <remarks>
        /// <c>vol.Range(min=1e-3)</c> on <c>CONF_STEP</c> in
        /// <c>homeassistant/components/mqtt/number.py</c>: a smaller one is not clamped,
        /// it fails the whole configuration and the entity never appears.
        /// </remarks>
        public const double SmallestStep = 0.001;

        // Decimal places in SmallestStep, which is what a derived step is capped at.
        private const int SmallestStepDecimals = 3;

        // The same number again, as the text a message quotes. Not interpolated from the
        // constant above, because a double reaches a message through this runtime's "G"
        // formatting, which renders 21.5 as 21.499999999999999 -- and a refusal that
        // misquotes its own threshold is one a reader stops trusting.
        private const string SmallestStepText = "0.001";

        // A boolean payload is the literal 'true' or 'false' -- what every property of
        // that datatype publishes and accepts. Home Assistant's own defaults are ON and
        // OFF, so without these every reading would be read as 'off' and every command
        // would be refused by the property. Note that this is the adapter naming the
        // payloads it already knows, and NOT a rendering of BooleanLabels: those say what
        // to call the two values for a human and do not define the payloads, so mapping
        // them here would advertise payloads the device refuses.
        private const string BooleanTrue = "true";
        private const string BooleanFalse = "false";

        /// <summary>
        /// What the diagnostic entity a device's alerts are rendered as is called in its
        /// entity id: <c>&lt;device&gt;_problem</c>.
        /// </summary>
        /// <remarks>
        /// The id only. Its displayed name is written where it is used and is capitalised
        /// ("Problem"), because the two are held to different rules: an entity id shares
        /// its alphabet with the model's ids, which are lowercase, while a displayed name
        /// is human-facing text that Home Assistant prefixes with the device's own name.
        /// Deriving one from the other would tie a rename in one to a rename in the
        /// other, and changing an entity id is a migration -- a retained configuration
        /// under the old id outlives the device that published it.
        /// </remarks>
        public const string ProblemEntityName = "problem";

        /// <summary>
        /// Builds one discovery configuration per property of <paramref name="device"/>,
        /// plus the one diagnostic entity the device itself gets.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// A property cannot be published to Home Assistant. The message names it by its
        /// state topic and says why.
        /// </exception>
        public static DiscoveryEntity[] Map(Device device, HomeAssistantSettings settings)
        {
            var deviceJson = BuildDeviceBlock(device, settings);
            var originJson = BuildOriginBlock(settings);
            var availabilityTopic = HomeAssistantTopics.Availability(device);

            var nodes = device.Nodes;

            // Home Assistant composes an entity's displayed name from the device's name
            // and the entity's own, with no level in between. On a device with a single
            // node that is exactly right -- "Room sensor office Temperature" -- and
            // folding the node's name in would only repeat what the device is. On a
            // device with several, the node is what tells two entities apart: a two-zone
            // irrigation controller has a 'valve' property in each, both named "Valve",
            // and without the node they read identically in every list Home Assistant
            // draws.
            var foldNodeName = nodes.Length > 1;

            var entities = new ArrayList();

            for (int i = 0; i < nodes.Length; i++)
            {
                var properties = nodes[i].Properties;
                for (int j = 0; j < properties.Length; j++)
                {
                    entities.Add(MapProperty(
                        nodes[i],
                        properties[j],
                        settings,
                        deviceJson,
                        originJson,
                        availabilityTopic,
                        foldNodeName));
                }
            }

            entities.Add(MapAlerts(device, deviceJson, originJson, availabilityTopic, settings));

            return (DiscoveryEntity[])entities.ToArray(typeof(DiscoveryEntity));
        }

        /// <summary>
        /// The device's alert set, rendered as one diagnostic entity.
        /// </summary>
        /// <remarks>
        /// Home Assistant has no vocabulary for a keyed alert set. A binary sensor with
        /// the <c>problem</c> device class carries the half it can say -- whether
        /// anything is wrong -- and the ids and messages travel as its attributes, which
        /// is the only place on this wire they fit. That is one-way and lossy in the
        /// other direction, as every alert mapping is: nothing can read a set back off
        /// an entity that is on or off.
        ///
        /// Published for every device, always, even one with no alerts and even one with
        /// no properties at all. That last case is the reason it is unconditional rather
        /// than added when the first alert is raised: a device that could not read its
        /// configuration announces *no* nodes and one alert saying why, so without this
        /// entity it would have no configuration to publish, no entity in Home Assistant,
        /// and therefore no device page at all -- the failure would be invisible in
        /// exactly the installation that needs to see it.
        ///
        /// A diagnostic category, so it sits with the device's own health rather than
        /// among the readings someone put on a dashboard.
        /// </remarks>
        private static DiscoveryEntity MapAlerts(
            Device device,
            string deviceJson,
            string originJson,
            string availabilityTopic,
            HomeAssistantSettings settings)
        {
            var objectId = HomeAssistantTopics.DeviceObjectId(device, ProblemEntityName);

            var json = new JsonWriter()
                // Capitalised, and deliberately not ProblemEntityName: see that
                // constant's remarks for why the id and the displayed name are written
                // separately.
                .String("name", "Problem")
                .String("uniq_id", objectId)
                .String("stat_t", HomeAssistantTopics.Problem(device))
                .String("avty_t", availabilityTopic)
                .Int("qos", QosLevel)
                .String("dev_cla", DeviceClass.Problem)
                .String("ent_cat", "diagnostic")
                // Where the ids and the messages go. Home Assistant reads a state and its
                // attributes from separate topics, because a binary sensor's state must be
                // exactly its on or off payload.
                .String("json_attr_t", HomeAssistantTopics.Alerts(device));

            // No expire_after, whatever the settings say: this entity publishes when
            // something changes and then stays silent, so an expiry would report a
            // healthy device's health as unknown. Same reasoning as a binary sensor built
            // from a property.

            json.Raw("dev", deviceJson).Raw("o", originJson);

            return new DiscoveryEntity(
                HomeAssistantTopics.ConfigTopic(settings.DiscoveryPrefix, Component.BinarySensor, objectId),
                json.ToJson());
        }

        private static DiscoveryEntity MapProperty(
            Node node,
            PropertyBase property,
            HomeAssistantSettings settings,
            string deviceJson,
            string originJson,
            string availabilityTopic,
            bool foldNodeName)
        {
            var stateTopic = HomeAssistantTopics.Of(property);
            var component = ComponentFor(property, stateTopic);
            var objectId = HomeAssistantTopics.ObjectId(property);
            var deviceClass = DeviceClassFor(property, stateTopic);

            var json = new JsonWriter()
                // Home Assistant prefixes this with the device's own name, so the
                // property's name is what belongs here -- "Temperature", not "Room sensor
                // office temperature".
                .String("name", EntityName(node, property, foldNodeName))
                .String("uniq_id", objectId)
                .String("stat_t", stateTopic)
                // No availability template: the payloads on that topic are already Home
                // Assistant's own 'online' and 'offline'. A template would only be needed
                // to translate a lifecycle vocabulary, which this adapter does not put on
                // the wire.
                .String("avty_t", availabilityTopic)
                .Int("qos", QosLevel);

            if (property.Settable)
            {
                json.String("cmd_t", HomeAssistantTopics.Command(property));
            }

            AppendComponentFields(json, component, property, settings, deviceClass, stateTopic);

            json.Raw("dev", deviceJson).Raw("o", originJson);

            return new DiscoveryEntity(
                HomeAssistantTopics.ConfigTopic(settings.DiscoveryPrefix, component, objectId),
                json.ToJson());
        }

        /// <summary>
        /// The component a property maps onto -- see <see cref="Component"/> for why the
        /// settable flag is half the answer.
        /// </summary>
        /// <remarks>
        /// A settable enum that declares no options is the one case where settability
        /// alone does not decide it: <c>options</c> is a required key of the select
        /// schema, so a select built from nothing is a configuration Home Assistant
        /// refuses and an entity that never appears. An enum property with no declared
        /// options accepts any payload, which is exactly a free-text control.
        ///
        /// A colour is a text entity rather than a light. Home Assistant's light is a
        /// composite of on/off, brightness and colour across several topics, and it
        /// requires a command topic for the on/off half that a single colour property
        /// does not have; a text entity carries the <c>&lt;r&gt;,&lt;g&gt;,&lt;b&gt;</c>
        /// triple the property already publishes and accepts. Mapping a whole node onto
        /// a light is the honest version of that and is not this slice's job.
        /// </remarks>
        private static string ComponentFor(PropertyBase property, string stateTopic)
        {
            var settable = property.Settable;

            switch (property.DataType)
            {
                case DataType.Integer:
                case DataType.Float:
                    return settable ? Component.Number : Component.Sensor;

                case DataType.Boolean:
                    return settable ? Component.Switch : Component.BinarySensor;

                case DataType.Enum:
                    if (!settable)
                    {
                        return Component.Sensor;
                    }

                    return OptionsOf(property) == null ? Component.Text : Component.Select;

                case DataType.String:
                case DataType.Color:
                    return settable ? Component.Text : Component.Sensor;

                // Refused loudly rather than approximated, the same call the v4 adapter
                // makes for the same three datatypes. Each of these could be made to
                // produce *something*, and each something would be wrong in a way only a
                // Home Assistant installation would show.
                case DataType.DateTime:
                    throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: a timestamp sensor could carry the value, but there is no MQTT entity that writes one back, and a datetime property cannot be given an initial reading -- so an announce would publish year one, retained, for every device that has not updated it yet.");

                case DataType.Duration:
                    throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: its duration device class reads a number in a declared time unit, and this datatype publishes an ISO 8601 duration such as 'PT5M', which Home Assistant cannot parse as a number.");

                case DataType.Json:
                    throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: it has no entity for a JSON document, only attributes attached to an entity that has a state of its own.");

                default:
                    throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: it declares a datatype this adapter does not know.");
            }
        }

        /// <summary>
        /// The device class a property's quantity kind names, or null when it declares
        /// none.
        /// </summary>
        /// <remarks>
        /// Null is a real answer, not a gap: an entity with no device class still shows
        /// its value and its unit, while one with the wrong class is either refused
        /// outright or quietly mis-drawn and mis-converted.
        ///
        /// The three refusals below are the "fail loudly on a pair Home Assistant would
        /// reject" half of this mapping. Note that a quantity kind says what a *number*
        /// means, so it has no reading at all on a boolean, a string, an enum or a
        /// colour: Home Assistant's device classes for those entities are a separate
        /// vocabulary (door, motion, moisture-as-wet-or-dry), and nothing in the model
        /// implies one.
        /// </remarks>
        private static string? DeviceClassFor(PropertyBase property, string stateTopic)
        {
            var kind = property.QuantityKind;
            if (kind == QuantityKind.None)
            {
                return null;
            }

            if (property.DataType != DataType.Integer && property.DataType != DataType.Float)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: it declares a quantity kind, which says what a number means, on a property that holds no number.");
            }

            var deviceClass = DeviceClass.FromQuantityKind(kind);
            if (deviceClass == null)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: this adapter has no device class for the quantity kind it declares.");
            }

            var units = DeviceClass.UnitsFor(deviceClass);

            if (property.Unit == null || property.Unit.Length == 0)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: its '{deviceClass}' device class is valid only with one of the units {Quote(units)}, and the property declares none.");
            }

            if (!DeviceClass.Accepts(deviceClass, property.Unit))
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: its '{deviceClass}' device class does not accept the unit '{property.Unit}'. Home Assistant accepts {Quote(units)}.");
            }

            return deviceClass;
        }

        private static void AppendComponentFields(
            JsonWriter json,
            string component,
            PropertyBase property,
            HomeAssistantSettings settings,
            string? deviceClass,
            string stateTopic)
        {
            switch (component)
            {
                case Component.Sensor:
                    AppendSensorFields(json, property, settings, deviceClass);
                    break;

                case Component.Number:
                    AppendNumberFields(json, property, deviceClass, stateTopic);
                    break;

                case Component.BinarySensor:
                case Component.Switch:
                    // state_on and state_off are deliberately not sent: Home Assistant
                    // defaults each to the matching payload
                    // (`state_on or config[CONF_PAYLOAD_ON]` in its switch platform), so
                    // they would be two more bytes per config saying what it already
                    // knows.
                    json.String("pl_on", BooleanTrue).String("pl_off", BooleanFalse);
                    break;

                case Component.Select:
                    // Non-null by construction: a settable enum with no options is mapped
                    // to a text entity instead, precisely because this key is required.
                    json.StringArray("options", OptionsOf(property)!.Values);
                    break;

                case Component.Text:
                    // Nothing to add. The mode defaults to text, and the length bounds
                    // default to 0 and Home Assistant's own maximum state length.
                    break;
            }
        }

        private static void AppendSensorFields(
            JsonWriter json,
            PropertyBase property,
            HomeAssistantSettings settings,
            string? deviceClass)
        {
            var dataType = property.DataType;

            if (dataType == DataType.Integer || dataType == DataType.Float)
            {
                json.String("unit_of_meas", property.Unit)
                    .String("dev_cla", deviceClass)
                    // Only a numeric sensor may carry a state class, and 'measurement' is
                    // what makes Home Assistant keep long-term statistics for it -- the
                    // difference between a value you can see now and one you can graph
                    // over a year. The device class has the last word; see
                    // DeviceClass.AcceptsMeasurementStateClass.
                    .String(
                        "stat_cla",
                        DeviceClass.AcceptsMeasurementStateClass(deviceClass) ? DeviceClass.MeasurementStateClass : null);
            }
            else if (dataType == DataType.Enum)
            {
                // Home Assistant's 'enum' device class is exactly an enum property: a
                // value from a declared set. It forbids a unit and a state class -- the
                // configuration is refused outright when either is present alongside
                // options -- which is why this branch is separate rather than two more
                // members above. An enum that declares no options has nothing to put in
                // the required key, so it stays an ordinary sensor showing its text.
                var options = OptionsOf(property);
                if (options != null)
                {
                    json.String("dev_cla", DeviceClass.Enumeration).StringArray("options", options.Values);
                }
            }

            // A string and a colour carry no unit, whatever the property declares. Home
            // Assistant reads a unit of measurement as a statement that the state is a
            // number, and then reports every reading of a non-numeric sensor as an error.

            if (settings.ExpireAfterSeconds > 0)
            {
                json.Int("exp_aft", settings.ExpireAfterSeconds);
            }
        }

        /// <remarks>
        /// A number is the one component whose configuration can silently swallow the
        /// device's readings. Home Assistant's number entity defaults to a range of 0 to
        /// 100 and **drops any state outside it** -- logged at error level, entity left
        /// at its previous value -- so a settable numeric property that declares no range
        /// gets its own values refused by the controller that is meant to display them. A
        /// property that declares one end only is the same failure on one side, with
        /// Home Assistant's default standing at the other.
        ///
        /// So a range is required here, where the v4 adapter is happy without one. That
        /// is not this adapter being stricter for its own sake: <c>min</c> and <c>max</c>
        /// are keys Home Assistant always has a value for, and the only question is
        /// whether the device or the default answers.
        /// </remarks>
        private static void AppendNumberFields(
            JsonWriter json,
            PropertyBase property,
            string? deviceClass,
            string stateTopic)
        {
            var range = RangeOf(property);

            if (range == null)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: a settable numeric property becomes a number entity, which always has a minimum and a maximum and refuses every value outside them -- so it must declare the range it accepts, or Home Assistant's own 0 to 100 silently becomes that range.");
            }

            if (!range.HasMinimum || !range.HasMaximum)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: a number entity has both a minimum and a maximum, and the property declares only one end -- Home Assistant's default would stand at the other and refuse values the property accepts.");
            }

            json.String("unit_of_meas", property.Unit)
                .String("dev_cla", deviceClass)
                .Raw("min", RenderNumber(property, range.Minimum, "range bound", stateTopic))
                .Raw("max", RenderNumber(property, range.Maximum, "range bound", stateTopic))
                .Raw("step", RenderStep(property, range, stateTopic));
        }

        /// <summary>
        /// The step a controller should move the value by, or null to leave Home
        /// Assistant's default of 1.
        /// </summary>
        /// <remarks>
        /// A declared step is published as declared. Without one, the step follows from
        /// what a float property actually renders -- two decimals is a step of 0.01 --
        /// because Home Assistant's default of 1 would otherwise make every fractional
        /// setpoint unreachable from its own controls.
        ///
        /// A derived step is capped at <see cref="SmallestStep"/>, which is the smallest
        /// Home Assistant's schema accepts. That caps the resolution of its controls and
        /// nothing else: every value they can then produce is one the property can hold,
        /// which is the direction that costs nothing. A *declared* step below that floor
        /// is refused instead -- it is a statement about the property that this
        /// convention cannot carry, and quietly widening it would advertise a granularity
        /// the device did not declare.
        /// </remarks>
        private static string? RenderStep(PropertyBase property, NumericRange range, string stateTopic)
        {
            if (range.HasStep)
            {
                if (range.Step < SmallestStep)
                {
                    throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: its number entity refuses a step below {SmallestStepText}, and the property declares a smaller one.");
                }

                return RenderNumber(property, range.Step, "step", stateTopic);
            }

            if (property is FloatProperty floatProperty)
            {
                if (floatProperty.Decimals <= 0)
                {
                    return null;
                }

                var places = floatProperty.Decimals > SmallestStepDecimals ? SmallestStepDecimals : floatProperty.Decimals;

                // Built rather than formatted: the property's own renderer would write
                // the capped step with the property's decimals ("0.0010"), which is the
                // same number said less clearly.
                var step = new StringBuilder("0.");
                for (int i = 1; i < places; i++)
                {
                    step.Append('0');
                }

                return step.Append('1').ToString();
            }

            // An integer property steps by one, which is also Home Assistant's default,
            // so there is nothing to say.
            return null;
        }

        /// <summary>
        /// Renders a number this adapter declares about a property -- a bound, a step --
        /// exactly the way that property writes its values.
        /// </summary>
        /// <remarks>
        /// As a JSON number, not a string, because that is what Home Assistant's schema
        /// coerces for <c>min</c>, <c>max</c> and <c>step</c>.
        ///
        /// Never <c>NumericRange.ToString()</c>, whose bounds go through "G" formatting:
        /// on this runtime that prints 21.5 as 21.499999999999999. A declared bound and
        /// the values published against it have to be written the same way, or a
        /// controller can refuse its own device's readings.
        ///
        /// A whole number renders as the whole number whatever the property's precision,
        /// which is what keeps a range of 0 to 100 going out as 0 and 100 for an integer
        /// and a float alike.
        /// </remarks>
        private static string RenderNumber(PropertyBase property, double value, string what, string stateTopic)
        {
            if (IsCastableToLong(value))
            {
                long truncated = (long)value;
                if (truncated == value)
                {
                    // Via long rather than the fixed-decimal formatter, so a negative
                    // zero bound comes out as "0" and a whole number carries no decimals.
                    return truncated.ToString();
                }
            }

            if (property is IntegerProperty)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: the {what} {value} is not a whole number, and an integer property's own payloads are -- so a controller offered it would send a value the property refuses.");
            }

            var floatProperty = (FloatProperty)property;

            if (value >= FloatProperty.MaxPublishableMagnitude || value <= -FloatProperty.MaxPublishableMagnitude)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: the {what} {value} has no fixed-decimal rendering that fits the runtime's format buffer.");
            }

            var rendered = floatProperty.FormatValue(value);

            // Asked by round-tripping the exact string about to be published, rather than
            // by comparing against a tolerance: a tolerance generous enough to absorb the
            // inexactness of scaling is also larger than the worst real rounding error
            // once the number is big enough, which makes the check vacuous exactly where
            // it is needed. double.TryParse here is the same parse a /set payload goes
            // through, so "the rendered number parses back to the declared one" is
            // literally "a controller echoing what was advertised is accepted".
            if (!double.TryParse(rendered, out var roundTripped) || roundTripped != value)
            {
                throw new ArgumentException($"Property '{stateTopic}' cannot be published to Home Assistant: the {what} {value} cannot be written exactly at the {floatProperty.Decimals} decimal places the property publishes, and a rounded one is a different {what}.");
            }

            return rendered;
        }

        /// <remarks>
        /// Checked before any cast, never after: the runtime's double-to-long conversion
        /// is an unchecked C cast, so an out-of-range value does not throw -- it hands
        /// back whatever the hardware produced, and the "is this a whole number" test
        /// would then be answering about a different number.
        /// </remarks>
        private static bool IsCastableToLong(double value) => value > -9.2e18 && value < 9.2e18;

        private static NumericRange? RangeOf(PropertyBase property)
        {
            if (property is FloatProperty floatProperty)
            {
                return floatProperty.Range;
            }

            if (property is IntegerProperty integerProperty)
            {
                return integerProperty.Range;
            }

            return null;
        }

        private static EnumOptions? OptionsOf(PropertyBase property)
            => property is EnumProperty enumProperty ? enumProperty.Options : null;

        /// <remarks>
        /// Falls back to the id when a name is not given. Home Assistant would otherwise
        /// name the entity after its platform -- "MQTT Sensor" -- which reads as a
        /// working entity belonging to nothing in particular, where the id at least says
        /// which property it is.
        /// </remarks>
        private static string EntityName(Node node, PropertyBase property, bool foldNodeName)
        {
            var name = NameOrId(property.Name, property.Id);

            return foldNodeName ? $"{NameOrId(node.Name, node.Id)} {name}" : name;
        }

        private static string NameOrId(string? name, string id)
            => name == null || name.Length == 0 ? id : name;

        private static string BuildDeviceBlock(Device device, HomeAssistantSettings settings) =>
            new JsonWriter()
                // The device's id is already unique among this installation's devices --
                // it is the topic level everything else hangs off -- so it is the natural
                // Home Assistant identifier, and it keeps the two views of one device
                // tied together for anyone reading a broker log.
                .String("ids", device.Id)
                .String("name", NameOrId(device.Name, device.Id))
                .String("mf", settings.Manufacturer)
                .String("mdl", settings.Model)
                .String("sw", settings.SoftwareVersion)
                .ToJson();

        private static string BuildOriginBlock(HomeAssistantSettings settings) =>
            new JsonWriter()
                .String("name", settings.OriginName)
                .String("sw", settings.OriginSoftwareVersion)
                .String("url", settings.OriginSupportUrl)
                .ToJson();

        /// <summary>Renders a list of units the way a failure message should read them.</summary>
        private static string Quote(string[] values)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('\'');
                builder.Append(values[i]);
                builder.Append('\'');
            }

            return builder.ToString();
        }
    }
}
