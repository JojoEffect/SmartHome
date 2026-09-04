using SmartHome.HomeAssistant;
using SmartHome.Homie.V4;
using SmartHome.Homie.V4.Builder;
using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Properties;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.TestFramework;
using System.Text;

namespace SmartHome.UnitTests
{
    /// <summary>
    /// What the announcer puts on the wire, and when it puts it there again.
    /// </summary>
    /// <remarks>
    /// The re-announce paths are the ones worth pinning: a discovery message that is
    /// published once and never again looks perfect on the day it ships and disappears the
    /// first time the broker or Home Assistant restarts, which is the failure this whole
    /// class exists to prevent.
    /// </remarks>
    [TestClass]
    public class HomeAssistantAnnouncerTests
    {
        private const string _temperatureConfigTopic = "homeassistant/sensor/super-car-engine-temperature/config";

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
        public void Announce_Publishes_One_Config_Per_Property()
        {
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);

            var announced = announcer.Announce();

            Assert.IsTrue(announced, "the announce reported success");
            Assert.AreEqual(2, mqttClient.PublishCount, "one discovery config per property");
            Assert.AreEqual(1, mqttClient.PayloadsFor(_temperatureConfigTopic).Length, "the sensor config went out");
        }

        [TestMethod]
        public void Announce_Opens_No_Connection_Of_Its_Own()
        {
            // The design in one assertion: the announcer borrows the session Homie
            // connected. If it ever called Connect() it would need a second client id, a
            // second keep-alive and a last will MQTT has already spent on Homie's $state.
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);

            announcer.Announce();

            Assert.IsNull(mqttClient.ConnectedClientId, "no CONNECT was issued");
            Assert.IsFalse(mqttClient.WillFlag, "no second last will was declared");
        }

        [TestMethod]
        public void Attach_Subscribes_To_The_Home_Assistant_Status_Topic()
        {
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);

            announcer.Attach();

            Assert.AreEqual(1, mqttClient.SubscriptionCount, "subscribed to homeassistant/status");
        }

        [TestMethod]
        public void Attach_Is_Idempotent()
        {
            // Two registrations of the same handler would publish every config twice on
            // each reconnect, which reads at the broker exactly like a device announcing
            // in a loop.
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);

            announcer.Attach();
            announcer.Attach();
            mqttClient.RaiseConnectionOpened();

            Assert.AreEqual(1, mqttClient.SubscriptionCount, "subscribed once");
            Assert.AreEqual(2, mqttClient.PublishCount, "one re-announce, not two");
        }

        [TestMethod]
        public void Re_Announces_When_The_Connection_Reopens()
        {
            // A broker that restarted has an empty retained store, so the configs are
            // gone even though the client reconnected cleanly -- the same reason
            // HomieClient re-announces the Homie tree on this event.
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);
            announcer.Attach();

            mqttClient.RaiseConnectionOpened();

            Assert.AreEqual(2, mqttClient.PublishCount, "both configs went out again");
        }

        [TestMethod]
        public void Re_Announces_When_Home_Assistant_Comes_Online()
        {
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);
            announcer.Attach();

            RaiseStatus(mqttClient, "online");

            Assert.AreEqual(2, mqttClient.PublishCount, "the birth message triggered a re-announce");
        }

        [TestMethod]
        public void Ignores_Home_Assistant_Going_Offline()
        {
            // "offline" is Home Assistant's own will. It says the consumer went away, not
            // that this device did, and re-announcing into a broker nobody is reading is
            // just traffic.
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);
            announcer.Attach();

            RaiseStatus(mqttClient, "offline");

            Assert.AreEqual(0, mqttClient.PublishCount, "nothing was republished");
        }

        [TestMethod]
        public void Ignores_Traffic_On_Other_Topics()
        {
            // The MQTT client is shared with HomieClient, so this handler sees every
            // message the device receives -- including the /set commands that are not its
            // business.
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);
            announcer.Attach();

            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(
                "homie/super-car/engine/setpoint/set",
                Encoding.UTF8.GetBytes("online"),
                false,
                MqttQoSLevel.AtLeastOnce,
                false));

            Assert.AreEqual(0, mqttClient.PublishCount, "an unrelated topic changed nothing");
        }

        [TestMethod]
        public void Detach_Stops_The_Re_Announcing()
        {
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);
            announcer.Attach();
            announcer.Detach();

            mqttClient.RaiseConnectionOpened();
            RaiseStatus(mqttClient, "online");

            Assert.AreEqual(0, mqttClient.PublishCount, "neither path fired after Detach");
        }

        [TestMethod]
        public void Remove_Publishes_An_Empty_Payload_To_Every_Config_Topic()
        {
            // An empty retained payload is how MQTT deletes a retained message, and how
            // Home Assistant deletes the entity. The two are the same act, which is why
            // there is one method rather than a delete and a cleanup.
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out _), mqttClient);

            announcer.Remove();

            Assert.AreEqual(2, mqttClient.PublishCount, "one withdrawal per property");
            var payloads = mqttClient.PayloadsFor(_temperatureConfigTopic);
            Assert.AreEqual(1, payloads.Length, "the sensor's config topic was addressed");
            Assert.AreEqual(string.Empty, payloads[0], "the payload is empty");
        }

        [TestMethod]
        public void An_Excluded_Property_Is_Never_Published()
        {
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out var humidity), mqttClient);

            announcer.Exclude(humidity);
            announcer.Announce();

            Assert.AreEqual(1, mqttClient.PublishCount, "only the temperature config went out");
        }

        [TestMethod]
        public void A_Device_Class_Override_Reaches_The_Payload()
        {
            var mqttClient = new MockMqttClient();
            var announcer = new HomeAssistantAnnouncer(BuildDevice(out var humidity), mqttClient);

            announcer.SetDeviceClass(humidity, DeviceClass.Humidity);
            announcer.Announce();

            var payloads = mqttClient.PayloadsFor("homeassistant/sensor/super-car-engine-humidity/config");
            Assert.AreEqual(1, payloads.Length, "the humidity config went out");
            Assert.IsTrue(payloads[0].IndexOf("\"dev_cla\":\"humidity\"") >= 0, $"the override is missing from '{payloads[0]}'");
        }

        private static void RaiseStatus(MockMqttClient mqttClient, string payload) =>
            mqttClient.RaisePublishReceived(new MqttMsgPublishEventArgs(
                HomeAssistantTopics.StatusTopic,
                Encoding.UTF8.GetBytes(payload),
                false,
                MqttQoSLevel.AtLeastOnce,
                false));

        private static Device BuildDevice(out FloatProperty humidity) =>
            new HomieDeviceBuilder("super-car", "Super car")
                .AddNode("engine", "Engine", "V8")
                    .AddFloatProperty("temperature", "Temperature", 0.0)
                        .WithUnit(Unit.DegreeCelsius)
                    .BuildProperty(out _)
                    .AddFloatProperty("humidity", "Humidity", 0.0)
                        .WithUnit(Unit.Percent)
                    .BuildProperty(out humidity)
                .BuildNode()
                .BuildDevice();
    }
}
