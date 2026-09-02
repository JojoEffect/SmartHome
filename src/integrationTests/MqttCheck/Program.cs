using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using SmartHome.Networking;
using SmartHome.IntegrationTests.TestSupport;

namespace SmartHome.IntegrationTests.MqttCheck
{
    // Round-trip pub/sub check through the local Mosquitto broker (see
    // scripts\Start-DevEnv.ps1). Uses the same WiFi setup as WifiTest, then
    // subscribes and publishes to the SAME topic -- the broker delivers a
    // client's own publish back to it when subscribed, so a message arriving
    // in HandleIncomingMessage proves both the publish and the subscribe path
    // work, through the broker, with just this one device.
    //
    // The IntegrationTest.Pass/Fail markers are what scripts\Run-IntegrationTests.ps1
    // greps for -- PASS is emitted on the first message that comes back, so the
    // marker means "the round trip actually completed", not just "we connected".
    public class Program
    {
        private const string BrokerHost = "192.168.1.238";
        private const string TestTopic = "smarthome-test/echo";

        private static int _receivedCount;

        public static void Main()
        {
            try
            {
                NetworkHelper.ConnectToConfiguredNetwork();

                Debug.WriteLine($"Connecting MQTT to {BrokerHost}...");
                var client = new MqttClient(BrokerHost);
                var clientId = Guid.NewGuid().ToString();
                client.Connect(clientId);
                Debug.WriteLine("MQTT connected.");

                client.MqttMsgPublishReceived += HandleIncomingMessage;
                client.Subscribe(new[] { TestTopic }, new[] { MqttQoSLevel.AtLeastOnce });
                Debug.WriteLine($"Subscribed to {TestTopic}.");

                int counter = 0;
                while (true)
                {
                    counter++;
                    var payload = $"MqttTest round-trip #{counter}";
                    Debug.WriteLine($"Publishing: {payload}");
                    client.Publish(TestTopic, Encoding.UTF8.GetBytes(payload), null, null, MqttQoSLevel.AtLeastOnce, false);

                    Thread.Sleep(5000);
                }
            }
            catch (Exception ex)
            {
                IntegrationTest.Fail(typeof(Program), ex.Message);
            }
        }

        private static void HandleIncomingMessage(object sender, MqttMsgPublishEventArgs e)
        {
            _receivedCount++;
            var text = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);
            Debug.WriteLine($"Received (#{_receivedCount}) on {e.Topic}: {text}");

            if (_receivedCount == 1)
            {
                IntegrationTest.Pass(typeof(Program), $"round-trip through {BrokerHost} on {TestTopic}");
            }
        }
    }
}
