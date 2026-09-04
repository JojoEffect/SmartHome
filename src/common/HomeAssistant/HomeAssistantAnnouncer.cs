using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Properties;
using SmartHome.Mqtt;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Text;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Announces a Homie device to Home Assistant over the connection the device already
    /// has, and keeps it announced.
    /// </summary>
    /// <remarks>
    /// Deliberately not a client. It neither opens nor owns an MQTT session: it borrows
    /// the <see cref="IReconnectingMqttClient"/> that <see cref="HomieClient"/> connected,
    /// and publishes retained configuration onto it. That is the whole design. MQTT allows
    /// one last will per connection and Homie has already spent it on
    /// <c>homie/&lt;device&gt;/$state = lost</c>, so a second session would be the only way
    /// to give Home Assistant a will of its own -- two sessions, two client ids, two
    /// keep-alives and two sets of credentials, for a device that has one identity.
    /// <see cref="DiscoveryMapper.AvailabilityTemplate"/> is what makes that unnecessary:
    /// Home Assistant reads availability off the Homie will that is already there.
    ///
    /// Ordering: announce *after* <see cref="IHomieClient.Connect"/> has returned. Both
    /// orders work -- a retained value is replayed to Home Assistant whenever it
    /// subscribes -- but announcing first means Home Assistant subscribes to state topics
    /// that have nothing in them yet, and every entity reads "unknown" until the device's
    /// next publish.
    /// </remarks>
    public sealed class HomeAssistantAnnouncer
    {
        private readonly Device _device;
        private readonly IReconnectingMqttClient _mqttClient;
        private readonly HomeAssistantSettings _settings;
        private readonly IDictionary _overrides = new Hashtable();
        private readonly ILogger _logger;

        // Matches what HomiePublishExtensions passes: M2Mqtt only accepts a content type
        // and user properties on an MQTT 5.0 session and throws NotSupportedException
        // otherwise, so both stay empty rather than unset.
        private static readonly ArrayList NoUserProperties = new ArrayList();

        private bool _attached;

        public HomeAssistantAnnouncer(
            Device device,
            IReconnectingMqttClient mqttClient,
            HomeAssistantSettings? settings = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _mqttClient = mqttClient ?? throw new ArgumentNullException(nameof(mqttClient));
            _settings = settings ?? new HomeAssistantSettings();
            _logger = this.GetCurrentClassLogger();
        }

        /// <summary>
        /// Declares what a property measures, when its unit alone does not say.
        /// </summary>
        /// <remarks>
        /// Takes the property rather than its topic so a typo is a build error. See
        /// <see cref="DeviceClass.FromUnit"/> for when this is needed -- in short,
        /// whenever the unit is '%'.
        /// </remarks>
        public HomeAssistantAnnouncer SetDeviceClass(PropertyBase property, string deviceClass)
        {
            OverrideFor(property).DeviceClass = deviceClass;
            return this;
        }

        /// <summary>Keeps a property out of Home Assistant entirely.</summary>
        public HomeAssistantAnnouncer Exclude(PropertyBase property)
        {
            OverrideFor(property).Excluded = true;
            return this;
        }

        /// <summary>
        /// Starts keeping the announcement alive: re-announces on reconnect and when Home
        /// Assistant restarts.
        /// </summary>
        /// <remarks>
        /// Two independent things can lose the announcement, and neither is unusual.
        ///
        /// A broker restart empties the retained store, so the configs have to go out
        /// again -- the same reason <c>HomieClient.HandleConnectionOpen</c> re-announces
        /// the Homie tree, and this hooks the same event.
        ///
        /// A Home Assistant restart is the other, and retained configs alone do not cover
        /// it as neatly as they look: they are replayed, but only once Home Assistant's
        /// MQTT integration has subscribed, and an installation that has just been
        /// reconfigured may not replay them at all. Home Assistant publishes "online" to
        /// <c>homeassistant/status</c> when it comes up precisely so devices can announce
        /// themselves again, so this subscribes and does that.
        ///
        /// The subscription is made through <see cref="IReconnectingMqttClient"/>, so it
        /// is cached and replayed after a reconnect without anything here having to
        /// re-subscribe.
        ///
        /// This one may throw, where <see cref="Announce"/> deliberately never does. The
        /// difference is which thread they run on: Announce is reached from M2Mqtt's
        /// dispatch thread on both re-announce paths, and that thread treats an escaping
        /// exception as a dead connection, so it has to swallow and report. Attach is only
        /// ever called by the app, which is the only thing that can decide whether a
        /// device that cannot reach Home Assistant should still do its real job.
        /// </remarks>
        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            // Removed first, so a second Attach() after a Detach() cannot leave two
            // handlers publishing every config twice. Same defensive shape as
            // HomieClient.RegisterConnectionChangeHandlers.
            _mqttClient.MqttMsgPublishReceived -= HandleIncomingMessage;
            _mqttClient.MqttMsgPublishReceived += HandleIncomingMessage;
            _mqttClient.ConnectionOpened -= HandleConnectionOpened;
            _mqttClient.ConnectionOpened += HandleConnectionOpened;

            _mqttClient.Subscribe(
                new string[] { HomeAssistantTopics.StatusTopic },
                new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce });

            _attached = true;
        }

        /// <summary>Stops re-announcing. The published configuration stays where it is.</summary>
        /// <remarks>
        /// The handlers come off unconditionally, ahead of the flag check. Attach()
        /// registers them before it issues the SUBSCRIBE, and that call throws whenever
        /// the link is momentarily down -- which leaves the handlers live while _attached
        /// is still false. Guarding their removal on the flag made Detach() a silent
        /// no-op in exactly that case, and the announcer went on re-announcing after the
        /// app had asked it to stop. Removing a handler that was never added is a no-op,
        /// so the unguarded form is safe in the ordinary case too. The UNSUBSCRIBE stays
        /// guarded: there is nothing to undo when the SUBSCRIBE never got out.
        /// </remarks>
        public void Detach()
        {
            _mqttClient.MqttMsgPublishReceived -= HandleIncomingMessage;
            _mqttClient.ConnectionOpened -= HandleConnectionOpened;

            if (!_attached)
            {
                return;
            }

            _mqttClient.Unsubscribe(new string[] { HomeAssistantTopics.StatusTopic });

            _attached = false;
        }

        /// <summary>
        /// Publishes one retained discovery configuration per property.
        /// </summary>
        /// <returns>False when a publish failed, so a caller can retry.</returns>
        public bool Announce() => MapAndPublish(remove: false);

        /// <summary>
        /// Withdraws every entity: an empty retained payload on each discovery topic.
        /// </summary>
        /// <remarks>
        /// This is the only way a discovered entity goes away. A retained config outlives
        /// the device that published it -- outlives a reflash, a rename, a broker restart
        /// -- so a property that is renamed or removed from the Homie model leaves a
        /// working-looking Home Assistant entity behind, wired to a topic nothing publishes
        /// to any more. Call this before changing the model, or clear the topics by hand
        /// afterwards.
        ///
        /// An empty payload is also how MQTT deletes a retained message, so this clears
        /// the broker's store as well as the entity -- the two are the same act.
        /// </remarks>
        public bool Remove() => MapAndPublish(remove: true);

        private EntityOverride OverrideFor(PropertyBase property)
        {
            if (property == null)
            {
                throw new ArgumentNullException(nameof(property));
            }

            var topic = property.GetTopic();
            var existing = (EntityOverride?)_overrides[topic];
            if (existing != null)
            {
                return existing;
            }

            var created = new EntityOverride();
            _overrides[topic] = created;
            return created;
        }

        /// <summary>
        /// Builds the discovery messages and puts them on the wire, letting nothing out.
        /// </summary>
        /// <remarks>
        /// The mapping is guarded for the same reason each publish below is, and it is the
        /// same reason: both re-announce paths reach this from M2Mqtt's dispatch thread,
        /// whose catch-all does not merely log an escaping exception -- it treats one as a
        /// dead connection and tears the session down. Mapping is not a step that cannot
        /// fail: it allocates a JSON payload per property, on a device with tens of
        /// kilobytes to spare, so leaving it outside the guard left "Announce() never
        /// throws" true only by luck.
        /// </remarks>
        private bool MapAndPublish(bool remove)
        {
            DiscoveryEntity[] entities;

            try
            {
                entities = DiscoveryMapper.Map(_device, _settings, _overrides);

                _logger.LogInformation(remove
                    ? $"Removing {entities.Length} Home Assistant entities for device '{_device.TopicId}'."
                    : $"Announcing {entities.Length} Home Assistant entities for device '{_device.TopicId}'.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Could not build the Home Assistant discovery messages for device '{_device.TopicId}'.");
                return false;
            }

            return PublishAll(entities, remove);
        }

        private bool PublishAll(DiscoveryEntity[] entities, bool remove)
        {
            var published = true;

            foreach (DiscoveryEntity entity in entities)
            {
                var payload = remove ? new byte[0] : Encoding.UTF8.GetBytes(entity.Payload);

                try
                {
                    _mqttClient.Publish(
                        entity.Topic,
                        payload,
                        string.Empty,
                        NoUserProperties,
                        MqttQoSLevel.AtLeastOnce,
                        // Retained, always. An un-retained config reaches only a Home
                        // Assistant that happens to be subscribed at that instant, which
                        // for a device that boots before the house does is usually
                        // nobody.
                        true);
                }
                catch (Exception ex)
                {
                    // One failed config must not cost the others. A QoS-1 publish raises
                    // MqttCommunicationException whenever the link is momentarily down,
                    // and this runs on M2Mqtt's dispatch thread on the re-announce paths,
                    // where that thread's catch-all treats an escaping exception as a dead
                    // connection and tears the session down -- see the same reasoning in
                    // HomieClient.HandleIncomingMessage.
                    _logger.LogError(ex, $"Failed to publish the Home Assistant discovery config '{entity.Topic}'.");
                    published = false;
                }
            }

            return published;
        }

        private void HandleConnectionOpened(object sender, ConnectionOpenedEventArgs e)
        {
            // Only reconnects reach this: Attach() runs after HomieClient.Connect(), so
            // the first CONNACK is long past by the time the handler is registered.
            _logger.LogInformation("MQTT connection reopened; re-announcing to Home Assistant.");
            Announce();
        }

        private void HandleIncomingMessage(object sender, MqttMsgPublishEventArgs e)
        {
            // This client is shared with HomieClient, which has its own handler on the
            // same event, so both see every message and each filters for its own topics.
            if (e.Topic != HomeAssistantTopics.StatusTopic)
            {
                return;
            }

            var payload = e.Message == null
                ? string.Empty
                : Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();

            if (payload != HomeAssistantTopics.StatusOnline)
            {
                // The other payload is "offline", Home Assistant's own will. Nothing to
                // do: it says the consumer went away, not that this device did.
                return;
            }

            _logger.LogInformation("Home Assistant came online; re-announcing.");
            Announce();
        }
    }
}
