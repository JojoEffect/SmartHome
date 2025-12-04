using HomieNano.Version4;
using HomieNano.Version4.Builder;
using HomieNano.Version4.Enums;
using HomieNano.Version4.Properties;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System.Text;

namespace NFUnitTest
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


        [TestMethod]
        public void HomieClient_Publish_On_Property_Update()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            int expectedPublishCount = 1;
            int expectedSubscriptionCount = 0;

            var mqttClient = new MockMqttClient();
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
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            int expectedPublishCount = 0;
            int expectedSubscriptionCount = 1;
            double initialValue = 0.0;
            double expectedValue = 25;

            var mqttClient = new MockMqttClient();
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
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(property.GetTopic(), Encoding.UTF8.GetBytes(expectedValue.ToString()), false, MqttQoSLevel.AtLeastOnce, false));

            // Assert
            Assert.AreEqual(expectedValue, property.Value);
            Assert.AreEqual(expectedPublishCount, mqttClient.PublishCount);
            Assert.AreEqual(expectedSubscriptionCount, mqttClient.SubscriptionCount);
        }
    }
}
