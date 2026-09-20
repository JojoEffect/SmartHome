using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Every topic this adapter owns: where a value goes, where a command arrives, where
    /// availability is published, and where Home Assistant looks for a discovery config.
    /// </summary>
    /// <remarks>
    /// The model has no <c>GetTopic()</c>, by design, so each adapter names the tree its
    /// own way. This one names it under its own root and publishes every value itself --
    /// it does not point Home Assistant at another convention's topics. An earlier
    /// attempt did exactly that (its state topic was the Homie property topic and its
    /// availability a template over Homie's <c>$state</c>), which made Home Assistant a
    /// passenger on a convention it does not speak: the two could not be chosen between,
    /// only stacked.
    ///
    /// Home Assistant has no node level, so the three ids of the tree are flattened into
    /// one entity id here. That is the adapter's problem precisely because the node is
    /// real in the model.
    /// </remarks>
    public static class HomeAssistantTopics
    {
        /// <summary>
        /// The root every state and command topic hangs off.
        /// </summary>
        /// <remarks>
        /// Neutral rather than named after a convention: Home Assistant places no
        /// requirement on where state lives, and keeping <c>homie/</c> meaning Homie is
        /// what lets the Homie conformance run stay a statement about Homie.
        /// </remarks>
        public const string Root = "smarthome";

        public const string Separator = "/";

        /// <summary>Where a controller writes to a settable property.</summary>
        public const string SetTopicId = "set";

        /// <summary>The device-level topic carrying <c>online</c> or <c>offline</c>.</summary>
        /// <remarks>
        /// Three levels (<c>smarthome/&lt;device&gt;/status</c>) where a property's value
        /// topic has four, so it cannot collide with one even on a device whose node is
        /// called <c>status</c>. The same holds for the two alert topics below.
        /// </remarks>
        public const string StatusTopicId = "status";

        /// <summary>
        /// The device-level topic carrying <c>ON</c> while any alert is raised.
        /// </summary>
        public const string ProblemTopicId = "problem";

        /// <summary>
        /// The device-level topic carrying the raised alerts as a JSON object, which
        /// Home Assistant attaches to the problem entity as its attributes.
        /// </summary>
        public const string AlertsTopicId = "alerts";

        /// <summary>
        /// The binary payloads, which are Home Assistant's own defaults for
        /// <c>payload_on</c> and <c>payload_off</c> on a binary sensor.
        /// </summary>
        /// <remarks>
        /// Upper case, unlike a boolean property's <c>true</c>/<c>false</c>: these are
        /// this adapter's own payloads on a topic no property owns, so there is nothing
        /// to agree with but Home Assistant's defaults.
        /// </remarks>
        public const string On = "ON";

        /// <inheritdoc cref="On"/>
        public const string Off = "OFF";

        /// <summary>
        /// Where Home Assistant publishes its own birth (<c>online</c>) and will
        /// (<c>offline</c>).
        /// </summary>
        /// <remarks>
        /// Not derived from the discovery prefix. Home Assistant's birth topic is
        /// configured separately from it and defaults to this literal, so an installation
        /// that moved the discovery prefix has almost certainly not moved this.
        /// </remarks>
        public const string DiscoveryStatusTopic = "homeassistant/status";

        /// <summary>The payload Home Assistant's birth message carries.</summary>
        public const string DiscoveryStatusOnline = "online";

        /// <summary>
        /// The availability payloads, which are Home Assistant's own defaults for
        /// <c>payload_available</c> and <c>payload_not_available</c>.
        /// </summary>
        /// <remarks>
        /// Spelled exactly as <c>DEFAULT_PAYLOAD_AVAILABLE</c> and
        /// <c>DEFAULT_PAYLOAD_NOT_AVAILABLE</c> in <c>homeassistant/components/mqtt/const.py</c>
        /// (read at tag 2026.9.3), which is why no discovery config has to declare either:
        /// a payload that matches neither leaves availability untouched, so agreeing with
        /// the defaults is what makes the plain topic work without a template.
        /// </remarks>
        public const string Available = "online";

        /// <inheritdoc cref="Available"/>
        public const string NotAvailable = "offline";

        /// <summary>
        /// What joins the device, node and property ids into one entity id.
        /// </summary>
        /// <remarks>
        /// An underscore, and the choice is load-bearing rather than cosmetic. A model id
        /// may contain a hyphen, so joining three of them with a hyphen is ambiguous:
        /// node <c>tank-level</c> + property <c>litres</c> and node <c>tank</c> +
        /// property <c>level-litres</c> produce the same string, and both configs are
        /// published retained to the same discovery topic -- the second overwrites the
        /// first, one entity silently disappears, and the survivor points at whichever
        /// state topic went out last. <c>NamedEntityBase.ValidateId</c> permits only
        /// lowercase a-z, digits and the hyphen, so an underscore cannot appear inside an
        /// id and the join stays unambiguous; Home Assistant's own object id rule
        /// (<c>[a-zA-Z0-9_-]</c>, <c>TOPIC_MATCHER</c> in
        /// <c>homeassistant/components/mqtt/discovery.py</c>) allows it.
        /// </remarks>
        public const string IdSeparator = "_";

        /// <summary>
        /// Home Assistant's default discovery prefix. Configurable there, hence
        /// <see cref="HomeAssistantSettings.DiscoveryPrefix"/>.
        /// </summary>
        public const string DefaultDiscoveryPrefix = "homeassistant";

        /// <summary>The last level of a discovery topic.</summary>
        public const string ConfigTopicId = "config";

        /// <summary>
        /// The entity's own topic:
        /// <c>smarthome/&lt;device&gt;[/&lt;node&gt;[/&lt;property&gt;]]</c>.
        /// </summary>
        /// <remarks>
        /// Walks <see cref="EntityBase.Parent"/>, which is the only way to recover the
        /// path now that no entity carries one. Nothing is memoised: an adapter calls
        /// this once per entity, on a tree that can no longer change, and caches the
        /// result itself.
        /// </remarks>
        public static string Of(EntityBase entity)
            => entity.Parent == null
                ? $"{Root}{Separator}{entity.Id}"
                : $"{Of(entity.Parent)}{Separator}{entity.Id}";

        /// <summary>
        /// Where a controller writes to a settable property:
        /// <c>smarthome/&lt;device&gt;/&lt;node&gt;/&lt;property&gt;/set</c>.
        /// </summary>
        /// <remarks>
        /// A separate topic from the value, so that a device cannot re-consume its own
        /// retained publishes as if a controller had sent them.
        /// </remarks>
        public static string Command(PropertyBase property)
            => $"{Of(property)}{Separator}{SetTopicId}";

        /// <summary>
        /// The device's availability topic: <c>smarthome/&lt;device&gt;/status</c>.
        /// </summary>
        /// <remarks>
        /// One per device rather than one per entity, and it is also the topic this
        /// adapter's last will addresses -- which is the whole reason it owns its MQTT
        /// session.
        /// </remarks>
        public static string Availability(Device device)
            => $"{Of(device)}{Separator}{StatusTopicId}";

        /// <summary>
        /// Where the device says whether anything is wrong:
        /// <c>smarthome/&lt;device&gt;/problem</c>.
        /// </summary>
        public static string Problem(Device device)
            => $"{Of(device)}{Separator}{ProblemTopicId}";

        /// <summary>
        /// Where the raised alerts go, as a JSON object:
        /// <c>smarthome/&lt;device&gt;/alerts</c>.
        /// </summary>
        /// <remarks>
        /// A separate topic from the one above because Home Assistant reads a state and
        /// its attributes from separate topics: a binary sensor's state must be exactly
        /// its on or off payload, so the ids and messages cannot travel with it.
        /// </remarks>
        public static string Alerts(Device device)
            => $"{Of(device)}{Separator}{AlertsTopicId}";

        /// <summary>
        /// The entity id of a property: <c>&lt;device&gt;_&lt;node&gt;_&lt;property&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Spans all three levels because Home Assistant's namespace is global: two
        /// devices with a <c>sensor</c> node and a <c>temperature</c> property are
        /// ordinary and must not collide. See <see cref="IdSeparator"/> for why they are
        /// joined the way they are.
        /// </remarks>
        public static string ObjectId(EntityBase entity)
            => entity.Parent == null
                ? entity.Id
                : $"{ObjectId(entity.Parent)}{IdSeparator}{entity.Id}";

        /// <summary>
        /// The entity id of something the device has that no property does:
        /// <c>&lt;device&gt;_&lt;name&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Cannot collide with a property's id whatever <paramref name="name"/> is: a
        /// property's spans three levels and so carries exactly two separators, and this
        /// one carries a single separator. A device with a node called <c>problem</c> is
        /// therefore no problem.
        /// </remarks>
        public static string DeviceObjectId(Device device, string name)
            => $"{device.Id}{IdSeparator}{name}";

        /// <summary>
        /// <c>&lt;prefix&gt;/&lt;component&gt;/&lt;object-id&gt;/config</c>: the
        /// single-component discovery topic.
        /// </summary>
        /// <remarks>
        /// The optional <c>&lt;node-id&gt;</c> level is deliberately unused. Home
        /// Assistant's guidance is that an entity carrying a <c>unique_id</c> puts that
        /// id in the object id and omits the node level, and the id built above already
        /// spans device, node and property -- so the extra level would only repeat part
        /// of it.
        ///
        /// Single-component discovery rather than the device-based form added in Home
        /// Assistant 2024.11: that one puts every component in a single payload, so the
        /// whole device's configuration has to be built and held at once, on a device
        /// where this library hand-rolls a JSON writer to avoid one more assembly. It
        /// also keeps working on installations older than 2024.11.
        /// </remarks>
        public static string ConfigTopic(string discoveryPrefix, string component, string objectId)
            => $"{discoveryPrefix}{Separator}{component}{Separator}{objectId}{Separator}{ConfigTopicId}";
    }
}
