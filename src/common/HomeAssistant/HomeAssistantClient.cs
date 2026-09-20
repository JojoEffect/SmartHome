using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Properties;
using SmartHome.Mqtt;
using SmartHome.Protocol;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Text;
using System.Threading;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// Operates a device over Home Assistant's MQTT Discovery: announces its entities,
    /// publishes every value, says whether it is available, and carries a controller's
    /// commands back to it.
    /// </summary>
    /// <remarks>
    /// A peer of the Homie adapter rather than a passenger on it. It owns its MQTT
    /// session, and it has to: availability is a plain topic carrying <c>online</c> or
    /// <c>offline</c>, and the <c>offline</c> that matters -- the one published when a
    /// device drops off the network without saying so -- can only come from a last will,
    /// which can only be declared in CONNECT. An adapter handed a session somebody else
    /// opened has no will at all.
    ///
    /// Lifecycle transitions must go through this class, never through
    /// <c>Device.TryChangeState</c>: the model's transition table knows nothing about
    /// what this adapter publishes on the way, so a direct call moves the device without
    /// the wire following.
    /// </remarks>
    public class HomeAssistantClient : IDeviceProtocol
    {
        // M2Mqtt accepts a content type and user properties only on an MQTT 5.0 session
        // and throws NotSupportedException otherwise, so both stay empty rather than
        // unset. Same call the Homie adapter's publish settings make.
        private static readonly ArrayList NoUserProperties = new ArrayList();

        private readonly Device _device;
        private readonly IReconnectingMqttClient _mqttClient;
        private readonly HomeAssistantClientSettings _clientSettings;
        private readonly HomeAssistantDescription _description;
        private readonly ILogger _logger;
        private readonly IDictionary _settablePropertiesTable;

        // Derived once from the description, which cannot change after construction: every
        // settable property's command topic, plus Home Assistant's own birth topic. One
        // array rather than two subscriptions, so a device with nothing settable still
        // hears Home Assistant come back.
        private readonly string[] _subscribedTopics;
        private readonly MqttQoSLevel[] _subscribedQosLevels;

        // Guards the one path that publishes availability, and the one that publishes the
        // alert set. Everything they protect is a field read and an enqueue, and nothing
        // waits while holding either.
        private readonly object _availabilityLock = new();
        private readonly object _alertLock = new();

        // The last alert payloads actually put on the wire, which is what keeps a device
        // that re-raises the same alert from republishing it. Null means "nothing on this
        // session yet".
        private string? _lastPublishedAlertState;
        private string? _lastPublishedAlertAttributes;

        // The last availability payload actually put on the wire, which is what makes the
        // publish idempotent. Null means "nothing on this session yet", which is also
        // what an announce resets it to: the broker it is announcing into may never have
        // heard of this device.
        private string? _lastPublishedAvailability;

        // Where a re-announce lands once it has republished everything. Normally Ready,
        // but a device that was asleep when the broker went away has to come back to
        // that same state.
        private DeviceState _postInitState = DeviceState.Ready;

        // Whether this MQTT session has already been announced, so that a first connect
        // and a reconnect cannot both do it. Owning the fact rather than inferring it
        // from control flow is what makes the order of those two irrelevant.
        private bool _announcedThisSession;

        /// <exception cref="ArgumentException">
        /// The device holds a property Home Assistant cannot carry -- see
        /// <see cref="DiscoveryMapper"/>. Thrown here, when the device is built, rather
        /// than on the wire.
        /// </exception>
        public HomeAssistantClient(
            Device device,
            IReconnectingMqttClient mqttClient,
            HomeAssistantSettings? settings = null,
            HomeAssistantClientSettings? clientSettings = null)
        {
            _device = device;
            _mqttClient = mqttClient;
            _clientSettings = clientSettings ?? new HomeAssistantClientSettings();

            if (string.IsNullOrEmpty(_clientSettings.ClientId))
            {
                // Filled in here rather than defaulted on the settings type, so a caller
                // who passes settings for a username or a keep-alive cannot silently opt
                // out of the stable id.
                _clientSettings.ClientId = device.Id;
            }

            _logger = this.GetCurrentClassLogger();

            // The whole rendering happens here: every topic, every discovery payload, and
            // the refusal of anything this convention cannot carry. All of it is fixed
            // once the device is built, and an announce re-runs on every reconnect.
            _description = new HomeAssistantDescription(device, settings ?? new HomeAssistantSettings());

            _settablePropertiesTable = InitialiseSettablePropertiesTable(_description);

            _subscribedTopics = new string[_settablePropertiesTable.Count + 1];
            _settablePropertiesTable.Keys.CopyTo(_subscribedTopics, 0);

            // Last, so the command topics keep the table's own key order -- which is what
            // ends up in the SUBSCRIBE packet.
            _subscribedTopics[_subscribedTopics.Length - 1] = HomeAssistantTopics.DiscoveryStatusTopic;

            _subscribedQosLevels = new MqttQoSLevel[_subscribedTopics.Length];
            for (int i = 0; i < _subscribedQosLevels.Length; i++)
            {
                // At least once, matching what the discovery configuration tells Home
                // Assistant to publish commands at. A command dropped by the broker
                // leaves no trace at either end.
                _subscribedQosLevels[i] = MqttQoSLevel.AtLeastOnce;
            }
        }

        /// <inheritdoc />
        public string DeviceId => _device.Id;

        /// <inheritdoc />
        public DeviceState State => _device.State;

        /// <inheritdoc />
        public bool IsConnected => _mqttClient.IsConnected;

        /// <summary>
        /// What this device's availability topic currently says, or null when nothing has
        /// been published on this session.
        /// </summary>
        /// <remarks>
        /// A record of the wire, not of the model -- which is the useful question here,
        /// since availability is the whole of what Home Assistant knows about a device's
        /// lifecycle.
        /// </remarks>
        public string? Availability
        {
            get
            {
                lock (_availabilityLock)
                {
                    return _lastPublishedAvailability;
                }
            }
        }

        /// <inheritdoc />
        public event DeviceCommandHandler? OnCommand;

        /// <summary>
        /// Connects, announces the device's entities and their values, and reports itself
        /// available.
        /// </summary>
        /// <remarks>
        /// A session opened by somebody else -- an app calling
        /// <c>IReconnectingMqttClient.Connect(clientId)</c> first, say -- cannot carry
        /// this adapter's last will, so a device that died would stay reported as
        /// available forever. Such a session is replaced, not reused.
        /// </remarks>
        public bool Connect()
        {
            _logger.LogDebug("Connect...");

            try
            {
                // -= before += on each of these: Connect() is called in a retry loop, so
                // a failed attempt would otherwise leave a handler attached and the next
                // success would fire each one twice.
                _device.OnDeviceStateChange -= HandleDeviceStateChange;
                _device.OnDeviceStateChange += HandleDeviceStateChange;
                _device.OnAlertChange -= HandleAlertChange;
                _device.OnAlertChange += HandleAlertChange;

                // A new session is about to be opened; nothing is announced on it yet.
                _announcedThisSession = false;

                // Detached for the duration of this connect, so Connect() owns the
                // announce ordering outright. M2Mqtt raises ConnectionOpened synchronously
                // from CONNACK, so on a retry -- where these are still attached from the
                // previous attempt -- the reconnect handler would announce from inside the
                // CONNECT, before the command subscriptions exist.
                UnregisterConnectionChangeHandlers();

                if (_mqttClient.IsConnected)
                {
                    _logger.LogWarning("MQTT client was already connected; that session cannot carry this adapter's last will. Reconnecting it.");
                    _mqttClient.Disconnect();
                }

                ConnectInternal();

                if (!_mqttClient.IsConnected)
                {
                    _logger.LogError("Failed to connect: the MQTT client did not connect.");
                    return false;
                }

                // Safe to re-attach: CONNACK has been and gone, so from here these can
                // only fire for a genuine later reconnect.
                RegisterConnectionChangeHandlers();

                RegisterPropertyUpdateHandlers();
                SubscribeTopics();

                // Subscriptions before the announcement, so a controller reacting to a
                // freshly discovered entity cannot find the device deaf to its commands.
                if (!Announce(DeviceState.Ready))
                {
                    Disconnect();
                    _logger.LogError("Failed to connect: unable to announce the device after connecting. Disconnecting.");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "Failed to connect.");
                return false;
            }
        }

        /// <inheritdoc />
        public bool ConnectWithRetry(int maxAttempts = 10, int retryDelayMs = 3000)
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                _logger.LogInformation($"Connecting device '{_device.Id}' to Home Assistant (attempt {attempt}/{maxAttempts})...");

                if (Connect())
                {
                    return true;
                }

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }

            _logger.LogCritical($"Could not connect the device '{_device.Id}' after {maxAttempts} attempts.");
            return false;
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            _logger.LogDebug("Disconnect...");

            // The state change is best-effort. It exists to publish 'offline' while the
            // transport is still up, and the model refuses the transition from
            // 'disconnecting' and 'lost'. The teardown below must not hang off it: a
            // refused transition -- a second Disconnect(), or one cleaning up after a
            // failed Connect() -- would otherwise leave the session, the subscriptions
            // and the handlers all live.
            if (!_device.TryChangeState(DeviceState.Disconnecting))
            {
                _logger.LogWarning($"Could not publish '{HomeAssistantTopics.NotAvailable}' from state '{_device.State.GetName()}'; tearing the session down regardless.");
            }

            // Idempotent, and deliberately unconditional: on the success path the state
            // handler above has already run it.
            DisconnectInternal();

            UnregisterConnectionChangeHandlers();
        }

        /// <inheritdoc />
        public void Ready()
        {
            // Deliberately does not clear alerts. They are keyed, and only the code that
            // knows a condition is over can say so.
            if (!_device.TryChangeState(DeviceState.Ready))
            {
                _logger.LogWarning($"Refused to move to 'Ready' from state '{_device.State.GetName()}'.");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// A sleeping device stays *available*. Home Assistant has no lifecycle attribute
        /// to say anything finer, and a battery device that sleeps between readings is
        /// working exactly as intended -- greying its entities out on every wakeup cycle
        /// would be wrong every time. What makes a stopped device visible is
        /// <see cref="HomeAssistantSettings.ExpireAfterSeconds"/>, which is about the
        /// value going stale rather than about the device being gone.
        /// </remarks>
        public void Sleep()
        {
            if (!_device.TryChangeState(DeviceState.Sleeping))
            {
                _logger.LogWarning($"Refused to move to 'Sleeping' from state '{_device.State.GetName()}'.");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Raised alerts become one diagnostic entity: it is on while any is raised, and
        /// every id and message travels as its attributes. The device stays *available*
        /// throughout -- it is reachable and still publishing, and its other properties
        /// may be perfectly good.
        /// </remarks>
        public void RaiseAlert(string id, string message) => _device.RaiseAlert(id, message);

        /// <inheritdoc />
        public void ClearAlert(string id) => _device.ClearAlert(id);

        /// <summary>
        /// Withdraws every entity this device announced: an empty retained payload on
        /// each discovery topic.
        /// </summary>
        /// <remarks>
        /// The only way a discovered entity goes away. A discovery configuration outlives
        /// the device that published it -- it outlives a reflash, a rename and a broker
        /// restart -- so a property that is renamed or removed leaves a working-looking
        /// entity behind, wired to a topic nothing publishes to any more.
        ///
        /// An empty payload is also how MQTT deletes a retained message, so this clears
        /// the broker's store as well as the entity: the two are the same act.
        ///
        /// **Order matters when ids change.** This withdraws the entities the device
        /// announces *now*, so it has to run before the change that renames them; run
        /// afterwards, it withdraws the new ids and leaves the old entities standing with
        /// nothing to remove them.
        ///
        /// The values, the availability and the alert topics are deliberately left alone.
        /// They are this device's own state under its own root, which stays true whether
        /// or not Home Assistant is listening, and clearing them would make a withdrawal
        /// indistinguishable from a device that went away.
        /// </remarks>
        /// <returns>False when a publish failed, so a caller can retry.</returns>
        public bool Remove()
        {
            var removed = true;

            _logger.LogInformation($"Removing {_description.Configs.Length} Home Assistant entities for device '{_description.DeviceId}'.");

            for (int i = 0; i < _description.Configs.Length; i++)
            {
                var config = _description.Configs[i];

                try
                {
                    Publish(config.Topic, new byte[0], retained: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to withdraw the discovery configuration '{config.Topic}'.");
                    removed = false;
                }
            }

            return removed;
        }

        private void ConnectInternal()
        {
            // Logged, not acted on: Connect() decides on IsConnected, because M2Mqtt
            // signals some failures through this code and others by throwing.
            var reasonCode = _mqttClient.Connect(
                _clientSettings.ClientId,
                _clientSettings.UserName,
                _clientSettings.Password,
                // The will is this adapter's own and is not configurable: retained, so a
                // controller connecting after the device died still learns it is gone;
                // at least once, so the broker cannot drop the one message nobody can
                // republish.
                true,
                MqttQoSLevel.AtLeastOnce,
                true,
                _description.AvailabilityTopic,
                HomeAssistantTopics.NotAvailable,
                _clientSettings.CleanSession,
                _clientSettings.KeepAlivePeriod);

            _logger.LogDebug($"MQTT CONNECT returned '{reasonCode}'.");
        }

        private void DisconnectInternal()
        {
            _announcedThisSession = false;

            // Guarded because this runs twice on the normal path: once from the
            // state-change handler, and once from Disconnect() itself, which has to call
            // it unconditionally since the transition can be refused.
            if (_mqttClient.IsConnected)
            {
                UnsubscribeTopics();
            }

            UnregisterPropertyUpdateHandlers();
            _mqttClient.Disconnect();
            _device.OnDeviceStateChange -= HandleDeviceStateChange;
            _device.OnAlertChange -= HandleAlertChange;
        }

        private void HandleDeviceStateChange(DeviceStateChangeEventArgs args)
        {
            switch (args.CurrentState)
            {
                case DeviceState.Disconnecting:
                    PublishAvailability(HomeAssistantTopics.NotAvailable);
                    DisconnectInternal();
                    return;

                case DeviceState.Connecting:
                {
                    // Forget what this client last published before announcing, not
                    // after: the broker being announced into may have restarted with an
                    // empty retained store, so the closing 'online' has to go out even
                    // though nothing about the device changed.
                    RecordAvailabilityPublished(null);

                    PublishAnnouncement();

                    // Consume the target: a first connect, and any re-announce from
                    // Ready, both land on Ready.
                    var postInitState = _postInitState;
                    _postInitState = DeviceState.Ready;

                    if (!_device.TryChangeState(postInitState))
                    {
                        _logger.LogError($"Failed to move the device to '{postInitState.GetName()}' after announcing. Disconnecting.");
                        DisconnectInternal();
                    }

                    return;
                }

                case DeviceState.Ready:
                case DeviceState.Sleeping:
                    PublishAvailability(HomeAssistantTopics.Available);
                    return;

                case DeviceState.Lost:
                    // Never entered by the device itself: the broker publishes the will
                    // on its behalf, precisely because the device is in no position to.
                    return;
            }
        }

        /// <summary>
        /// The single path that publishes availability: publish the payload if it differs
        /// from the last one this session put on the wire.
        /// </summary>
        /// <remarks>
        /// One path, under one lock, because two threads reach it: the app's, through the
        /// lifecycle calls, and the MQTT client's, which runs a re-announce from inside
        /// CONNACK handling. Without the comparison, a device that goes to sleep and
        /// wakes republishes 'online' over 'online' in every retained store.
        ///
        /// The failure is swallowed and the record cleared rather than thrown: this is
        /// reached from M2Mqtt's dispatch thread on the re-announce paths, and that
        /// thread's catch-all does not merely log an escaping exception -- it treats one
        /// as a dead connection and tears the session down. Clearing the record is what
        /// lets the next transition try again rather than believing it already said this.
        /// </remarks>
        private void PublishAvailability(string payload)
        {
            lock (_availabilityLock)
            {
                if (_lastPublishedAvailability == payload)
                {
                    return;
                }

                _lastPublishedAvailability = payload;

                try
                {
                    Publish(_description.AvailabilityTopic, Encoding.UTF8.GetBytes(payload), retained: true);
                }
                catch (Exception ex)
                {
                    _lastPublishedAvailability = null;
                    _logger.LogError(ex, $"Failed to publish '{payload}' to '{_description.AvailabilityTopic}'.");
                }
            }
        }

        private void RecordAvailabilityPublished(string? payload)
        {
            lock (_availabilityLock)
            {
                _lastPublishedAvailability = payload;
            }
        }

        /// <summary>
        /// Publishes every discovery configuration, then every property's current value.
        /// </summary>
        /// <remarks>
        /// Configurations first, so that a Home Assistant subscribing to a state topic it
        /// has just learned about finds something there. Both are retained where they can
        /// be, which is what makes the order a courtesy rather than a requirement: a
        /// retained value is replayed whenever Home Assistant subscribes.
        ///
        /// **This must not throw.** It is reached from M2Mqtt's dispatch thread on every
        /// re-announce, where an escaping exception is read as a dead connection. One
        /// failed publish must not cost the others either: a QoS 1 publish raises
        /// MqttCommunicationException whenever the link is momentarily down, and the rest
        /// of the announcement is still worth attempting.
        /// </remarks>
        private bool PublishAnnouncement()
        {
            var published = true;

            _logger.LogInformation($"Announcing {_description.Configs.Length} Home Assistant entities for device '{_description.DeviceId}'.");

            for (int i = 0; i < _description.Configs.Length; i++)
            {
                var config = _description.Configs[i];

                try
                {
                    // Retained, always. A configuration that reaches only a Home Assistant
                    // which happens to be subscribed at that instant is, for a device that
                    // boots before the house does, a configuration nobody receives.
                    Publish(config.Topic, Encoding.UTF8.GetBytes(config.Payload), retained: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to publish the discovery configuration '{config.Topic}'.");
                    published = false;
                }
            }

            for (int i = 0; i < _description.Properties.Length; i++)
            {
                var described = _description.Properties[i];

                try
                {
                    // The property's own retained flag, not the announcement's: a
                    // momentary event must not be left in the store, while a state a
                    // controller wants on connect must.
                    Publish(described.StateTopic, described.Property.GetPayload(), described.Property.Retained);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to publish the value of '{described.StateTopic}'.");
                    published = false;
                }
            }

            // Forced, because this runs when the store on the other end may hold nothing
            // of ours. It is also what puts an alert raised *before* Connect() into the
            // announcement -- which is how a device that could not read its configuration
            // says so, and it has to be part of the announcement rather than an update
            // after it, or the device is briefly advertised as healthy.
            PublishAlerts(force: true);

            return published;
        }

        /// <summary>
        /// Publishes whether anything is wrong, and what.
        /// </summary>
        /// <remarks>
        /// Two topics, because Home Assistant reads a state and its attributes
        /// separately: a binary sensor's state must be exactly its on or off payload, so
        /// the ids and the messages cannot travel with it.
        ///
        /// Each is published only when it changed, which is what keeps a device that
        /// re-raises the same alert inside its measurement loop from republishing it
        /// every few seconds. The two move independently: a second alert raised while one
        /// is already up changes the attributes and not the state.
        ///
        /// Swallows and reports, like the availability publish, because this is reached
        /// from M2Mqtt's dispatch thread on the re-announce paths, where an escaping
        /// exception is treated as a dead connection.
        /// </remarks>
        private void PublishAlerts(bool force)
        {
            lock (_alertLock)
            {
                PublishAlertTopic(
                    _description.AlertStateTopic,
                    AlertPayloads.State(_device.HasAlerts),
                    ref _lastPublishedAlertState,
                    force);

                PublishAlertTopic(
                    _description.AlertAttributesTopic,
                    AlertPayloads.Attributes(_device.Alerts),
                    ref _lastPublishedAlertAttributes,
                    force);
            }
        }

        private void PublishAlertTopic(string topic, string payload, ref string? lastPublished, bool force)
        {
            if (!force && lastPublished == payload)
            {
                return;
            }

            lastPublished = payload;

            try
            {
                Publish(topic, Encoding.UTF8.GetBytes(payload), retained: true);
            }
            catch (Exception ex)
            {
                // Cleared, so the next change tries again rather than believing this one
                // is already on the wire.
                lastPublished = null;
                _logger.LogError(ex, $"Failed to publish '{topic}'.");
            }
        }

        /// <remarks>
        /// The model raises this only when the raised set actually changed -- re-raising
        /// an alert with the message it already carries is a no-op there -- so this can
        /// publish straight from it.
        /// </remarks>
        private void HandleAlertChange(AlertChangeEventArgs args) => PublishAlerts(force: false);

        // The single announce path, for both first connect and reconnect. Idempotent per
        // session: whichever of the two gets there first does the work, and the other is
        // a no-op, so neither has to know whether the other already ran.
        private bool Announce(DeviceState postInitState)
        {
            if (_announcedThisSession)
            {
                _logger.LogDebug("Already announced on this session; not repeating it.");
                return true;
            }

            // Armed together with the flag, so the target can only ever be set for an
            // announce that is actually going to run, and both before the transition,
            // because publishing re-enters this class through the state-change handler.
            _postInitState = postInitState;
            _announcedThisSession = true;

            if (_device.TryChangeState(DeviceState.Connecting))
            {
                return true;
            }

            _announcedThisSession = false;
            _postInitState = DeviceState.Ready;
            return false;
        }

        /// <remarks>
        /// The command topics and Home Assistant's birth topic in one SUBSCRIBE. The
        /// birth topic is why this is never skipped: a device with nothing settable still
        /// has to hear Home Assistant come back, or it stays discovered only for as long
        /// as that installation keeps its retained configurations.
        ///
        /// Through <c>IReconnectingMqttClient</c>, so both are cached and replayed after
        /// a reconnect without anything here re-subscribing.
        /// </remarks>
        private void SubscribeTopics()
        {
            _logger.LogDebug("Subscribing to command topics and the Home Assistant status topic...");

            _mqttClient.Subscribe(_subscribedTopics, _subscribedQosLevels);
            _mqttClient.MqttMsgPublishReceived -= HandleIncomingMessage;
            _mqttClient.MqttMsgPublishReceived += HandleIncomingMessage;
        }

        private void UnsubscribeTopics()
        {
            _logger.LogDebug("Unsubscribing from command topics and the Home Assistant status topic...");

            _mqttClient.Unsubscribe(_subscribedTopics);
            _mqttClient.MqttMsgPublishReceived -= HandleIncomingMessage;
        }

        private void HandleIncomingMessage(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;
            byte[] message = e.Message;

            if (topic == HomeAssistantTopics.DiscoveryStatusTopic)
            {
                HandleDiscoveryStatus(message);
                return;
            }

            if (!_settablePropertiesTable.Contains(topic))
            {
                return;
            }

            var property = (PropertyBase)_settablePropertiesTable[topic];

            // Everything below runs on M2Mqtt's dispatch thread, and that thread's
            // catch-all does not merely log an escaping exception -- it calls
            // OnConnectionClosing(), i.e. it treats one as a dead connection. Both calls
            // here can throw for reasons that are nothing of the sort: Set() reflects the
            // value back with a synchronous QoS 1 publish, which raises
            // MqttCommunicationException whenever the link is momentarily down, and
            // OnCommand runs arbitrary app code that typically publishes again.
            //
            // Guarded separately: a command that failed to apply is still a command the
            // app should hear about, since handlers act on the payload rather than on the
            // property's resulting value.
            //
            // A payload the property *rejected* is the one exception, and it is a
            // different thing from one that threw. A rejected payload never reached
            // anything -- it violates the property's own datatype or declared format, the
            // value did not move and nothing was published -- so raising OnCommand for it
            // would hand every actuator a payload the library has already refused.
            var accepted = true;
            try
            {
                // Set() reflects the value back onto the state topic through OnUpdate,
                // which is what both conventions expect. OnCommand is raised separately
                // so an app can tell a controller's command from its own update --
                // property.OnUpdate fires for both and cannot distinguish them.
                accepted = property.Set(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply the command on '{topic}'.");
            }

            if (!accepted)
            {
                // Set() has already logged what was wrong with the payload.
                return;
            }

            try
            {
                OnCommand?.Invoke(new DeviceCommandEventArgs(property, message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"An OnCommand handler threw for '{topic}'.");
            }
        }

        /// <summary>
        /// Home Assistant said it came up: announce everything again.
        /// </summary>
        /// <remarks>
        /// Retained configurations do not cover this on their own. They are replayed, but
        /// only once Home Assistant's MQTT integration has subscribed, and an
        /// installation that was reconfigured may not replay them at all -- which is why
        /// Home Assistant publishes a birth message precisely so devices can announce
        /// themselves again.
        ///
        /// Not routed through <c>Announce()</c>, deliberately: this session has already
        /// been announced, so that call would correctly do nothing. What has been lost is
        /// at the other end, not in the broker, so the publishes are repeated without
        /// taking the device back through its lifecycle -- a device does not re-enter
        /// 'connecting' because a consumer restarted.
        ///
        /// Runs on M2Mqtt's dispatch thread. PublishAnnouncement guards every publish,
        /// which is what keeps an exception from being read there as a dead connection.
        /// </remarks>
        private void HandleDiscoveryStatus(byte[] message)
        {
            var payload = message == null
                ? string.Empty
                : Encoding.UTF8.GetString(message, 0, message.Length).Trim();

            if (payload != HomeAssistantTopics.DiscoveryStatusOnline)
            {
                // The other payload is 'offline', Home Assistant's own will. It says the
                // consumer went away, not that this device did, and re-announcing into a
                // broker nobody is reading is just traffic.
                return;
            }

            _logger.LogInformation("Home Assistant came online; announcing again.");

            PublishAnnouncement();
        }

        private void HandleConnectionOpen(object sender, ConnectionOpenedEventArgs e)
        {
            // Only reconnects reach this handler: on the first connect the connection
            // change handlers are registered *after* the CONNECT, so that CONNACK has
            // already been and gone.
            _logger.LogInformation("MQTT connection reopened.");

            RegisterPropertyUpdateHandlers();

            // Both re-attached before the announce below, so an alert or a reading that
            // arrives while it is being written is published rather than dropped.
            // Unsubscribe first: this runs on every reconnect, and a double registration
            // would publish every change twice.
            _device.OnAlertChange -= HandleAlertChange;
            _device.OnAlertChange += HandleAlertChange;

            // Re-announce. The MQTT layer restores the session and replays the
            // subscriptions, but a discovery configuration lives in the BROKER's retained
            // store, and a broker that restarted has an empty one. Without this the
            // device goes on publishing values to topics no entity points at any more:
            // Home Assistant keeps the entities it already discovered and shows them as
            // unavailable, because the availability payload is gone too.
            //
            // Going back through Connecting republishes everything and returns to
            // whatever the device was in: Ready normally, but Sleeping is preserved -- a
            // broker restart is not a reason to wake a sleeping device.
            var postInitState = _device.State == DeviceState.Sleeping
                ? DeviceState.Sleeping
                : DeviceState.Ready;

            if (!Announce(postInitState))
            {
                _logger.LogError($"Reconnected but could not re-announce: state is '{_device.State.GetName()}'.");
            }
        }

        private void HandleConnectionClosed(object sender, System.EventArgs e)
        {
            _logger.LogInformation("MQTT connection closed handler called.");

            // The announcement lives in the broker's retained store, so it dies with the
            // session as far as this client can know. Clearing this is what lets the next
            // connection announce again.
            _announcedThisSession = false;

            UnregisterPropertyUpdateHandlers();

            // The alert handler comes off with them, and for the same reason: with no
            // session there is nowhere to publish, so an alert raised during an outage
            // would only produce a failed publish and an error line per change -- and a
            // device raising one from inside its measurement loop produces a change every
            // few seconds. Nothing is lost by staying quiet: the re-announce republishes
            // the alert set as it stands the moment the session comes back.
            _device.OnAlertChange -= HandleAlertChange;
        }

        private void RegisterConnectionChangeHandlers()
        {
            _logger.LogDebug("Registering connection change handlers...");

            _mqttClient.ConnectionClosed -= HandleConnectionClosed;
            _mqttClient.ConnectionOpened -= HandleConnectionOpen;
            _mqttClient.ConnectionClosed += HandleConnectionClosed;
            _mqttClient.ConnectionOpened += HandleConnectionOpen;
        }

        private void UnregisterConnectionChangeHandlers()
        {
            _logger.LogDebug("Unregistering connection change handlers...");

            _mqttClient.ConnectionClosed -= HandleConnectionClosed;
            _mqttClient.ConnectionOpened -= HandleConnectionOpen;
        }

        private void RegisterPropertyUpdateHandlers()
        {
            _logger.LogDebug("Registering property update handlers...");

            for (int i = 0; i < _description.Properties.Length; i++)
            {
                // Unsubscribe first: this runs again on every reconnect, and a double
                // registration would publish every property update twice.
                _description.Properties[i].Property.OnUpdate -= PublishPropertyUpdate;
                _description.Properties[i].Property.OnUpdate += PublishPropertyUpdate;
            }
        }

        private void UnregisterPropertyUpdateHandlers()
        {
            _logger.LogDebug("Unregistering property update handlers...");

            for (int i = 0; i < _description.Properties.Length; i++)
            {
                _description.Properties[i].Property.OnUpdate -= PublishPropertyUpdate;
            }
        }

        private void PublishPropertyUpdate(PropertyUpdateEventArgs args)
        {
            var property = args.Property;
            var topic = _description.TopicOf(property);

            if (topic == null)
            {
                // Only reachable if something outside the announced tree raised this,
                // which the registration above makes impossible -- worth a line rather
                // than a null topic on the wire.
                _logger.LogWarning($"Ignoring an update from property '{property.Id}', which is not part of the announced device.");
                return;
            }

            _logger.LogDebug($"Publishing '{topic}' -> '{Encoding.UTF8.GetString(args.Value, 0, args.Value.Length)}'");

            Publish(topic, args.Value, property.Retained);
        }

        private void Publish(string topic, byte[] payload, bool retained)
            => _mqttClient.Publish(topic, payload, string.Empty, NoUserProperties, MqttQoSLevel.AtLeastOnce, retained);

        private static IDictionary InitialiseSettablePropertiesTable(HomeAssistantDescription description)
        {
            var settableProperties = description.SettableProperties;
            var table = new Hashtable(settableProperties.Length);

            for (int i = 0; i < settableProperties.Length; i++)
            {
                // Keyed by the COMMAND topic, not the state topic. A device subscribed to
                // its own value topic would never receive a command and would re-consume
                // its own retained publishes as if a controller had sent them.
                //
                // Filled in the order the description walked the tree, which is not
                // cosmetic: the subscribe array is this table's own key order, so it is
                // what ends up in the SUBSCRIBE packet.
                table.Add(settableProperties[i].CommandTopic, settableProperties[i].Property);
            }

            return table;
        }
    }
}
