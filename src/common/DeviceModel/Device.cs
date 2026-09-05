using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;
using System.Collections;

namespace SmartHome.DeviceModel
{
    public delegate void DeviceStateChangeHandler(DeviceStateChangeEventArgs args);

    public delegate void AlertChangeHandler(AlertChangeEventArgs args);

    /// <summary>
    /// What a device is: its nodes, its lifecycle state and the alerts it has raised.
    /// </summary>
    /// <remarks>
    /// Says nothing about how any of that is published. There is no topic here, no
    /// protocol version, no last will and no MQTT reference anywhere in this assembly --
    /// an adapter reads this tree and decides all of it. That separation is what lets one
    /// device description go out as Homie v4, Homie v5 or Home Assistant MQTT Discovery
    /// with the device app unchanged.
    /// </remarks>
    public class Device : NamedEntityBase
    {
        private readonly object _lock = new();

        private readonly IDictionary _nodes = new Hashtable();
        private readonly IDictionary _alerts = new Hashtable();
        private readonly ILogger _logger;

        public Device(string id, string name)
            : base(id, name)
        {
            _logger = this.GetCurrentClassLogger();
        }

        /// <summary>Raised after the device's state has changed.</summary>
        public event DeviceStateChangeHandler? OnDeviceStateChange;

        /// <summary>
        /// Raised when an alert is raised, re-raised with a different message, or
        /// cleared.
        /// </summary>
        /// <remarks>
        /// Only when the raised set actually changed. Re-raising an alert with the
        /// message it already carries is a no-op, so a device that calls
        /// <see cref="RaiseAlert"/> from inside its measurement loop does not republish
        /// the same alert every few seconds.
        /// </remarks>
        public event AlertChangeHandler? OnAlertChange;

        /// <summary>
        /// Where the device is in its lifecycle. Starts at
        /// <see cref="DeviceState.Disconnecting"/>, which for a device that has never
        /// connected means what it says: not on the broker.
        /// </summary>
        public DeviceState State { get; private set; } = DeviceState.Disconnecting;

        /// <summary>The device's nodes, in no particular order.</summary>
        public Node[] Nodes
        {
            get
            {
                lock (_lock)
                {
                    Node[] nodes = new Node[_nodes.Count];
                    _nodes.Values.CopyTo(nodes, 0);
                    return nodes;
                }
            }
        }

        /// <summary>The alerts currently raised, in no particular order.</summary>
        public Alert[] Alerts
        {
            get
            {
                lock (_lock)
                {
                    Alert[] alerts = new Alert[_alerts.Count];
                    _alerts.Values.CopyTo(alerts, 0);
                    return alerts;
                }
            }
        }

        /// <summary>
        /// Whether anything is wrong. A Homie v4 adapter's whole view of the alert set:
        /// it can publish <c>$state = alert</c> and nothing more.
        /// </summary>
        public bool HasAlerts
        {
            get
            {
                lock (_lock)
                {
                    return _alerts.Count > 0;
                }
            }
        }

        /// <summary>
        /// Raises an alert, or replaces the message of one already raised under this id.
        /// </summary>
        /// <remarks>
        /// Nothing about the device's lifecycle state changes. An alerting device is
        /// still <c>ready</c> as far as this model is concerned -- it is running and it
        /// is publishing -- and it is the Homie v4 adapter, which has nowhere else to put
        /// the fact, that turns a non-empty alert set into its <c>alert</c> state.
        /// </remarks>
        /// <param name="id">
        /// What is wrong, as an id. Held to the same character rule as a node or property
        /// id: Homie v5 publishes it as a topic level.
        /// </param>
        /// <param name="message">The human-readable description.</param>
        /// <exception cref="ArgumentException">The id is empty or not a valid id.</exception>
        public void RaiseAlert(string id, string message)
        {
            NamedEntityBase.ValidateId(id);

            var text = message ?? string.Empty;

            lock (_lock)
            {
                // Contains before indexing, rather than reading and testing for null:
                // what a Hashtable's indexer returns for a key it does not hold is not
                // something this code needs an opinion about.
                if (_alerts.Contains(id))
                {
                    var existing = (Alert)_alerts[id];
                    if (existing.Message == text)
                    {
                        return;
                    }
                }

                _alerts[id] = new Alert(id, text);
            }

            _logger.LogWarning($"Device '{Id}' raised alert '{id}': {text}");
            OnAlertChange?.Invoke(new AlertChangeEventArgs(this, id, text, true));
        }

        /// <summary>
        /// Clears an alert. Does nothing if it was not raised.
        /// </summary>
        public void ClearAlert(string id)
        {
            if (id == null)
            {
                return;
            }

            lock (_lock)
            {
                if (!_alerts.Contains(id))
                {
                    return;
                }

                _alerts.Remove(id);
            }

            _logger.LogInformation($"Device '{Id}' cleared alert '{id}'.");
            OnAlertChange?.Invoke(new AlertChangeEventArgs(this, id, null, false));
        }

        internal void AddNode(Node node) => AddNodes(new Node[] { node });

        internal void AddNodes(Node[] nodes)
        {
            lock (_lock)
            {
                foreach (var node in nodes)
                {
                    _logger.LogDebug($"Adding node '{node.Id}' to device '{Id}'.");
                    if (_nodes.Contains(node.Id))
                    {
                        throw new ArgumentException($"A node with the id '{node.Id}' already exists in the device '{Id}'.");
                    }

                    node.Parent = this;
                    _nodes.Add(node.Id, node);
                }
            }
        }

        /// <summary>
        /// Moves the device to a new lifecycle state, if the transition is legal.
        /// </summary>
        /// <remarks>
        /// Public because the adapter that publishes the change lives in another
        /// assembly. It drives the model rather than the other way round: this decides
        /// whether the transition is allowed and records it, and whatever is subscribed
        /// to <see cref="OnDeviceStateChange"/> decides what goes on the wire. An adapter
        /// that published first and asked afterwards could announce a state the device
        /// then refused to enter.
        /// </remarks>
        /// <returns>False when the current state does not allow the transition.</returns>
        public bool TryChangeState(DeviceState newState)
        {
            _logger.LogDebug($"Attempting to change state of device '{Id}' from '{State.GetName()}' to '{newState.GetName()}'.");
            if (CanChangeState(newState))
            {
                var oldState = State;
                State = newState;

                _logger.LogInformation($"Device '{Id}' state changed from '{oldState.GetName()}' to '{newState.GetName()}'.");

                OnDeviceStateChange?.Invoke(new DeviceStateChangeEventArgs(this, oldState, newState));
                return true;
            }

            _logger.LogWarning($"Invalid state transition attempted for device '{Id}' from '{State.GetName()}' to '{newState.GetName()}'.");

            return false;
        }

        /// <remarks>
        /// Nothing may transition *to* <see cref="DeviceState.Lost"/>: that is published
        /// by the broker from the connection's last will, precisely because the device is
        /// in no position to say it.
        /// </remarks>
        private bool CanChangeState(DeviceState newState) => State switch
        {
            // Connecting -> Sleeping completes the set of states a re-announce can
            // return to; see the note on the Sleeping row below.
            DeviceState.Connecting => newState == DeviceState.Ready || newState == DeviceState.Sleeping || newState == DeviceState.Disconnecting,
            // Ready -> Connecting is the re-announce path: a broker that restarted has an
            // empty retained store, so a reconnected device has to publish its
            // description again and come back through Connecting to Ready, exactly as it
            // did on first connect.
            DeviceState.Ready => newState == DeviceState.Sleeping || newState == DeviceState.Disconnecting || newState == DeviceState.Connecting,
            // Sleeping must reach Connecting for the same reason Ready does. Without it a
            // device that was asleep when the broker restarted could never re-announce:
            // the adapter would log "could not re-announce" and give up, leaving the
            // fresh broker with no device description while the device happily published
            // values into it.
            DeviceState.Sleeping => newState == DeviceState.Ready || newState == DeviceState.Disconnecting || newState == DeviceState.Connecting,
            DeviceState.Disconnecting => newState == DeviceState.Connecting || newState == DeviceState.Ready,
            DeviceState.Lost => newState == DeviceState.Connecting || newState == DeviceState.Ready,
            _ => false,
        };

        /// <summary>
        /// Every settable property in the tree, which is exactly the set an adapter has
        /// to subscribe a command topic for.
        /// </summary>
        public PropertyBase[] GetAllSettableProperties()
        {
            var properties = new ArrayList();
            foreach (var node in Nodes)
            {
                foreach (var property in node.Properties)
                {
                    if (property.Settable)
                    {
                        properties.Add(property);
                    }
                }
            }

            return (PropertyBase[])properties.ToArray(typeof(PropertyBase));
        }
    }
}
