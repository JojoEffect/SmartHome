using HomieNano.Version4;
using HomieNano.Version4.Builder;
using HomieNano.Version4.Enums;
using HomieNano.Version4.Properties;
using nanoFramework.TestFramework;
using System.Text;

namespace NFUnitTest
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
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{Constants.Version4}" +
                $"{Constants.TopicSeparator}{_testDeviceTopicId}";

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
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{Constants.Version4}" +
                $"{Constants.TopicSeparator}{_testDeviceTopicId}" +
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
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{Constants.Version4}" +
                $"{Constants.TopicSeparator}{_testDeviceTopicId}" +
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
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{Constants.Version4}" +
                $"{Constants.TopicSeparator}{_testDeviceTopicId}" +
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
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{Constants.Version4}" +
                $"{Constants.TopicSeparator}{_testDeviceTopicId}" +
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
            var expectedTopic = $"{Constants.RootTopicId}{Constants.TopicSeparator}{Constants.Version4}" +
                $"{Constants.TopicSeparator}{_testDeviceTopicId}" +
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
    }
}
