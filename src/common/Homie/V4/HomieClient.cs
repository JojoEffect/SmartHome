using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using SmartHome.Homie.V4.Extensions;
using SmartHome.Homie.V4.Properties;
using SmartHome.Homie.V4.Settings;
using SmartHome.Mqtt;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Threading;

namespace SmartHome.Homie.V4
{
    public class HomieClient : IHomieClient
    {
        private readonly Device _device;
        private readonly HomieClientSettings _homieClientSettings;
        private readonly HomiePublishSettings _homiePublishSettings;
        private readonly HomieLastWillSettings _homieLastWillSettings;
        private readonly ILogger _logger;
        private readonly IReconnectingMqttClient _mqttClient;
        private readonly IDictionary _settablePropertiesTable;

        // Derived once from _settablePropertiesTable, which is readonly and never
        // mutated after construction. Subscribe and Unsubscribe each used to rebuild the
        // topic array, and Subscribe rebuilt an all-AtLeastOnce QoS array in a loop, on
        // every call -- four places to keep in step for a value fixed at construction.
        private readonly string[] _settableCommandTopics;
        private readonly MqttQoSLevel[] _settableQosLevels;

        // Where a re-announce lands once it has republished everything. Init normally
        // leads to Ready, but a device that was alerting or asleep when the broker went
        // away has to come back to that same state -- otherwise surviving a broker
        // restart would silently clear an alert nobody has resolved.
        private State _postInitState = State.Ready;

        // Whether this MQTT session has already been announced. The announce used to be
        // triggered purely by control flow -- Connect() called TryChangeState(Init), and
        // HandleConnectionOpen did the same -- which made correctness depend on the two
        // never both running. That held only because the connection-change handlers were
        // registered after ConnectInternal(), so the first CONNACK was missed by
        // accident. Making the client own the fact instead means the order of those two
        // no longer matters.
        private bool _announcedThisSession;

        public HomieClient(Device device,
            IReconnectingMqttClient mqttClient,
            HomieClientSettings? deviceClientSettings = null,
            HomiePublishSettings? homiePublishSettings = null,
            HomieLastWillSettings? homieLastWillSettings = null)
        {
            _device = device;
            _mqttClient = mqttClient;
            // Default the MQTT client id to the device's own topic id rather than a
            // random Guid. With a per-boot random id the broker keeps the dead session
            // alive until its keepalive expires, so the old session's 'lost' will is
            // delivered AFTER the rebooted device has already announced 'ready' --
            // leaving the retained $state at 'lost' while the device is running. A
            // stable id makes the new connection take the session over instead, so the
            // states stay ordered. Homie doesn't prescribe a client id; it does
            // prescribe one connection per device.
            _homieClientSettings = deviceClientSettings ?? new HomieClientSettings();
            if (string.IsNullOrEmpty(_homieClientSettings.ClientId))
            {
                // Filled in here rather than defaulted on the settings type, so a caller
                // who passes settings for a username or keep-alive cannot silently opt
                // out of the stable id.
                _homieClientSettings.ClientId = device.TopicId;
            }
            _homiePublishSettings = homiePublishSettings ?? new HomiePublishSettings();
            _homieLastWillSettings = homieLastWillSettings ?? _device.CreateLastWillSettings();
            _logger = this.GetCurrentClassLogger();
            _settablePropertiesTable = InitializeSettablePropertiesTable(device);

            _settableCommandTopics = new string[_settablePropertiesTable.Count];
            _settablePropertiesTable.Keys.CopyTo(_settableCommandTopics, 0);

            _settableQosLevels = new MqttQoSLevel[_settableCommandTopics.Length];
            for (int i = 0; i < _settableQosLevels.Length; i++)
            {
                _settableQosLevels[i] = MqttQoSLevel.AtLeastOnce;
            }
        }

        /// <inheritdoc />
        public string DeviceId => _device.TopicId;

        /// <inheritdoc />
        public State State => _device.StateAttribute.Value;

        /// <inheritdoc />
        public bool IsConnected => _mqttClient.IsConnected;

        /// <inheritdoc />
        public event HomieCommandHandler? OnCommand;

        /// <summary>
        /// Connects the device and announces it, returning whether that succeeded.
        /// </summary>
        /// <remarks>
        /// This client owns the MQTT session, and it has to: Homie v4 requires the
        /// connection to carry a last will setting <c>homie/[device-id]/$state</c> to
        /// <c>lost</c>, and a will can only be declared in CONNECT. A session opened
        /// by someone else -- for instance an app calling
        /// <c>IReconnectingMqttClient.Connect(clientId)</c> first -- cannot have that
        /// will, so continuing on it would leave the device permanently stuck at
        /// 'ready' from a controller's point of view whenever it dies abruptly. That
        /// is exactly what this code used to do. A foreign session is therefore
        /// replaced, not reused.
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
                // the second invocation of the Init branch found the device already
                // 'ready', TryChangeState refused, and the failure path disconnected a
                // device that had just connected -- with auto-reconnect switched off.
                _device.OnDeviceStateChange -= HandleDeviceStateChange;
                _device.OnDeviceStateChange += HandleDeviceStateChange;

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
                if (!Announce(State.Ready))
                {
                    Disconnect();
                    _logger.LogError("Failed to connect: unable to change device state to 'init' after connecting. Disconnecting.");
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
                _logger.LogInformation($"Connecting Homie device '{_device.TopicId}' (attempt {attempt}/{maxAttempts})...");

                if (Connect())
                {
                    return true;
                }

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }

            _logger.LogCritical($"Could not connect the Homie device '{_device.TopicId}' after {maxAttempts} attempts.");
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

        public void Disconnect()
        {
            _logger.LogDebug("Disconnect...");

            // The state change is best-effort. It exists to publish $state=disconnected,
            // which only lands while the transport is still up, and CanChangeState
            // refuses it from 'disconnected' and 'lost'.
            //
            // The teardown must NOT hang off it. It used to: DisconnectInternal() was
            // reachable only through the state-change handler, so a refused transition --
            // a second Disconnect(), or one cleaning up after a failed Connect() -- left
            // the MQTT session, the /set subscriptions and the state handler all live
            // while this logged "Disconnected MQTT client anyways".
            if (!_device.TryChangeState(State.Disconnected))
            {
                _logger.LogWarning($"Could not publish 'disconnected' from state '{_device.StateAttribute.Value.GetString()}'; tearing the session down regardless.");
            }

            // Idempotent, and deliberately unconditional: on the success path the state
            // handler above has already run it.
            DisconnectInternal();

            UnregisterConnectionChangeHandlers();
        }

        /// <inheritdoc />
        public bool Alert() => ChangeState(State.Alert);

        /// <inheritdoc />
        public bool Sleep() => ChangeState(State.Sleeping);

        /// <inheritdoc />
        public bool Ready() => ChangeState(State.Ready);

        // The device model owns which transitions are legal (see Device.CanChangeState);
        // publishing follows from the state change through HandleDeviceStateChange, so
        // these three don't publish anything themselves.
        private bool ChangeState(State newState)
        {
            if (!_device.TryChangeState(newState))
            {
                _logger.LogWarning($"Refused to change state to '{newState.GetString()}' from '{_device.StateAttribute.Value.GetString()}'.");
                return false;
            }

            return true;
        }

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
        }

        private void HandleDeviceStateChange(DeviceStateChangeEventArgs args)
        {
            switch (args.CurrentState)
            {
                case State.Disconnected:
                    _mqttClient.PublishHomieAttribute(_device.StateAttribute, _homiePublishSettings.DeviceStatePublishSettings, _logger);
                    DisconnectInternal();
                    return;
                case State.Init:
                {
                    _mqttClient.PublishHomieDeviceInfo(_device, _homiePublishSettings, _logger);

                    // Consume the target: a first connect, and any re-announce from
                    // Ready, both land on Ready.
                    var postInitState = _postInitState;
                    _postInitState = State.Ready;

                    if (!_device.TryChangeState(postInitState))
                    {
                        _logger.LogError($"Failed to change device state to '{postInitState.GetString()}' after publishing device info. Disconnecting.");
                        DisconnectInternal();
                    }
                    return;
                }
                case State.Ready:
                case State.Sleeping:
                case State.Alert:
                    _mqttClient.PublishHomieAttribute(_device.StateAttribute, _homiePublishSettings.DeviceStatePublishSettings, _logger);
                    return;
                case State.Lost:
                    return;
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
            try
            {
                // Set() reflects the value back to the property topic via OnUpdate, which
                // is what the spec asks for. OnCommand is raised separately so an app can
                // tell a controller's command from its own update -- property.OnUpdate
                // fires for both and cannot distinguish them.
                property.Set(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply the command on '{topic}'.");
            }

            try
            {
                OnCommand?.Invoke(new HomieCommandEventArgs(property, message));
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
            // Going back through Init republishes everything and returns to whatever the
            // device was in (see HandleDeviceStateChange) -- Ready normally, but Alert or
            // Sleeping are preserved: a broker restart is not a reason to clear an alert.
            var stateBeforeReannounce = _device.StateAttribute.Value;
            var postInitState = stateBeforeReannounce == State.Alert || stateBeforeReannounce == State.Sleeping
                ? stateBeforeReannounce
                : State.Ready;

            if (!Announce(postInitState))
            {
                _logger.LogError($"Reconnected but could not re-announce: state is '{_device.StateAttribute.Value.GetString()}'.");
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
        private bool Announce(State postInitState)
        {
            if (_announcedThisSession)
            {
                _logger.LogDebug("Already announced on this session; not repeating it.");
                return true;
            }

            // Armed together with the flag, so the target can only ever be set for an
            // announce that is actually going to run. Setting it at the call site meant a
            // no-op Announce() left it primed, and the *next* announce -- possibly a
            // first connect that should land on 'ready' -- would consume a stale 'alert'.
            //
            // Both set before the transition, not after: publishing device info re-enters
            // this class through the state-change handler, which reads them.
            _postInitState = postInitState;
            _announcedThisSession = true;

            if (_device.TryChangeState(State.Init))
            {
                return true;
            }

            _announcedThisSession = false;
            _postInitState = State.Ready;
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
            var message = args.Value;
            var retained = property.RetainedAttribute.Value;
            string topic = property.GetTopic();

            // No log line here: PublishHomiePropertyValue already logs topic and payload,
            // and this one decoded the same bytes a second time. Interpolated arguments
            // are built whether or not anything consumes them, so on RoomSensor's 5s cycle
            // that was a dozen throwaway strings per reading, forever.
            _mqttClient.PublishHomiePropertyValue(topic, message, _homiePublishSettings.PropertyUpdatePublishSettings, retained, _logger);
        }

        private static IDictionary InitializeSettablePropertiesTable(Device device)
        {
            var settableProperties = device.GetAllSettableProperties();

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
                var commandTopic = $"{settableProperties[i].GetTopic()}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
                settablePropertiesTable.Add(commandTopic, settableProperties[i]);
            }

            return settablePropertiesTable;
        }
    }
}
