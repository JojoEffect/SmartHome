using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Extensions;
using SmartHome.Homie.V4.Properties;
using System.Collections;
using System.Text;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Turns a Homie v4 device model into Home Assistant MQTT discovery messages.
    /// </summary>
    /// <remarks>
    /// The whole point of this library, and the reason it is worth having rather than
    /// declaring Home Assistant entities by hand: a Homie property already declares its
    /// datatype, its unit, its permitted values and whether a controller may write to it,
    /// which is nearly everything a Home Assistant component config needs. So nothing is
    /// duplicated and nothing can drift -- add a property to the Homie model and Home
    /// Assistant gets an entity for it.
    ///
    /// The discovery messages point Home Assistant at the *Homie* topics. The device
    /// publishes no second copy of anything: <c>state_topic</c> is the property's own
    /// value topic, <c>command_topic</c> is the <c>/set</c> topic
    /// <see cref="HomieClient"/> already subscribes to, and availability is read off
    /// <c>$state</c>. The only new traffic on the wire is one retained config per
    /// property, published once per session.
    ///
    /// Single-component discovery, not the device-based form Home Assistant 2024.11
    /// added. Device-based discovery would be one message instead of N, but it puts every
    /// component in one payload under a <c>components</c> object, so the whole device's
    /// configuration has to be built and held in memory at once -- on a device where the
    /// reason this file hand-rolls a JSON writer is to avoid one more assembly. N small
    /// retained messages, each built and released in turn, is the cheaper shape here. It
    /// also keeps working on Home Assistant installations older than 2024.11.
    /// </remarks>
    public static class DiscoveryMapper
    {
        /// <summary>
        /// Maps <c>$state</c> onto Home Assistant's availability, as a Jinja template.
        /// </summary>
        /// <remarks>
        /// Home Assistant wants a topic carrying "online" or "offline"; Homie has a
        /// six-valued lifecycle. A template converts one to the other, which is better
        /// than the alternative of naming one payload as available and one as not: the
        /// other four states would then match neither, and Home Assistant leaves
        /// availability untouched on a payload it does not recognise -- so a device that
        /// went to <c>lost</c> via <c>alert</c> would still read as available.
        ///
        /// <c>ready</c> and <c>alert</c> are available because in both the device is
        /// connected and answering; <c>alert</c> only says something is wrong, and its
        /// other properties may be perfectly good. That is what
        /// <see cref="HomeAssistantSettings.ExpireAfterSeconds"/> is for -- an alerting
        /// device stops updating, and only an expiry can tell Home Assistant the value
        /// went stale. <c>sleeping</c> is available for the opposite reason: a battery
        /// device that sleeps between readings is working exactly as intended, and greying
        /// its entities out between wakeups would be wrong every time.
        ///
        /// <c>init</c>, <c>disconnected</c> and <c>lost</c> are unavailable. <c>lost</c>
        /// is the one that matters most: it is the Homie last will, so this is what makes
        /// a device that dropped off the network go unavailable in Home Assistant without
        /// a second last will on a second topic -- which MQTT could not have given us
        /// anyway, since a connection carries exactly one.
        /// </remarks>
        public const string AvailabilityTemplate =
            "{{ 'online' if value in ['ready','alert','sleeping'] else 'offline' }}";

        /// <summary>
        /// Builds one discovery message per property of <paramref name="device"/>.
        /// </summary>
        /// <param name="device">The Homie device to describe.</param>
        /// <param name="settings">Metadata Homie has no vocabulary for.</param>
        /// <param name="overrides">
        /// Property topic (<see cref="HomieEntityBase.GetTopic"/>) to
        /// <see cref="EntityOverride"/>, or null. Keyed by topic rather than by property
        /// reference so the key is the same string that appears on the wire and in a
        /// broker log.
        /// </param>
        public static DiscoveryEntity[] Map(Device device, HomeAssistantSettings settings, IDictionary? overrides = null)
        {
            var deviceJson = BuildDeviceBlock(device, settings);
            var originJson = BuildOriginBlock(settings);
            var availabilityTopic = $"{device.GetTopic()}{Constants.TopicSeparator}{Constants.StateAttributeTopicId}";

            var entities = new ArrayList();

            foreach (var node in device.Nodes)
            {
                foreach (var property in node.Properties)
                {
                    var propertyOverride = overrides == null
                        ? null
                        : (EntityOverride?)overrides[property.GetTopic()];

                    if (propertyOverride != null && propertyOverride.Excluded)
                    {
                        continue;
                    }

                    entities.Add(MapProperty(
                        device,
                        node,
                        property,
                        propertyOverride,
                        settings,
                        deviceJson,
                        originJson,
                        availabilityTopic));
                }
            }

            return (DiscoveryEntity[])entities.ToArray(typeof(DiscoveryEntity));
        }

        /// <summary>
        /// The Home Assistant component a property maps onto.
        /// </summary>
        /// <remarks>
        /// Settability is half the answer, not a detail: the same Homie datatype becomes a
        /// read-only entity or a control depending on it, and Home Assistant models those
        /// as different components rather than as one component with a flag. Getting this
        /// wrong is not cosmetic -- a <c>sensor</c> given a <c>command_topic</c> ignores
        /// it, so the control would simply not appear.
        ///
        /// Colour has no honest mapping yet. Home Assistant's <c>light</c> is a composite
        /// of brightness, colour mode and on/off across several topics, and a Homie colour
        /// property is one <c>r,g,b</c> string; pretending otherwise would produce a light
        /// that does not work. Until something maps a whole Homie node onto a light, a
        /// colour is reported as the string it is.
        ///
        /// A settable enum that declares no <c>$format</c> is the one case where
        /// settability alone does not decide it. <c>options</c> is a required key of the
        /// select schema and an empty list is rejected, so a select built from nothing is
        /// an entity that never appears at all. <c>EnumProperty</c> deliberately accepts
        /// any payload when <c>$format</c> is missing, and a free-text control is exactly
        /// that property.
        /// </remarks>
        private static string ComponentFor(PropertyBase property)
        {
            var settable = property.SettableAttribute.Value;

            return property.DataTypeAttribute.Value switch
            {
                DataType.Integer => settable ? Component.Number : Component.Sensor,
                DataType.Float => settable ? Component.Number : Component.Sensor,
                DataType.Boolean => settable ? Component.Switch : Component.BinarySensor,
                DataType.Enum => settable
                    ? (OptionsOf(property.FormatAttribute.Value).Length > 0 ? Component.Select : Component.Text)
                    : Component.Sensor,
                _ => settable ? Component.Text : Component.Sensor,
            };
        }

        private static DiscoveryEntity MapProperty(
            Device device,
            Node node,
            PropertyBase property,
            EntityOverride? propertyOverride,
            HomeAssistantSettings settings,
            string deviceJson,
            string originJson,
            string availabilityTopic)
        {
            var component = ComponentFor(property);
            var settable = property.SettableAttribute.Value;
            var dataType = property.DataTypeAttribute.Value;
            var stateTopic = property.GetTopic();

            // Home Assistant requires an object id of [a-zA-Z0-9_-], and a Homie topic id
            // is [a-z0-9-] by NamedHomieEntityBase.ValidateTopicId -- so joining three of
            // them with '-' cannot produce an invalid one, and nothing here has to
            // sanitize. The id spans all three levels because Home Assistant's namespace
            // is global: two devices with a 'sensor' node and a 'temperature' property are
            // ordinary, and would otherwise collide.
            var objectId = $"{device.TopicId}-{node.TopicId}-{property.TopicId}";

            var json = new JsonWriter()
                // Home Assistant prefixes this with the device name itself
                // (has_entity_name), so the property's own name is what belongs here --
                // "Temperature", not "Room sensor office temperature".
                .String("name", property.NameAttribute.Value)
                .String("uniq_id", objectId)
                .String("stat_t", stateTopic)
                .String("avty_t", availabilityTopic)
                .String("avty_tpl", AvailabilityTemplate);

            if (settable)
            {
                json.String("cmd_t", $"{stateTopic}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}");
            }

            AppendComponentFields(json, component, property, propertyOverride, settings, dataType);

            json.Raw("dev", deviceJson).Raw("o", originJson);

            return new DiscoveryEntity(
                HomeAssistantTopics.ConfigTopic(settings.DiscoveryPrefix, component, objectId),
                json.ToJson());
        }

        private static void AppendComponentFields(
            JsonWriter json,
            string component,
            PropertyBase property,
            EntityOverride? propertyOverride,
            HomeAssistantSettings settings,
            DataType dataType)
        {
            switch (component)
            {
                case Component.Sensor:
                    AppendSensorFields(json, property, propertyOverride, settings, dataType);
                    break;

                case Component.Number:
                    TryGetRange(property.FormatAttribute.Value, out var min, out var max);

                    json.String("unit_of_meas", property.UnitAttribute.Value.GetString())
                        .String("dev_cla", ResolveDeviceClass(property, propertyOverride))
                        .Raw("min", min)
                        .Raw("max", max)
                        .Raw("step", StepOf(property))
                        // "box" gives a typed entry field rather than a slider. A slider
                        // needs a range to be meaningful, and $format is optional in
                        // Homie -- so the control that works without one is the honest
                        // default.
                        .String("mode", "box");
                    break;

                case Component.BinarySensor:
                    // Homie v4 defines a boolean payload as exactly "true" or "false";
                    // Home Assistant defaults to "ON"/"OFF" and would read every payload
                    // as "off".
                    json.String("pl_on", "true")
                        .String("pl_off", "false")
                        // Binary-sensor device classes are a different vocabulary
                        // entirely (door, motion, moisture...) and nothing in a Homie
                        // declaration implies one, so only an explicit override sets it.
                        .String("dev_cla", propertyOverride?.DeviceClass);
                    break;

                case Component.Switch:
                    json.String("pl_on", "true")
                        .String("pl_off", "false")
                        .String("stat_on", "true")
                        .String("stat_off", "false")
                        .String("dev_cla", propertyOverride?.DeviceClass);
                    break;

                case Component.Select:
                    json.StringArray("options", OptionsOf(property.FormatAttribute.Value));
                    break;

                case Component.Text:
                    json.String("mode", "text");
                    break;
            }
        }

        private static void AppendSensorFields(
            JsonWriter json,
            PropertyBase property,
            EntityOverride? propertyOverride,
            HomeAssistantSettings settings,
            DataType dataType)
        {
            var numeric = dataType == DataType.Integer || dataType == DataType.Float;

            if (numeric)
            {
                var deviceClass = ResolveDeviceClass(property, propertyOverride);

                json.String("unit_of_meas", property.UnitAttribute.Value.GetString())
                    .String("dev_cla", deviceClass)
                    // Only a numeric sensor may carry a state class, and "measurement" is
                    // what makes Home Assistant keep long-term statistics for it -- the
                    // difference between a value you can see now and one you can graph
                    // over a year. Not every device class takes it, though, so the class
                    // has the last word: see DeviceClass.AcceptsMeasurementStateClass.
                    .String(
                        "stat_cla",
                        DeviceClass.AcceptsMeasurementStateClass(deviceClass) ? "measurement" : null);
            }
            else if (dataType == DataType.Enum)
            {
                // Home Assistant's 'enum' device class is exactly a Homie enum: a value
                // from a declared set. It forbids a unit and a state class, which is why
                // this branch is separate rather than a couple of extra members above.
                var options = OptionsOf(property.FormatAttribute.Value);
                if (options.Length > 0)
                {
                    json.String("dev_cla", "enum").StringArray("options", options);
                }
            }
            else
            {
                json.String("dev_cla", propertyOverride?.DeviceClass);
            }

            if (settings.ExpireAfterSeconds > 0)
            {
                json.Int("exp_aft", settings.ExpireAfterSeconds);
            }
        }

        private static string? ResolveDeviceClass(PropertyBase property, EntityOverride? propertyOverride) =>
            propertyOverride?.DeviceClass ?? DeviceClass.FromUnit(property.UnitAttribute.Value);

        /// <summary>
        /// The two ends of a Homie <c>min:max</c> <c>$format</c>, or false when there is
        /// no usable range.
        /// </summary>
        /// <remarks>
        /// Emitted raw, as JSON numbers, because that is what Home Assistant's schema
        /// expects for <c>min</c>/<c>max</c>/<c>step</c> -- and because the substring is
        /// already the literal Homie published, so re-parsing it into a double and
        /// re-formatting it could only lose precision or reintroduce the decimal-separator
        /// problem <c>FloatProperty.FormatValue</c> exists to solve.
        ///
        /// Read the way <c>PropertyBase.TryGetDeclaredRange</c> reads it, and that is the
        /// point: whatever range the property will actually enforce is the range Home
        /// Assistant has to offer. So both ends are trimmed (that helper trims too, and
        /// "5 : 30" is a range the device really does enforce), and the pair is taken or
        /// dropped together -- one end alone leaves Home Assistant's own default standing
        /// at the other, and its number schema rejects the whole config outright when the
        /// two cross. A <c>$format</c> the device itself declines to read as a range, such
        /// as a reversed "30:5", declares no range here either.
        ///
        /// Omitting both leaves Home Assistant's own 1..100 default, which is wrong for
        /// most setpoints -- so a settable numeric property really should declare a
        /// <c>$format</c>. That is a Homie-side fix, not something this mapper can invent.
        /// </remarks>
        private static bool TryGetRange(string? format, out string? min, out string? max)
        {
            min = null;
            max = null;

            if (format == null || format.Length == 0)
            {
                return false;
            }

            var parts = format.Split(':');
            if (parts.Length != 2)
            {
                return false;
            }

            var low = parts[0].Trim();
            var high = parts[1].Trim();

            // Anything that is not a bare number would be injected into the payload as a
            // raw JSON token and would corrupt it. Checked rather than trusted: $format is
            // a free string on the Homie side, and a malformed one is a device bug, not a
            // reason to publish invalid JSON.
            if (!IsJsonNumber(low) || !IsJsonNumber(high))
            {
                return false;
            }

            // Parsed as well as shape-checked, so the pair can be compared. Home Assistant
            // requires max > min and refuses the entity when it is not, which loses far
            // more than the range.
            if (!double.TryParse(low, out var lowValue)
                || !double.TryParse(high, out var highValue)
                || lowValue >= highValue)
            {
                return false;
            }

            min = low;
            max = high;
            return true;
        }

        /// <summary>
        /// Step for a numeric control: the smallest change the property can actually
        /// publish.
        /// </summary>
        /// <remarks>
        /// Home Assistant's default step is 1, which for a float setpoint means the user
        /// can only pick whole numbers. <see cref="FloatProperty.Decimals"/> already says
        /// what the property renders, so the step follows from it exactly -- two decimals
        /// is a step of 0.01. Built as a string rather than computed as a double for the
        /// same reason the range is passed through verbatim.
        /// </remarks>
        private static string? StepOf(PropertyBase property)
        {
            if (property is FloatProperty floatProperty)
            {
                if (floatProperty.Decimals <= 0)
                {
                    return "1";
                }

                var step = new StringBuilder("0.");
                for (int i = 1; i < floatProperty.Decimals; i++)
                {
                    step.Append('0');
                }

                return step.Append('1').ToString();
            }

            // An integer property steps by one, which is also Home Assistant's default --
            // so say nothing.
            return null;
        }

        /// <summary>
        /// The values of a Homie enum <c>$format</c>.
        /// </summary>
        /// <remarks>
        /// Trimmed, because <c>EnumProperty.Validate</c> trims each declared value
        /// before comparing it to a payload and this has to offer exactly the values that
        /// comparison will accept. Untrimmed, a <c>$format</c> written "eco, sport" -- a
        /// spelling that file explicitly supports -- put " sport" in Home Assistant's
        /// option list, and every attempt to select it came back to the device as a
        /// payload its own property then rejected.
        /// </remarks>
        private static string[] OptionsOf(string? format)
        {
            if (format == null || format.Length == 0)
            {
                return new string[0];
            }

            var options = format.Split(',');
            for (int i = 0; i < options.Length; i++)
            {
                options[i] = options[i].Trim();
            }

            return options;
        }

        /// <summary>
        /// Whether <paramref name="value"/> is a JSON number literal that can be written
        /// into a payload as-is.
        /// </summary>
        /// <remarks>
        /// Shape, not just "digits and dots". JSON requires a digit on both sides of the
        /// decimal point, so ".5", "1." and "1.2.3" are not numbers -- and a merely
        /// digit-counting check let all three through as raw tokens, which is a payload
        /// Home Assistant cannot parse at all. That costs the whole entity rather than
        /// just its range, and silently: the device sees a successful publish.
        ///
        /// Exponents are not accepted. JSON allows them, but no Homie <c>$format</c> in
        /// this codebase writes one and accepting a form that cannot be checked here as
        /// cheaply is not worth the surface.
        /// </remarks>
        private static bool IsJsonNumber(string value)
        {
            var index = 0;

            if (index < value.Length && value[index] == '-')
            {
                index++;
            }

            var integerDigits = 0;
            while (index < value.Length && IsDigit(value[index]))
            {
                index++;
                integerDigits++;
            }

            if (integerDigits == 0)
            {
                return false;
            }

            if (index == value.Length)
            {
                return true;
            }

            if (value[index] != '.')
            {
                return false;
            }

            index++;

            var fractionDigits = 0;
            while (index < value.Length && IsDigit(value[index]))
            {
                index++;
                fractionDigits++;
            }

            return fractionDigits > 0 && index == value.Length;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static string BuildDeviceBlock(Device device, HomeAssistantSettings settings) =>
            new JsonWriter()
                // The Homie device id is already globally unique among this installation's
                // devices -- it is the topic level everything else hangs off -- so it is
                // the natural Home Assistant identifier, and it keeps the two views of one
                // device tied together for anyone reading a broker log.
                .String("ids", device.TopicId)
                .String("name", device.NameAttribute.Value)
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
    }
}
