using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Enums;
using SmartHome.DeviceModel.EventArgs;
using SmartHome.DeviceModel.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.TestFramework;
using System;

namespace SmartHome.UnitTests
{
    // The protocol-neutral model: the tree it builds, the ids it will accept, the
    // lifecycle it allows, and the alerts it holds. Nothing here mentions a topic,
    // because nothing in the model knows about one -- that is the property the whole
    // design rests on, and the compiler enforces it: SmartHome.DeviceModel references no
    // MQTT assembly at all.
    [TestClass]
    public class DeviceModelTests
    {
        private const string _deviceId = "super-car";
        private const string _deviceName = "Super car";
        private const string _nodeId = "engine";
        private const string _nodeName = "Car engine";
        private const string _nodeType = "V8";

        [Setup]
        public void Setup()
        {
            LogDispatcher.LoggerFactory = new DebugLoggerFactory();
        }

        [Cleanup]
        public void Cleanup()
        {
            LogDispatcher.LoggerFactory = null;
        }

        [TestMethod]
        public void Builder_Builds_The_Tree_And_Links_Every_Parent()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 21.5)
                    .BuildProperty(out FloatProperty property)
                .BuildNode(out Node node)
                .BuildDevice();

            Assert.AreEqual(_deviceId, device.Id);
            Assert.AreEqual(_deviceName, device.Name);
            Assert.AreEqual(1, device.Nodes.Length);
            Assert.AreEqual(_nodeType, node.Type);
            Assert.AreEqual(1, node.Properties.Length);

            // The chain an adapter walks to name things. The builder attaches a property
            // to its node before it attaches the node to the device, so this is exactly
            // the window in which the old model's cached topics went wrong -- there is
            // no cache now because there is no topic.
            Assert.AreSame(node, property.Parent);
            Assert.AreSame(device, node.Parent);
            Assert.IsNull(device.Parent);
        }

        [TestMethod]
        public void Builder_Refuses_An_Id_That_No_Convention_Could_Carry()
        {
            // Rejected where it was written rather than on the wire, and by the builder
            // as well as the entity: the builder defers constructing the device until
            // BuildDevice(), which would otherwise be several calls later.
            Assert.ThrowsException(typeof(ArgumentException), () => new DeviceBuilder("Super-Car", _deviceName));
            Assert.ThrowsException(typeof(ArgumentException), () => new DeviceBuilder("super car", _deviceName));
            Assert.ThrowsException(typeof(ArgumentException), () => new DeviceBuilder("super_car", _deviceName));
            Assert.ThrowsException(typeof(ArgumentException), () => new DeviceBuilder("-super-car", _deviceName));
            Assert.ThrowsException(typeof(ArgumentException), () => new DeviceBuilder("super-car-", _deviceName));
            Assert.ThrowsException(typeof(ArgumentException), () => new DeviceBuilder(string.Empty, _deviceName));
        }

        [TestMethod]
        public void Builder_Refuses_A_Bad_Id_At_Every_Level()
        {
            var builder = new DeviceBuilder(_deviceId, _deviceName);
            Assert.ThrowsException(typeof(ArgumentException), () => builder.AddNode("Engine", _nodeName, _nodeType));

            var nodeBuilder = builder.AddNode(_nodeId, _nodeName, _nodeType);
            Assert.ThrowsException(typeof(ArgumentException), () => nodeBuilder.AddFloatProperty("Temperature", "Temperature", 0));
        }

        [TestMethod]
        public void Ids_Accept_Digits_And_Interior_Hyphens()
        {
            var device = new DeviceBuilder("room-sensor-1", _deviceName).BuildDevice();

            Assert.AreEqual("room-sensor-1", device.Id);
        }

        [TestMethod]
        public void Device_Starts_Disconnecting_And_Reaches_Ready_Through_Connecting()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();

            Assert.AreEqual((int)DeviceState.Disconnecting, (int)device.State, "a device that has never connected is not on the broker");

            Assert.IsTrue(device.TryChangeState(DeviceState.Connecting));
            Assert.IsTrue(device.TryChangeState(DeviceState.Ready));
            Assert.AreEqual((int)DeviceState.Ready, (int)device.State);
        }

        [TestMethod]
        public void Device_Can_Return_To_Connecting_To_Re_Announce()
        {
            // A broker that restarted has an empty retained store, so a device that
            // reconnects has to publish its whole description again -- which means going
            // back through Connecting. Without this the adapter gives up and the fresh
            // broker is left with values but no description of what they are.
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();
            device.TryChangeState(DeviceState.Connecting);
            device.TryChangeState(DeviceState.Ready);

            Assert.IsTrue(device.TryChangeState(DeviceState.Connecting), "a ready device must be able to re-announce");

            device.TryChangeState(DeviceState.Ready);
            Assert.IsTrue(device.TryChangeState(DeviceState.Sleeping));
            Assert.IsTrue(device.TryChangeState(DeviceState.Connecting), "a sleeping device must be able to re-announce too");
        }

        [TestMethod]
        public void Device_Refuses_To_Enter_Lost_By_Itself()
        {
            // 'lost' is published by the broker from the connection's last will, exactly
            // because a device in that state is in no position to say so.
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();
            device.TryChangeState(DeviceState.Connecting);
            device.TryChangeState(DeviceState.Ready);

            Assert.IsFalse(device.TryChangeState(DeviceState.Lost));
            Assert.AreEqual((int)DeviceState.Ready, (int)device.State, "a refused transition must not move the device");
        }

        [TestMethod]
        public void Device_Refuses_A_Transition_Its_Current_State_Does_Not_Allow()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();

            // Disconnecting -> Sleeping: a device that is not connected cannot announce
            // that it is going to sleep.
            Assert.IsFalse(device.TryChangeState(DeviceState.Sleeping));

            device.TryChangeState(DeviceState.Connecting);
            Assert.IsFalse(device.TryChangeState(DeviceState.Connecting), "already connecting");
        }

        [TestMethod]
        public void Device_Reports_Every_State_Change_Once()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();

            var changes = 0;
            DeviceState previous = DeviceState.Lost;
            DeviceState current = DeviceState.Lost;
            device.OnDeviceStateChange += (DeviceStateChangeEventArgs args) =>
            {
                changes++;
                previous = args.PreviousState;
                current = args.CurrentState;
            };

            device.TryChangeState(DeviceState.Connecting);
            Assert.AreEqual(1, changes);
            Assert.AreEqual((int)DeviceState.Disconnecting, (int)previous);
            Assert.AreEqual((int)DeviceState.Connecting, (int)current);

            device.TryChangeState(DeviceState.Lost);
            Assert.AreEqual(1, changes, "a refused transition must not be announced");
        }

        [TestMethod]
        public void Device_Holds_The_Alerts_It_Has_Raised()
        {
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();

            Assert.IsFalse(device.HasAlerts);
            Assert.AreEqual(0, device.Alerts.Length);

            device.RaiseAlert("battery", "Battery is low, at 8%");
            device.RaiseAlert("sensor-unreadable", "The BMP280 did not answer");

            Assert.IsTrue(device.HasAlerts);
            Assert.AreEqual(2, device.Alerts.Length);

            device.ClearAlert("battery");
            Assert.AreEqual(1, device.Alerts.Length);
            Assert.AreEqual("sensor-unreadable", device.Alerts[0].Id);
            Assert.AreEqual("The BMP280 did not answer", device.Alerts[0].Message);

            device.ClearAlert("sensor-unreadable");
            Assert.IsFalse(device.HasAlerts);
        }

        [TestMethod]
        public void Device_Alerts_Are_Independent_Of_Its_Lifecycle_State()
        {
            // There is no Alert state, on purpose: 'something is wrong' is not a place in
            // the lifecycle, and a device with a flat battery is still ready. Turning a
            // raised alert into Homie v4's 'alert' state is the v4 adapter's job, and it
            // is one-way.
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();
            device.TryChangeState(DeviceState.Connecting);
            device.TryChangeState(DeviceState.Ready);

            device.RaiseAlert("battery", "Battery is low, at 8%");

            Assert.AreEqual((int)DeviceState.Ready, (int)device.State);
            Assert.IsTrue(device.HasAlerts);
        }

        [TestMethod]
        public void Device_Announces_An_Alert_Only_When_The_Raised_Set_Changed()
        {
            // A device raising an alert from inside its measurement loop calls this every
            // few seconds. Republishing the same message each time would be noise on the
            // broker and, retained, would rewrite the same value forever.
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();

            var changes = 0;
            var lastRaised = false;
            string? lastMessage = null;
            device.OnAlertChange += (AlertChangeEventArgs args) =>
            {
                changes++;
                lastRaised = args.IsRaised;
                lastMessage = args.Message;
            };

            device.RaiseAlert("battery", "Battery is low, at 8%");
            Assert.AreEqual(1, changes);
            Assert.IsTrue(lastRaised);

            device.RaiseAlert("battery", "Battery is low, at 8%");
            Assert.AreEqual(1, changes, "re-raising an unchanged alert must not be announced again");

            device.RaiseAlert("battery", "Battery is low, at 5%");
            Assert.AreEqual(2, changes, "a new message is a change");
            Assert.AreEqual("Battery is low, at 5%", lastMessage);

            device.ClearAlert("battery");
            Assert.AreEqual(3, changes);
            Assert.IsFalse(lastRaised);
            Assert.IsNull(lastMessage, "a cleared alert has no message");

            device.ClearAlert("battery");
            Assert.AreEqual(3, changes, "clearing an alert that is not raised must not be announced");
        }

        [TestMethod]
        public void Device_Holds_Alert_Ids_To_The_Same_Rule_As_Every_Other_Id()
        {
            // Homie v5 publishes the alert id as a topic level, so an id no topic could
            // carry has to be refused here rather than discovered on the wire.
            var device = new DeviceBuilder(_deviceId, _deviceName).BuildDevice();

            Assert.ThrowsException(typeof(ArgumentException), () => device.RaiseAlert("Battery", "nope"));
            Assert.ThrowsException(typeof(ArgumentException), () => device.RaiseAlert("battery low", "nope"));
            Assert.IsFalse(device.HasAlerts);
        }

        [TestMethod]
        public void Device_Lists_Exactly_Its_Settable_Properties()
        {
            // The set an adapter has to subscribe a command topic for.
            var device = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 21.5)
                    .BuildProperty()
                    .AddIntegerProperty("speed", "Speed", 0)
                        .WithSettable(true)
                    .BuildProperty()
                .BuildNode()
                .AddNode("lights", "Lights", "LED")
                    .AddBooleanProperty("on", "On", false)
                        .WithSettable(true)
                    .BuildProperty()
                .BuildNode()
                .BuildDevice();

            var settable = device.GetAllSettableProperties();

            Assert.AreEqual(2, settable.Length);
            foreach (var property in settable)
            {
                Assert.IsTrue(property.Settable);
            }
        }

        [TestMethod]
        public void Device_Refuses_Two_Nodes_With_The_Same_Id()
        {
            var builder = new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                .BuildNode()
                .AddNode(_nodeId, "A second engine", _nodeType)
                .BuildNode();

            Assert.ThrowsException(typeof(ArgumentException), () => builder.BuildDevice());
        }
    }
}
