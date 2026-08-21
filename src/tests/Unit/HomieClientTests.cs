using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System.Text;

namespace SmartHome.UnitTests
{
    [TestClass]
    public class HomieClientTests
    {
        private const string _testDeviceTopicId = "super-car";
        private const string _testDeviceName = "Super car";

        private const string _testNodeWheelsTopicId = "wheels";
        private const string _testNodeWheelsName = "Wheels";
        private const string _testNodeWheelsType = "Gum";

        private const string _testNodeEngineTopicId = "engine";
        private const string _testNodeEngineName = "Engine";
        private const string _testNodeEngineType = "V8";

        private const string _testNodeLightsTopicId = "ligths";
        private const string _testNodeLightsName = "Ligths";
        private const string _testNodeLightsType = "LED";

        private const string _testPropertyAngleTopicId = "angle";
        private const string _testPropertyAngleName = "Angle";

        private const string _testPropertySpeedTopicId = "speed";
        private const string _testPropertySpeedName = "Speed";

        private const string _testPropertyDirectionTopicId = "direction";
        private const string _testPropertyDirectionName = "Direction";

        private const string _testPropertyTemperatureTopicId = "temperature";
        private const string _testPropertyTemperatureName = "Temperature";

        private const string _testPropertyIntensityTopicId = "intensity";
        private const string _testPropertyIntensityName = "Intensity";

        private const string _testPropertyColorTopicId = "color";
        private const string _testPropertyColorName = "Color";

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
        public void HomieClient_Publish_On_Property_Update()
        {
            // Arrange
            int expectedPublishCount = 16;
            int expectedSubscriptionCount = 0;

            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();


            // Act
            var homieClient = new HomieClient(device, mqttClient);

            homieClient.Connect();

            property.Update(10);

            // Assert
            Assert.AreEqual(expectedPublishCount, mqttClient.PublishCount);
            Assert.AreEqual(expectedSubscriptionCount, mqttClient.SubscriptionCount);
        }

        [TestMethod]
        public void HomieClient_Property_Is_Set_On_Property_Set_Message()
        {
            // Arrange
            int expectedPublishCount = 16;
            int expectedSubscriptionCount = 1;
            double initialValue = 0.0;
            double expectedValue = 25;

            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, initialValue)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            // Assert
            Assert.AreEqual(initialValue, property.Value);

            // Act
            var homieClient = new HomieClient(device, mqttClient);

            homieClient.Connect();
            var commandTopic = $"{property.GetTopic()}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(commandTopic, Encoding.UTF8.GetBytes(expectedValue.ToString()), false, MqttQoSLevel.AtLeastOnce, false));

            // Assert
            Assert.AreEqual(expectedValue, property.Value);
            Assert.AreEqual(expectedPublishCount, mqttClient.PublishCount);
            Assert.AreEqual(expectedSubscriptionCount, mqttClient.SubscriptionCount);
        }

        [TestMethod]
        public void HomieClient_Connect_Declares_HomieLastWill()
        {
            // Homie v4 requires the connection to carry a last will setting
            // homie/[device-id]/$state to 'lost', retained. A will can only be declared
            // in CONNECT, so the Homie client has to own the session -- RoomSensor used
            // to connect the transport itself first, which silently produced a session
            // with no will at all.

            // Arrange
            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            var connected = homieClient.Connect();

            // Assert
            Assert.IsTrue(connected);
            Assert.IsTrue(mqttClient.WillFlag);
            Assert.AreEqual($"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}{Constants.TopicSeparator}{Constants.StateAttributeTopicId}", mqttClient.WillTopic);
            Assert.AreEqual("lost", mqttClient.WillMessage);
            Assert.IsTrue(mqttClient.WillRetain);
        }

        [TestMethod]
        public void HomieClient_Connect_Replaces_A_Foreign_Session()
        {
            // A session opened by someone else cannot carry the Homie will, so the
            // client must replace it rather than continue on it.

            // Arrange
            var mqttClient = new MockMqttClient();
            mqttClient.Connect("someone-else");

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            var connected = homieClient.Connect();

            // Assert
            Assert.IsTrue(connected);
            Assert.IsTrue(mqttClient.WillFlag);
            Assert.AreEqual("lost", mqttClient.WillMessage);
        }

        [TestMethod]
        public void HomieClient_Lifecycle_States_Are_Reachable()
        {
            // All six $state values are part of the convention, but alert and sleeping
            // used to be unreachable from outside the library: Device.TryChangeState is
            // internal, and the client exposed no way to ask for them.

            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();

            // Assert -- Connect leaves the device ready
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);

            // Act + Assert -- ready -> sleeping -> ready
            Assert.IsTrue(homieClient.Sleep());
            Assert.AreEqual((int)State.Sleeping, (int)homieClient.State);
            Assert.IsTrue(homieClient.Ready());
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);

            // Act + Assert -- ready -> alert -> ready
            Assert.IsTrue(homieClient.Alert());
            Assert.AreEqual((int)State.Alert, (int)homieClient.State);
            Assert.IsTrue(homieClient.Ready());
            Assert.AreEqual((int)State.Ready, (int)homieClient.State);
        }

        [TestMethod]
        public void HomieClient_Refuses_An_Illegal_State_Transition()
        {
            // Arrange
            var mqttClient = new MockMqttClient();
            var homieClient = new HomieClient(BuildSinglePropertyDevice(), mqttClient);
            homieClient.Connect();
            homieClient.Alert();

            // Act -- alert may go to ready or disconnected, never straight to sleeping
            var slept = homieClient.Sleep();

            // Assert
            Assert.IsFalse(slept);
            Assert.AreEqual((int)State.Alert, (int)homieClient.State);
        }

        [TestMethod]
        public void HomieClient_Raises_OnCommand_For_A_Controller_Set()
        {
            // property.OnUpdate fires both when a controller sets a value and when the
            // device updates its own, so an actuator cannot act on it. OnCommand fires
            // only for the former.

            // Arrange
            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyIntensityTopicId, _testPropertyIntensityName, 0.0)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);
            homieClient.Connect();

            var commandCount = 0;
            PropertyBase commandedProperty = null;
            homieClient.OnCommand += (args) =>
            {
                commandCount++;
                commandedProperty = args.Property;
            };

            // Act -- the device updating itself is not a command
            property.Update(42.0);

            // Assert
            Assert.AreEqual(0, commandCount);

            // Act -- a controller writing to /set is
            var setTopic = $"{property.GetTopic()}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(setTopic, Encoding.UTF8.GetBytes("73"), false, MqttQoSLevel.AtLeastOnce, false));

            // Assert
            Assert.AreEqual(1, commandCount);
            Assert.IsNotNull(commandedProperty);
            Assert.AreEqual(73.0, property.Value);
        }

        private Device BuildSinglePropertyDevice()
        {
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            return builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();
        }

        [TestMethod]
        public void HomieClient_Connect_Disconnect()
        {
            // Arrange
            int expectedPublishCountAfterConnect = 22;
            int expectedPublishCountAfterDisconnect = 23;
            int expectedSubscriptionCountConnected = 2;
            int expectedSubscriptionCountDisconnected = 0;

            var mqttClient = new MockMqttClient();

            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var device = builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                            .WithSettable(true)
                        .BuildProperty()
                        .AddFloatProperty(_testPropertyIntensityTopicId, _testPropertyIntensityName, 100.0)
                            .WithSettable(true)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();

            var homieClient = new HomieClient(device, mqttClient);

            // Act
            homieClient.Connect();

            // Assert
            Assert.AreEqual(expectedSubscriptionCountConnected, mqttClient.SubscriptionCount);
            Assert.AreEqual(expectedPublishCountAfterConnect, mqttClient.PublishCount);

            // Act
            homieClient.Disconnect();

            // Assert
            Assert.AreEqual(expectedSubscriptionCountDisconnected, mqttClient.SubscriptionCount);
            Assert.AreEqual(expectedPublishCountAfterDisconnect, mqttClient.PublishCount);
        }
    }
}
