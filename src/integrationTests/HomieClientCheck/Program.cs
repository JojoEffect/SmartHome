using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.EventArgs;
using SmartHome.Homie.V4.Extensions;
using SmartHome.Homie.V4.Properties;
using SmartHome.Mqtt;
using SmartHome.Networking;

namespace SmartHome.IntegrationTests.HomieClientCheck
{
    // A device that exists only to be measured against the Homie v4 convention.
    //
    // It is deliberately not a sensor or an actuator: it carries one property of every
    // datatype, settable and not, retained and not, so the conformance test can assert
    // the whole convention rather than whichever corners a real device happens to use.
    // Nothing here should ever depend on RoomSensor or any shipped device.
    //
    // Its verdict is host-side (see scripts\Run-IntegrationTests.ps1, kind
    // HomieConformance): the runner reads what actually lands on the broker, retain
    // flags included, and drives the device by publishing to /set topics. So this app
    // emits no [ITEST] marker.
    public class Program
    {
        private const string BrokerHost = "192.168.1.238";
        private const int HeartbeatIntervalMs = 2000;

        // The lifecycle property is how the host drives $state: publishing 'alert',
        // 'sleeping' or 'ready' to its /set topic moves the device, which is the only
        // way to exercise those states from outside.
        //
        // The names are taken from the State enum rather than spelled out here. They are
        // the convention's own vocabulary and StateExtensions.GetString() already owns
        // it; a private copy would drift from $state, which is precisely the value the
        // host compares this property against. This array is the single source for both
        // the $format string and the set of commands HandleLifecycleCommand accepts.
        private static readonly State[] LifecycleStates = { State.Ready, State.Alert, State.Sleeping };

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
            var builder = new HomieDeviceBuilder("homie-client-check", "Homie client check");

            return builder
                .AddNode("matrix", "Datatype matrix", "conformance")
                    .AddIntegerProperty("integer-value", "Integer", 0)
                        .WithSettable(true)
                        .WithFormat("0:100")
                        .WithUnit(Unit.CountOrAmount)
                    .BuildProperty()
                    .AddFloatProperty("float-value", "Float", 0.0)
                        .WithSettable(true)
                        .WithUnit(Unit.DegreeCelsius)
                    .BuildProperty()
                    .AddBooleanProperty("boolean-value", "Boolean", false)
                        .WithSettable(true)
                    .BuildProperty()
                    .AddStringProperty("string-value", "String", "initial")
                        .WithSettable(true)
                    .BuildProperty()
                    .AddEnumProperty("enum-value", "Enum", "low")
                        .WithSettable(true)
                        .WithFormat("low,medium,high")
                    .BuildProperty()
                    .AddColorProperty("color-value", "Colour", new HomieColor { R = 0, G = 0, B = 0 })
                        .WithSettable(true)
                        .WithFormat("rgb")
                    .BuildProperty()
                    // Not settable and not retained: both defaults are the opposite, so
                    // this is the property that proves $settable and $retained are
                    // published as declared rather than assumed.
                    .AddIntegerProperty("counter", "Counter", 0)
                        .WithRetained(false)
                    .BuildProperty(out _counter)
                    .AddEnumProperty("lifecycle", "Lifecycle control", State.Ready.GetString())
                        .WithSettable(true)
                        .WithFormat(BuildLifecycleFormat())
                    .BuildProperty(out _lifecycle)
                .BuildNode()
            .BuildDevice();
        }

        // Not StringUtils.Join: it takes string[], so using it would mean building an
        // intermediate array of names to join -- two passes and an extra allocation for a
        // three-element list. SmartHome.Text is on the device either way (SmartHome.Homie
        // references it), so the trade here is about the second pass, not the assembly.
        private static string BuildLifecycleFormat()
        {
            var format = new StringBuilder();

            for (var i = 0; i < LifecycleStates.Length; i++)
            {
                if (i > 0)
                {
                    format.Append(',');
                }

                format.Append(LifecycleStates[i].GetString());
            }

            return format.ToString();
        }

        private static void HandleCommand(HomieCommandEventArgs args)
        {
            var payload = Encoding.UTF8.GetString(args.Payload, 0, args.Payload.Length);
            Debug.WriteLine($"HomieClientCheck: command on '{args.Property.TopicId}' -> '{payload}'");

            if (args.Property.TopicId != "lifecycle")
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
            // down: Device.CanChangeState refuses illegal transitions (alert -> sleeping
            // among them), and an unknown payload is not a state at all. Either way the
            // reflection would leave the retained store advertising a state the device is
            // not in, contradicting the $state published right beside it -- and retained,
            // so every controller connecting later reads the contradiction too.
            //
            // Actuators copying this device should do the same: reflect the outcome, not
            // the command.
            //
            // Unconditional, so an accepted command publishes the same payload twice --
            // the library's reflection, then an identical correction. See #36: guarding
            // on _lifecycle.Value would suppress the second publish, but Value is already
            // updated even when the reflection's own publish threw, and the guard would
            // then suppress the only publish the broker would have seen.
            _lifecycle.Update(_homieClient.State.GetString());
        }

        private static void HandleLifecycleCommand(string payload)
        {
            foreach (var state in LifecycleStates)
            {
                if (payload != state.GetString())
                {
                    continue;
                }

                // Every arm named, and the default refuses rather than falling back to
                // Ready(). LifecycleStates is also what BuildLifecycleFormat publishes
                // as $format, so a state added there is immediately offered to
                // controllers -- and a default that applied Ready() would answer such a
                // command with the wrong transition and no sign that anything was
                // missed. Refusing leaves the correction in HandleCommand to publish the
                // state the device is actually in.
                var applied = state switch
                {
                    State.Ready => _homieClient.Ready(),
                    State.Alert => _homieClient.Alert(),
                    State.Sleeping => _homieClient.Sleep(),
                    _ => false,
                };

                Debug.WriteLine($"HomieClientCheck: '{payload}' -> {applied}");
                return;
            }

            Debug.WriteLine($"HomieClientCheck: unknown lifecycle command '{payload}'.");
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
