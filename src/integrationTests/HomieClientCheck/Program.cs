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
        private const string LifecycleReady = "ready";
        private const string LifecycleAlert = "alert";
        private const string LifecycleSleeping = "sleeping";

        private static IHomieClient _homieClient;
        private static IntegerProperty _counter;

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
                    .AddEnumProperty("lifecycle", "Lifecycle control", LifecycleReady)
                        .WithSettable(true)
                        .WithFormat($"{LifecycleReady},{LifecycleAlert},{LifecycleSleeping}")
                    .BuildProperty()
                .BuildNode()
            .BuildDevice();
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

            switch (payload)
            {
                case LifecycleAlert:
                    Debug.WriteLine($"HomieClientCheck: alert -> {_homieClient.Alert()}");
                    return;
                case LifecycleSleeping:
                    Debug.WriteLine($"HomieClientCheck: sleep -> {_homieClient.Sleep()}");
                    return;
                case LifecycleReady:
                    Debug.WriteLine($"HomieClientCheck: ready -> {_homieClient.Ready()}");
                    return;
                default:
                    Debug.WriteLine($"HomieClientCheck: unknown lifecycle command '{payload}'.");
                    return;
            }
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
