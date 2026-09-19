using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.Properties;
using SmartHome.Homie.V4;
using SmartHome.Mqtt;
using SmartHome.Networking;
using SmartHome.Protocol;

namespace SmartHome.IntegrationTests.HomieClientCheck
{
    // A device that exists only to be measured against the Homie v4 convention.
    //
    // It is deliberately not a sensor or an actuator: it carries one property of every
    // datatype, settable and not, retained and not, so the conformance test can assert
    // the whole convention rather than whichever corners a real device happens to use.
    // Nothing here should ever depend on RoomSensor or any shipped device.
    //
    // The description itself is protocol-neutral -- DeviceBuilder, Units, the model's
    // property types -- and only the client it is handed to knows Homie v4. That is the
    // point of the seam, and this app is where it is proved: the same tree measured
    // against the convention has to go out byte-for-byte as it did when the model itself
    // was the convention. It talks to IHomieClient rather than IDeviceProtocol only
    // because the lifecycle property below has to say what $state a controller would see,
    // which is the one thing the neutral seam deliberately cannot answer.
    //
    // Its verdict is host-side (see scripts\Run-IntegrationTests.ps1, kind
    // HomieConformance): the runner reads what actually lands on the broker, retain
    // flags included, and drives the device by publishing to /set topics. So this app
    // emits no [ITEST] marker.
    public class Program
    {
        private const string BrokerHost = "192.168.1.238";
        private const int HeartbeatIntervalMs = 2000;

        // The id the lifecycle property raises its alert under. v4 has nowhere to put it
        // -- $state carries the bare token 'alert' and nothing else -- but the model keys
        // alerts, and clearing one means naming it.
        private const string LifecycleAlertId = "lifecycle";

        // The lifecycle property is how the host drives $state: publishing 'alert',
        // 'sleeping' or 'ready' to its /set topic moves the device, which is the only
        // way to exercise those states from outside.
        //
        // The names are taken from HomieStates rather than spelled out here. They are the
        // convention's own vocabulary and the adapter already owns it; a private copy
        // would drift from $state, which is precisely the value the host compares this
        // property against. That is also why these are the adapter's constants and not
        // the model's: the model's DeviceState has no 'alert' member at all, and its names
        // are deliberately not the wire's.
        //
        // It is the single source for the $format string, and since issue #39 that is
        // the only place the membership rule is stated: EnumProperty measures a /set
        // against the options it declares -- which is what the adapter renders as $format
        // -- and the client does not raise OnCommand for a payload it refused, so a name
        // outside this array can no longer reach the handler below. The array is still
        // needed -- something has to declare the options, and the handler still has to map
        // a name to the client call that applies it -- but that mapping is dispatch now,
        // not a second copy of the check.
        private static readonly string[] LifecycleStates = { HomieStates.Ready, HomieStates.Alert, HomieStates.Sleeping };

        private static IHomieClient _homieClient;
        private static IntegerProperty _counter;
        private static EnumProperty _lifecycle;

        public static void Main()
        {
            LogDispatcher.LoggerFactory = new DebugLoggerFactory();

            NetworkHelper.ConnectToConfiguredNetwork();

            var device = BuildDevice();
            var mqttClient = new ReconnectingMqttClient(BrokerHost);
            _homieClient = new HomieClient(device, mqttClient);
            _homieClient.OnCommand += HandleCommand;

            ConnectWithRetry();

            // A non-retained counter, so the host can see the device is alive without
            // the broker replaying a stale value at it.
            var value = 0;
            while (true)
            {
                value++;
                _counter.Update(value);
                Thread.Sleep(HeartbeatIntervalMs);
            }
        }

        private static Device BuildDevice()
        {
            var builder = new DeviceBuilder("homie-client-check", "Homie client check");

            return builder
                .AddNode("matrix", "Datatype matrix", "conformance")
                    .AddIntegerProperty("integer-value", "Integer", 0)
                        .WithSettable(true)
                        .WithFormat("0:100")
                        .WithUnit(Units.CountOrAmount)
                    .BuildProperty()
                    .AddFloatProperty("float-value", "Float", 0.0)
                        .WithSettable(true)
                        .WithUnit(Units.DegreeCelsius)
                    .BuildProperty()
                    .AddBooleanProperty("boolean-value", "Boolean", false)
                        .WithSettable(true)
                    .BuildProperty()
                    .AddStringProperty("string-value", "String", "initial")
                        .WithSettable(true)
                    .BuildProperty()
                    // The text form on purpose, here and for the colour below: a
                    // conformance device should still exercise the path a device author
                    // most often writes, and these two are where the model parses text
                    // into the structured format the adapter renders back into $format.
                    .AddEnumProperty("enum-value", "Enum", "low")
                        .WithSettable(true)
                        .WithFormat("low,medium,high")
                    .BuildProperty()
                    .AddColorProperty("color-value", "Colour", new ColorValue { R = 0, G = 0, B = 0 })
                        .WithSettable(true)
                        .WithFormat("rgb")
                    .BuildProperty()
                    // Not settable and not retained: both defaults are the opposite, so
                    // this is the property that proves $settable and $retained are
                    // published as declared rather than assumed.
                    .AddIntegerProperty("counter", "Counter", 0)
                        .WithRetained(false)
                    .BuildProperty(out _counter)
                    // WithOptions rather than WithFormat, unlike the enum above: these
                    // names come from HomieStates, so they are already an array, and
                    // joining them into text for the model to split again would be two
                    // passes to arrive back where we started. It is also the safer half
                    // of the trade here -- a joined string that lost a separator declares
                    // options nobody wrote, and the adapter could not report it even in
                    // principle: it never sees the text, only what the model parsed out of
                    // it, so the mistake would surface as a $format this test then failed
                    // on, a long way from its cause. The array is handed over as written.
                    .AddEnumProperty("lifecycle", "Lifecycle control", HomieStates.Ready)
                        .WithSettable(true)
                        .WithOptions(LifecycleStates)
                    .BuildProperty(out _lifecycle)
                .BuildNode()
            .BuildDevice();
        }

        private static void HandleCommand(DeviceCommandEventArgs args)
        {
            var payload = Encoding.UTF8.GetString(args.Payload, 0, args.Payload.Length);
            Debug.WriteLine($"HomieClientCheck: command on '{args.Property.Id}' -> '{payload}'");

            if (args.Property.Id != "lifecycle")
            {
                // Every other property is just reflected back, which the library already
                // did before this handler ran.
                return;
            }

            HandleLifecycleCommand(payload);

            // Publish what the device actually is, over the optimistic reflection the
            // library already made.
            //
            // Reflecting a command onto its own property is right for an ordinary
            // property, but this one's value is a *request*, and a request can be turned
            // down: the adapter refuses alert -> sleeping (v4's alert may only return to
            // ready or disconnect), the model's own transition table refuses the rest, and
            // an unknown payload is not a state at all. Either way the reflection would
            // leave the retained store advertising a state the device is not in,
            // contradicting the $state published right beside it -- and retained, so every
            // controller connecting later reads the contradiction too.
            //
            // HomieState, not the model's State: this property's values are $state tokens,
            // and two of the five model states are deliberately not spelled the way the
            // wire spells them -- while 'alert' is not a model state at all, it is what the
            // adapter synthesises from the alert set.
            //
            // Actuators copying this device should do the same: reflect the outcome, not
            // the command.
            //
            // Unconditional, and that is the decision rather than an oversight (#36 item
            // 3, closed on this reasoning; the remaining half is #61).
            //
            // It costs a duplicate: an ACCEPTED command publishes the same payload twice,
            // the library's reflection and then an identical correction. Every guard that
            // would save that publish is content-based, and every content-based guard is
            // wrong in the one case that matters. PropertyBase.Set applies the value
            // before publishing it and EnumProperty.Update assigns Value before raising
            // OnUpdate, so by the time this handler runs Value already holds the commanded
            // value whether or not the reflection reached the broker -- and the client
            // catches a throw from that publish and raises OnCommand anyway. So
            // 'if (_lifecycle.Value != actual)' suppresses the correction precisely when
            // the correction is the only publish the broker would ever have seen, and the
            // store is left with no value at all. Comparing against the command payload
            // instead fails the same way.
            //
            // A duplicate publish is cheap and self-correcting; a silently missing one is
            // neither. #61 is what would make the guard safe -- the library telling the
            // handler whether its reflection actually landed -- and until then
            // unconditional is the side of the trade to be on.
            _lifecycle.Update(_homieClient.HomieState);
        }

        private static void HandleLifecycleCommand(string payload)
        {
            foreach (string state in LifecycleStates)
            {
                if (payload != state)
                {
                    continue;
                }

                // Read the effective token either side of the call rather than taking the
                // call's word for it: the lifecycle members of IDeviceProtocol return
                // void, deliberately -- the seam has no "command refused" channel, for the
                // same reason the convention has none -- so what the device *is* afterwards
                // is the only outcome there is to report. 'Unchanged but already what was
                // asked for' counts as applied: a second 'alert' while alerting moved
                // nothing and was still honoured.
                var before = _homieClient.HomieState;
                ApplyLifecycleCommand(state);
                var after = _homieClient.HomieState;
                var applied = after != before || after == state;

                Debug.WriteLine($"HomieClientCheck: '{payload}' -> {applied}");
                return;
            }

            // Unreachable since #39 -- the property refuses a payload its declared options
            // do not list, and a refused payload never reaches OnCommand. Kept as the thing
            // that would say so if that ever stopped being true.
            Debug.WriteLine($"HomieClientCheck: unknown lifecycle command '{payload}'.");
        }

        // Every arm named, and the default refuses rather than falling back to Ready().
        // LifecycleStates is also what the adapter publishes as $format, so a state added
        // there is immediately offered to controllers -- and a default that applied
        // Ready() would answer such a command with the wrong transition and no sign that
        // anything was missed. Refusing leaves the correction in HandleCommand to publish
        // the state the device is actually in.
        //
        // The default logs through RefuseUnwiredState rather than doing nothing inline:
        // silence is indistinguishable in the log from a transition the convention's own
        // rules refused, which is exactly the "no sign that anything was missed" this arm
        // exists to remove.
        //
        // Every transition goes through the client, never device.TryChangeState: the
        // model's table knows nothing about v4's alert -> sleeping ban, so a direct call
        // would put 'sleeping' on the wire from an alerting device.
        private static void ApplyLifecycleCommand(string state)
        {
            switch (state)
            {
                case HomieStates.Ready:
                    // Clear first, then move only if there is a move to make. 'ready' is
                    // the answer to both 'alert' and 'sleeping', but those are two
                    // different things in this model: the alert set is keyed and only
                    // ClearAlert empties it, while Sleeping is a lifecycle state and only
                    // a transition leaves it. A device that is Ready with an alert raised
                    // needs the first and would be refused the second.
                    _homieClient.ClearAlert(LifecycleAlertId);

                    if (_homieClient.State == DeviceState.Sleeping)
                    {
                        _homieClient.Ready();
                    }

                    break;

                case HomieStates.Alert:
                    // The message has nowhere to go on a v4 wire; it is here because the
                    // model keys alerts by id and a message is what makes the id readable
                    // in the device's own log.
                    _homieClient.RaiseAlert(LifecycleAlertId, "raised through the lifecycle property");
                    break;

                case HomieStates.Sleeping:
                    _homieClient.Sleep();
                    break;

                default:
                    RefuseUnwiredState(state);
                    break;
            }
        }

        // A state that is in LifecycleStates -- and so in $format, and so offered to
        // controllers -- but has no arm in ApplyLifecycleCommand's switch. Says so
        // rather than sharing the refused transition's log line, so the gap is visible in
        // the captured debug output instead of looking like the device declining a
        // command it understood.
        private static void RefuseUnwiredState(string state)
        {
            Debug.WriteLine($"HomieClientCheck: '{state}' is offered in $format but has no transition wired -- refusing.");
        }

        // The runner cycles the broker while this device runs, so it can boot with no
        // broker present. A bare Connect() would throw out of Main and the CLR would
        // restart the app, turning the test into a reboot-loop meter.
        //
        // 20 attempts rather than the default 10: this test is deliberately started
        // against a broker the runner is still cycling.
        private static void ConnectWithRetry()
        {
            if (!_homieClient.ConnectWithRetry(maxAttempts: 20))
            {
                throw new Exception($"Could not connect to {BrokerHost}.");
            }

            Debug.WriteLine("HomieClientCheck: connected and announced.");
        }
    }
}
