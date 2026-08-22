using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;
using System;
using nanoFramework.TestFramework;
using System.Text;

namespace SmartHome.UnitTests
{
    [TestClass]
    public class DeviceBuilderTests
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
        public void Device_GetTopic_Valid()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}";

            // Act
            var device = builder.BuildDevice();

            // Assert
            Assert.AreEqual(expectedTopic, device.GetTopic());
        }

        [TestMethod]
        public void Node_GetTopic_Valid()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}" +
                $"{Constants.TopicSeparator}{_testNodeEngineTopicId}";

            // Act
            builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                   .BuildNode(out Node node)
                .BuildDevice();

            // Assert
            Assert.AreEqual(expectedTopic, node.GetTopic());
        }

        [TestMethod]
        public void Node_Attribute_GetTopic_Valid()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}" +
                $"{Constants.TopicSeparator}{_testNodeLightsTopicId}" +
                $"{Constants.TopicSeparator}{Constants.TypeAttributeTopicId}";

            // Act
            builder.AddNode(_testNodeLightsTopicId, _testNodeLightsName, _testNodeLightsType)
                   .BuildNode(out Node node)
                .BuildDevice();

            // Assert
            Assert.AreEqual(expectedTopic, node.TypeAttribute.GetTopic());
        }

        [TestMethod]
        public void Property_GetTopic_Valid()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}" +
                $"{Constants.TopicSeparator}{_testNodeEngineTopicId}" +
                $"{Constants.TopicSeparator}{_testPropertyTemperatureTopicId}";

            // Act
            builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            // Assert
            Assert.AreEqual(expectedTopic, property.GetTopic());
        }

        [TestMethod]
        public void Property_Attribute_GetTopic_Valid()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}" +
                $"{Constants.TopicSeparator}{_testNodeEngineTopicId}" +
                $"{Constants.TopicSeparator}{_testPropertyIntensityTopicId}" +
                $"{Constants.TopicSeparator}{Constants.UnitAttributeTopicId}";

            // Act
            builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyIntensityTopicId, _testPropertyIntensityName, 0.0)
                            .WithUnit(Unit.None)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            // Assert
            Assert.AreEqual(expectedTopic, property.UnitAttribute.GetTopic());
        }

        [TestMethod]
        public void Device_WithoutImplementation_HasNullImplementation()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);

            // Act
            var device = builder.BuildDevice();

            // Assert
            Assert.IsNull(device.ImplementationAttribute);
        }

        [TestMethod]
        public void Device_WithImplementation_SetsImplementation()
        {
            // Arrange
            var expectedImplementation = "impl";
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{_testDeviceTopicId}" +
                $"{Constants.TopicSeparator}{Constants.ImplementationAttributeTopicId}";

            // Act
            var device = builder
                    .WithImplementation(expectedImplementation)
                .BuildDevice();

            // Assert
            Assert.IsNotNull(device.ImplementationAttribute);
            Assert.AreEqual(expectedImplementation, device.ImplementationAttribute.Value);
            Assert.AreEqual(expectedTopic, device.ImplementationAttribute.GetTopic());
        }

        [TestMethod]
        public void BuildDevice_ReturnsNewInstanceOnEachCall()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);

            // Act
            var device1 = builder.BuildDevice();
            var device2 = builder.BuildDevice();

            // Assert
            Assert.IsFalse(object.ReferenceEquals(device1, device2));
        }

        [TestMethod]
        public void Property_Update_RaiseOnUpdate()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            bool handlerCalled = false;
            double expectedValue = 10;

            // Act
            builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            property.OnUpdate += (args) =>
            {
                handlerCalled = true;
            };

            property.Update(expectedValue);

            // Assert
            Assert.AreEqual(expectedValue, property.Value);
            Assert.IsTrue(handlerCalled);
        }

        [TestMethod]
        public void Property_Set_RaiseOnUpdate()
        {
            // Arrange
            var builder = new HomieDeviceBuilder(_testDeviceTopicId, _testDeviceName);
            bool handlerCalled = false;
            double expectedValue = 10;

            // Act
            builder.AddNode(_testNodeEngineTopicId, _testNodeEngineName, _testNodeEngineType)
                        .AddFloatProperty(_testPropertyTemperatureTopicId, _testPropertyTemperatureName, 0.0)
                            .WithSettable(true)
                        .BuildProperty(out FloatProperty property)
                    .BuildNode()
                .BuildDevice();

            property.OnUpdate += (args) =>
            {
                handlerCalled = true;
            };

            var setValue = Encoding.UTF8.GetBytes(expectedValue.ToString());
            property.Set(setValue);

            // Assert
            Assert.AreEqual(expectedValue, property.Value);
            Assert.IsTrue(handlerCalled);
        }

        [TestMethod]
        public void TopicId_Rejects_Ids_The_Convention_Forbids()
        {
            // Spec: an id "MAY contain lowercase letters from a to z, numbers from 0 to 9
            // as well as the hyphen character", and "MUST NOT start or end with a hyphen".
            // Caught at construction, not on the wire.
            Assert.ThrowsException(typeof(ArgumentException), () => new HomieDeviceBuilder("Room-Sensor", "Uppercase"));
            Assert.ThrowsException(typeof(ArgumentException), () => new HomieDeviceBuilder("-leading", "Leading hyphen"));
            Assert.ThrowsException(typeof(ArgumentException), () => new HomieDeviceBuilder("trailing-", "Trailing hyphen"));
            Assert.ThrowsException(typeof(ArgumentException), () => new HomieDeviceBuilder("with space", "Space"));
            Assert.ThrowsException(typeof(ArgumentException), () => new HomieDeviceBuilder("under_score", "Underscore"));
            Assert.ThrowsException(typeof(ArgumentException), () => new HomieDeviceBuilder("", "Empty"));
        }

        [TestMethod]
        public void TopicId_Accepts_A_Valid_Id_At_Every_Level()
        {
            // Lowercase, digits and inner hyphens, on device, node and property alike.
            var device = new HomieDeviceBuilder("room-sensor-2", "Valid device")
                    .AddNode("sensor-1", "Valid node", "BMP280")
                        .AddFloatProperty("temperature-c", "Valid property", 0.0)
                        .BuildProperty()
                    .BuildNode()
                .BuildDevice();

            Assert.AreEqual("room-sensor-2", device.TopicId);
        }

        [TestMethod]
        public void TopicId_Rejects_An_Invalid_Node_Or_Property_Id()
        {
            var builder = new HomieDeviceBuilder("valid-device", "Device");

            Assert.ThrowsException(typeof(ArgumentException), () => builder.AddNode("Node", "Uppercase node", "type"));

            var node = new HomieDeviceBuilder("valid-device", "Device")
                .AddNode("valid-node", "Node", "type");

            Assert.ThrowsException(typeof(ArgumentException), () => node.AddFloatProperty("Temperature", "Uppercase property", 0.0));
        }

    }
}
