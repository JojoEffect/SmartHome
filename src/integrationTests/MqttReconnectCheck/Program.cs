using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.M2Mqtt.Messages;
using SmartHome.Mqtt;
using SmartHome.Networking;

namespace SmartHome.IntegrationTests.MqttReconnectCheck
{
    // Publishes a heartbeat forever through ReconnectingMqttClient, so the host can take
    // the broker away underneath it and watch whether the device comes back.
    //
    // Unlike the other checks this one emits NO [ITEST] marker. Its verdict is
    // host-side by design: the device claiming "I reconnected" is weaker evidence
    // than a message actually arriving at the recreated broker, and a device-side
    // marker would be a second, competing verdict. scripts\Run-IntegrationTests.ps1
    // runs it as a BrokerOutage-kind test and decides from homie/# traffic.
    //
    // What is under test is ReconnectingMqttClient's auto-reconnect: Connect() enables it,
    // hooks ConnectionClosed, and retries every 5s, re-subscribing cached topics.
    // The heartbeat is published non-retained on purpose -- a retained message would
    // be delivered to any fresh subscriber by the broker itself, which would look
    // exactly like the device having republished when it had not.
    public class Program
    {
        private const string BrokerHost = "192.168.1.238";
        private const string HeartbeatTopic = "homie/mqtt-reconnect-check/heartbeat";
        private const int HeartbeatIntervalMs = 2000;

        public static void Main()
        {
            // ReconnectingMqttClient logs through LogDispatcher; without a factory its
            // logger calls would go nowhere useful, and the reconnect path is
            // exactly what someone will want to read when this test fails.
            LogDispatcher.LoggerFactory = new DebugLoggerFactory();

            NetworkHelper.ConnectToConfiguredNetwork();

            var client = new ReconnectingMqttClient(BrokerHost);
            ConnectWithRetry(client);

            var counter = 0;
            while (true)
            {
                counter++;

                try
                {
                    var payload = $"heartbeat {counter}";
                    client.Publish(
                        HeartbeatTopic,
                        Encoding.UTF8.GetBytes(payload),
                        null,
                        null,
                        MqttQoSLevel.AtMostOnce,
                        false);

                    Debug.WriteLine($"MqttReconnectCheck: published {payload}");
                }
                catch (Exception ex)
                {
                    // Expected while the broker is down: publishing through a dead
                    // socket throws. Keep the loop alive so that when the reconnect
                    // handler gets the client back, heartbeats resume on their own --
                    // that resumption is the whole point of the test.
                    Debug.WriteLine($"MqttReconnectCheck: publish failed ({ex.Message}); waiting for reconnect.");
                }

                Thread.Sleep(HeartbeatIntervalMs);
            }
        }

        // The runner cycles the broker before it starts measuring, so this app can
        // easily boot while there is no broker to connect to. A bare Connect() would
        // throw straight out of Main, the CLR would restart the app, and the test
        // would then be measuring reboot loops instead of reconnects.
        private static void ConnectWithRetry(ReconnectingMqttClient client)
        {
            const int maxAttempts = 20;
            const int retryDelayMs = 3000;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Debug.WriteLine($"MqttReconnectCheck: connecting to {BrokerHost} (attempt {attempt}/{maxAttempts})...");
                    client.Connect("MqttReconnectCheck");
                    Debug.WriteLine("MqttReconnectCheck: connected; publishing heartbeats.");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MqttReconnectCheck: connect attempt {attempt} failed ({ex.Message}).");

                    if (attempt == maxAttempts)
                    {
                        throw;
                    }

                    Thread.Sleep(retryDelayMs);
                }
            }
        }
    }
}
