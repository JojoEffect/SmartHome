using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Properties;
using SmartHome.Homie.V4.Description;
using SmartHome.Homie.V4.Extensions;
using SmartHome.Homie.V4.Settings;
using SmartHome.Mqtt;
using SmartHome.Protocol;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Text;
using System.Threading;

namespace SmartHome.Homie.V4
{
    /// <summary>
    /// Publishes a model device as a Homie v4 device, and carries a controller's
    /// commands back to it.
    /// </summary>
    /// <remarks>
    /// This client owns its MQTT session, and it has to: v4 requires the connection to
    /// carry a last will setting <c>homie/[device-id]/$state</c> to <c>lost</c>, and a
    /// will can only be declared in CONNECT.
    ///
    /// Lifecycle transitions must go through this class -- never through
    /// <c>Device.TryChangeState</c> directly. See <see cref="IHomieClient"/>.
    /// </remarks>
    public class HomieClient : IHomieClient
    {
        private readonly Device _device;
        private readonly HomieClientSettings _homieClientSettings;
        private readonly HomiePublishSettings _homiePublishSettings;
        private readonly HomieLastWillSettings _homieLastWillSettings;
        private readonly HomieDescription _description;
        private readonly ILogger _logger;
        private readonly IReconnectingMqttClient _mqttClient;
        private readonly IDictionary _settablePropertiesTable;

        // Derived once from _settablePropertiesTable, which is readonly and never
        // mutated after construction. Subscribe and Unsubscribe each used to rebuild the
        // topic array, and Subscribe rebuilt an all-AtLeastOnce QoS array in a loop, on
        // every call -- four places to keep in step for a value fixed at construction.
        private readonly string[] _settableCommandTopics;
        private readonly MqttQoSLevel[] _settableQosLevels;

        // Guards the one path that publishes $state. Everything it protects is a few
        // field reads and an enqueue, and nothing waits while holding it.
        private readonly object _stateLock = new();

        // The last $state token actually put on the wire, which is what makes the
        // publish idempotent. It starts at 'disconnected' because that is what the
        // model's starting state maps to -- a device that has never connected is, as
        // far as anything can tell, not on the broker.
        private string _lastPublishedState = HomieStates.Disconnected;

        // Where a re-announce lands once it has republished everything. Connecting
        // normally leads to Ready, but a device that was asleep when the broker went
        // away has to come back to that same state. Alerts need no such preservation:
        // they are keyed and nothing here clears them, so an alerting device comes back
        // to Ready and the token is synthesised as 'alert' again.
        private DeviceState _postInitState = DeviceState.Ready;

        // Whether this MQTT session has already been announced. The announce used to be
        // triggered purely by control flow -- Connect() moved the device to Connecting,
        // and HandleConnectionOpen did the same -- which made correctness depend on the
        // two never both running. That held only because the connection-change handlers
        // were registered after ConnectInternal(), so the first CONNACK was missed by
        // accident. Making the client own the fact instead means the order of those two
        // no longer matters.
        private bool _announcedThisSession;

        /// <exception cref="ArgumentException">
        /// The device holds a property Homie v4 cannot express -- see
        /// <see cref="HomieDescription"/>. Thrown here, when the device is built, rather
        /// than on the wire.
        /// </exception>
        public HomieClient(Device device,
            IReconnectingMqttClient mqttClient,
            HomieClientSettings? deviceClientSettings = null,
            HomiePublishSettings? homiePublishSettings = null,
            HomieLastWillSettings? homieLastWillSettings = null,
            HomieDeviceSettings? homieDeviceSettings = null)
        {
            _device = device;
            _mqttClient = mqttClient;
            // Default the MQTT client id to the device's own id rather than a random
            // Guid. With a per-boot random id the broker keeps the dead session alive
            // until its keepalive expires, so the old session's 'lost' will is delivered
            // AFTER the rebooted device has already announced 'ready' -- leaving the
            // retained $state at 'lost' while the device is running. A stable id makes
            // the new connection take the session over instead, so the states stay
            // ordered. Homie doesn't prescribe a client id; it does prescribe one
            // connection per device.
            _homieClientSettings = deviceClientSettings ?? new HomieClientSettings();
            if (string.IsNullOrEmpty(_homieClientSettings.ClientId))
            {
                // Filled in here rather than defaulted on the settings type, so a caller
                // who passes settings for a username or keep-alive cannot silently opt
                // out of the stable id.
                _homieClientSettings.ClientId = device.Id;
            }
            _homiePublishSettings = homiePublishSettings ?? new HomiePublishSettings();
            _homieLastWillSettings = homieLastWillSettings ?? _device.CreateLastWillSettings();
            _logger = this.GetCurrentClassLogger();

            // The whole v4 rendering happens here: every topic, every attribute payload,
            // and the refusal of anything this convention cannot express. All of it is
            // fixed once the device is built, and an announce re-runs on every reconnect.
            _description = new HomieDescription(device, homieDeviceSettings ?? new HomieDeviceSettings());

            _settablePropertiesTable = InitializeSettablePropertiesTable(_description);

            _settableCommandTopics = new string[_settablePropertiesTable.Count];
            _settablePropertiesTable.Keys.CopyTo(_settableCommandTopics, 0);

            _settableQosLevels = new MqttQoSLevel[_settableCommandTopics.Length];
            for (int i = 0; i < _settableQosLevels.Length; i++)
            {
                _settableQosLevels[i] = MqttQoSLevel.AtLeastOnce;
            }
        }

        /// <inheritdoc />
        public string DeviceId => _device.Id;

        /// <inheritdoc />
        public DeviceState State => _device.State;

        /// <inheritdoc />
        public string HomieState => HomieStates.From(_device.State, _device.HasAlerts);

        /// <inheritdoc />
        public bool IsConnected => _mqttClient.IsConnected;

        /// <inheritdoc />
        public event DeviceCommandHandler? OnCommand;

        /// <summary>
        /// Connects the device and announces it, returning whether that succeeded.
        /// </summary>
        /// <remarks>
        /// A session opened by someone else -- for instance an app calling
        /// <c>IReconnectingMqttClient.Connect(clientId)</c> first -- cannot carry the
        /// last will, so continuing on it would leave the device permanently stuck at
        /// 'ready' from a controller's point of view whenever it dies abruptly. That is
        /// exactly what this code used to do. A foreign session is therefore replaced,
        /// not reused.
        /// </remarks>
        public bool Connect()
        {
            _logger.LogDebug("Connect...");

            try
            {
                // -= before += on every one of these: Connect() is called in a retry
                // loop by both device apps, and this runs before the attempt, so a
                // failed attempt would leave a handler attached and the next success
                // would fire each handler twice. For OnDeviceStateChange that was fatal:
                // the second invocation of the Connecting branch found the device already
                // 'ready', TryChangeState refused, and the failure path disconnected a
                // device that had just connected -- with auto-reconnect switched off.
                _device.OnDeviceStateChange -= HandleDeviceStateChange;
                _device.OnDeviceStateChange += HandleDeviceStateChange;
                _device.OnAlertChange -= HandleAlertChange;
                _device.OnAlertChange += HandleAlertChange;

                // A new session is about to be opened; nothing is announced on it yet.
                _announcedThisSession = false;

                // Detach for the duration of this connect so Connect() owns the announce
                // ordering outright. M2Mqtt raises ConnectionOpened synchronously from
                // CONNACK, so on a retry -- where these are still attached from the
                // previous attempt -- HandleConnectionOpen would announce from inside
                // ConnectInternal(), before the /set subscriptions exist.
                UnregisterConnectionChangeHandlers();

                if (_mqttClient.IsConnected)
                {
                    _logger.LogWarning("MQTT client was already connected; that session cannot carry the Homie last will. Reconnecting it.");
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
                SubscribeSettablePropertyTopics();

                // Subscriptions before the announcement, so a controller reacting to it
                // cannot find the device deaf to /set.
                //
                // Ready, not the device's current state: a first connect announces a
                // device that is starting up. An alert raised beforehand is not lost by
                // that -- the alert set is keyed and survives, so the closing $state
                // comes out as 'alert'.
                if (!Announce(DeviceState.Ready))
                {
                    Disconnect();
                    _logger.LogError("Failed to connect: unable to move the device to 'init' after connecting. Disconnecting.");
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
                _logger.LogInformation($"Connecting Homie device '{_device.Id}' (attempt {attempt}/{maxAttempts})...");

                if (Connect())
                {
                    return true;
                }

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }

            _logger.LogCritical($"Could not connect the Homie device '{_device.Id}' after {maxAttempts} attempts.");
            return false;
        }

        private void ConnectInternal()
        {
            // Logged, not acted on: Connect() decides on IsConnected, because M2Mqtt
            // signals some failures through this code and others by throwing. Assigning
            // it and never reading it implied an inspection that was not happening.
            var reasonCode = _mqttClient.Connect(
                                _homieClientSettings.ClientId,
                                _homieClientSettings.UserName,
                                _homieClientSettings.Password,
                                _homieLastWillSettings.WillRetain,
                                _homieLastWillSettings.WillQosLevel,
                                _homieLastWillSettings.WillFlag,
                                _homieLastWillSettings.WillTopic,
                                _homieLastWillSettings.WillMessage,
                                _homieClientSettings.CleanSession,
                                _homieClientSettings.KeepAlivePeriod
                                );

            _logger.LogDebug($"MQTT CONNECT returned '{reasonCode}'.");
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            _logger.LogDebug("Disconnect...");

            // The state change is best-effort. It exists to publish $state=disconnected,
            // which only lands while the transport is still up, and the model refuses
            // the transition from 'disconnecting' and 'lost'.
            //
            // The teardown must NOT hang off it. It used to: DisconnectInternal() was
            // reachable only through the state-change handler, so a refused transition --
            // a second Disconnect(), or one cleaning up after a failed Connect() -- left
            // the MQTT session, the /set subscriptions and the state handler all live
            // while this logged "Disconnected MQTT client anyways".
            if (!_device.TryChangeState(DeviceState.Disconnecting))
            {
                _logger.LogWarning($"Could not publish 'disconnected' from state '{HomieState}'; tearing the session down regardless.");
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
            // knows a condition is over can say so -- ClearAlert. Coming back to Ready
            // while one is still raised republishes 'alert', which is the truth.
            if (!_device.TryChangeState(DeviceState.Ready))
            {
                _logger.LogWarning($"Refused to move to '{HomieStates.Ready}' from state '{HomieState}'.");
            }
        }

        /// <inheritdoc />
        public void Sleep()
        {
            // Both halves under the lock, because they are one decision: v4 lets an
            // alerting device return to ready or disconnect, and nothing else, so a
            // check that has gone stale by the time the transition runs would put
            // 'sleeping' on the wire for a device the convention says may not sleep.
            // The old code had the same race unlocked; this hardens it rather than
            // preserving it. The lock is the one the state publisher takes, and a
            // monitor is re-entrant, so the publish this transition triggers is fine.
            lock (_stateLock)
            {
                if (HomieState == HomieStates.Alert)
                {
                    _logger.LogWarning($"Refused to move to '{HomieStates.Sleeping}': an alerting device may only return to '{HomieStates.Ready}' or disconnect. Clear the alert first.");
                    return;
                }

                if (!_device.TryChangeState(DeviceState.Sleeping))
                {
                    _logger.LogWarning($"Refused to move to '{HomieStates.Sleeping}' from state '{HomieState}'.");
                }
            }
        }

        /// <inheritdoc />
        public void RaiseAlert(string id, string message) => _device.RaiseAlert(id, message);

        /// <inheritdoc />
        public void ClearAlert(string id) => _device.ClearAlert(id);

        private void DisconnectInternal()
        {
            _announcedThisSession = false;

            // Guarded because this runs twice on the normal path: once from the
            // state-change handler, and once from Disconnect() itself, which has to call
            // it unconditionally since the transition can be refused. Without the guard
            // the second pass would issue a second UNSUBSCRIBE for topics already gone.
            if (_mqttClient.IsConnected)
            {
                UnsubscribeSettablePropertyTopics();
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
                    PublishStateIfChanged();
                    DisconnectInternal();
                    return;
                case DeviceState.Connecting:
                {
                    // 'init' is recorded as published before the announcement rather than
                    // after it: it goes out inside the device info below, and recording it
                    // first means the closing $state is always seen as a change and always
                    // published -- including when it is 'alert' because something was
                    // raised while the announcement was being written.
                    RecordStatePublished(HomieStates.Init);

                    _mqttClient.PublishHomieDeviceInfo(_description, _homiePublishSettings, _logger);

                    // Consume the target: a first connect, and any re-announce from
                    // Ready, both land on Ready.
                    var postInitState = _postInitState;
                    _postInitState = DeviceState.Ready;

                    if (!_device.TryChangeState(postInitState))
                    {
                        _logger.LogError($"Failed to move the device to '{postInitState.GetName()}' after publishing device info. Disconnecting.");
                        DisconnectInternal();
                    }
                    return;
                }
                case DeviceState.Ready:
                case DeviceState.Sleeping:
                    PublishStateIfChanged();
                    return;
                case DeviceState.Lost:
                    return;
            }
        }

        /// <remarks>
        /// The id and the message are dropped, and have to be: v4's <c>$state</c> carries
        /// the single token <c>alert</c> and has nowhere to put either. The model has
        /// already logged the raise; the line below says why that log is where the detail
        /// stops, so the loss is visible rather than silent.
        /// </remarks>
        private void HandleAlertChange(AlertChangeEventArgs args)
        {
            if (args.IsRaised)
            {
                _logger.LogInformation($"Alert '{args.AlertId}' and its message stay in the log: Homie v4 can say only that something is wrong, through $state.");
            }

            // Only when the token actually moved. A second alert, or a new message for
            // one already raised, changes nothing a v4 controller can see, and
            // republishing 'alert' over 'alert' would be noise in every retained store.
            PublishStateIfChanged();
        }

        /// <summary>
        /// The single path that publishes <c>$state</c>: work out the token the device
        /// is in now, and publish it if it differs from the last one that went out.
        /// </summary>
        /// <remarks>
        /// One path, under one lock, because two threads reach it. The app thread raises
        /// and clears alerts; the MQTT client's receive thread runs a re-announce from
        /// inside CONNACK handling -- while <c>IsConnected</c> is still false and before
        /// the dispatch thread starts, which is why nothing here gates on it. Without
        /// the comparison an alert raised mid-announce could publish 'alert' and then
        /// have the announce's own closing 'ready' land on top of it.
        ///
        /// <c>init</c> is never published from here. It means "a description is being
        /// written", and the only place that is true is inside the announcement, which
        /// publishes it itself.
        ///
        /// The token is recorded before the Publish call rather than after it: Publish
        /// enqueues and returns, throwing only for a full queue or a v5-only option, so
        /// "recorded but not sent" is not a state this can get stuck in -- while
        /// "published but not recorded" would republish the same token forever.
        /// </remarks>
        private void PublishStateIfChanged()
        {
            lock (_stateLock)
            {
                var token = HomieState;

                if (token == HomieStates.Init || token == _lastPublishedState)
                {
                    return;
                }

                _lastPublishedState = token;

                _mqttClient.PublishHomieAttribute(
                    _description.StateTopic,
                    Encoding.UTF8.GetBytes(token),
                    _homiePublishSettings.DeviceStatePublishSettings,
                    _logger);
            }
        }

        private void RecordStatePublished(string token)
        {
            lock (_stateLock)
            {
                _lastPublishedState = token;
            }
        }

        private void SubscribeSettablePropertyTopics()
        {
            _logger.LogDebug("Subscribing to settable property topics...");

            if (_settableCommandTopics.Length == 0)
            {
                _logger.LogDebug("No settable properties found. Skipping MQTT subscribe.");
                return;
            }

            _mqttClient.Subscribe(_settableCommandTopics, _settableQosLevels);
            _mqttClient.MqttMsgPublishReceived -= HandleIncomingMessage;
            _mqttClient.MqttMsgPublishReceived += HandleIncomingMessage;
        }

        private void UnsubscribeSettablePropertyTopics()
        {
            _logger.LogDebug("Unsubscribing from settable property topics...");

            if (_settableCommandTopics.Length == 0)
            {
                return;
            }

            _mqttClient.Unsubscribe(_settableCommandTopics);
            _mqttClient.MqttMsgPublishReceived -= HandleIncomingMessage;
        }

        private void HandleIncomingMessage(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;
            byte[] message = e.Message;

            if (!_settablePropertiesTable.Contains(topic))
            {
                return;
            }

            var property = (PropertyBase)_settablePropertiesTable[topic];

            // Everything below runs on M2Mqtt's dispatch thread, and that thread's
            // catch-all does not merely log an escaping exception -- it calls
            // OnConnectionClosing(), i.e. it treats one as a dead connection. Both calls
            // here can throw for reasons that are nothing of the sort: Set() reflects the
            // value back with a synchronous QoS-1 publish, which raises
            // MqttCommunicationException whenever the link is momentarily down, and
            // OnCommand runs arbitrary app code that typically publishes again. Unguarded,
            // a flaky link during a /set escalated into a full teardown, reconnect and
            // re-announce.
            //
            // Guarded separately: a command that failed to apply is still a command the
            // app should hear about, since handlers act on the payload rather than on the
            // property's resulting value.
            //
            // A payload the property *rejected* is the one exception, and it is a
            // different thing from one that threw. Set() only throws once it is applying
            // a payload it already accepted -- typically from the reflection publish on a
            // flaky link -- so that command did reach the device and the app should hear
            // about it. A rejected payload never reached anything: it violates the
            // property's own declared datatype or format, the value did not move and
            // nothing was published. Raising OnCommand for it would hand every actuator a
            // payload the library has already refused, which is how each of them ends up
            // re-checking the format its property already declares (issue #39).
            var accepted = true;
            try
            {
                // Set() reflects the value back to the property topic via OnUpdate, which
                // is what the spec asks for. OnCommand is raised separately so an app can
                // tell a controller's command from its own update -- property.OnUpdate
                // fires for both and cannot distinguish them.
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

        private void HandleConnectionOpen(object sender, ConnectionOpenedEventArgs e)
        {
            // Only reconnects reach this handler: on the first connect the connection
            // change handlers are registered *after* ConnectInternal, so that CONNACK
            // has already been and gone.
            _logger.LogInformation("MQTT connection reopened.");

            RegisterPropertyUpdateHandlers();

            // Re-announce. The MQTT layer restores the session (SmartHome.Mqtt replays
            // the subscriptions), but Homie state lives in the BROKER's retained store,
            // and a broker that restarted has an empty one. Without this the device
            // keeps publishing property values into a broker that has never heard of
            // it: no $homie, no $nodes, no $state, so a controller sees an unknown
            // device emitting values. The spec doesn't mandate this -- it says nothing
            // about reconnects at all -- but a device that only re-announces on reboot
            // is invisible after every broker restart.
            //
            // Going back through Connecting republishes everything and returns to
            // whatever the device was in: Ready normally, but Sleeping is preserved --
            // a broker restart is not a reason to wake a sleeping device. An alerting
            // one needs no preservation here: alerts are keyed, nothing clears them, and
            // the closing $state is synthesised from them again.
            var stateBeforeReannounce = _device.State;
            var postInitState = stateBeforeReannounce == DeviceState.Sleeping
                ? DeviceState.Sleeping
                : DeviceState.Ready;

            if (!Announce(postInitState))
            {
                _logger.LogError($"Reconnected but could not re-announce: state is '{HomieState}'.");
            }
        }

        private void HandleConnectionClosed(object sender, System.EventArgs e)
        {
            _logger.LogInformation("MQTT connection closed handler called.");

            // The announcement lives in the broker's retained store, so it dies with the
            // session. Clearing this is what lets the next connection announce again.
            _announcedThisSession = false;

            UnregisterPropertyUpdateHandlers();
        }

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
            // announce that is actually going to run. Setting it at the call site meant a
            // no-op Announce() left it primed, and the *next* announce -- possibly a
            // first connect that should land on 'ready' -- would consume a stale
            // 'sleeping'.
            //
            // Both set before the transition, not after: publishing device info re-enters
            // this class through the state-change handler, which reads them.
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

            foreach (var node in _device.Nodes)
            {
                foreach (var property in node.Properties)
                {
                    // Unsubscribe first: this runs again on every reconnect, and a
                    // double registration would publish every property update twice.
                    property.OnUpdate -= PublishPropertyUpdate;
                    property.OnUpdate += PublishPropertyUpdate;
                }
            }
        }

        private void UnregisterPropertyUpdateHandlers()
        {
            _logger.LogDebug("Unregistering property update handlers...");

            foreach (var node in _device.Nodes)
            {
                foreach (var property in node.Properties)
                {
                    property.OnUpdate -= PublishPropertyUpdate;
                }
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

            // No log line here: PublishHomiePropertyValue already logs topic and payload,
            // and this one decoded the same bytes a second time. Interpolated arguments
            // are built whether or not anything consumes them, so on RoomSensor's 5s cycle
            // that was a dozen throwaway strings per reading, forever.
            _mqttClient.PublishHomiePropertyValue(topic, args.Value, _homiePublishSettings.PropertyUpdatePublishSettings, property.Retained, _logger);
        }

        private static IDictionary InitializeSettablePropertiesTable(HomieDescription description)
        {
            var settableProperties = description.SettableProperties;

            var settablePropertiesTable = new Hashtable(settableProperties.Length);

            for (int i = 0; i < settableProperties.Length; i++)
            {
                // Keyed by the COMMAND topic, not the property topic. The spec puts
                // commands on homie/[device]/[node]/[property]/set and says the device
                // "must subscribe to this topic if the property is settable". Keying by
                // the property topic -- as this did until 2026-08-21 -- meant controller
                // commands were never received, and worse, the device subscribed to its
                // own retained value topic, so the broker replayed its own publishes
                // back at it and set it from them.
                //
                // Filled in the order Device.GetAllSettableProperties() walks the tree.
                // That is not cosmetic: the subscribe array is this table's own key
                // order, so it is what ends up in the SUBSCRIBE packet.
                settablePropertiesTable.Add(settableProperties[i].CommandTopic, settableProperties[i].Property);
            }

            return settablePropertiesTable;
        }
    }
}
