// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System.Device.Wifi;

namespace Test
{
    // Minimal network + MQTT connect validation: no HomieMqttClient, no Homie protocol,
    // no reconnect/retry logic. A single Connect() call, live or die -- isolates whether
    // the WSAEWOULDBLOCK connect failure is in RoomSensor/HomieNano's own code or purely
    // upstream (nanoFramework.M2Mqtt / System.Net / firmware).
    public class Program
    {
        private const string BrokerHost = "192.168.1.238";
        private const string StatusTopic = "smarthome-test/status";
        private const string HeartbeatTopic = "smarthome-test/heartbeat";
        private const string EchoTopic = "smarthome-test/echo";

        public static void Main()
        {
            SetupAndConnectNetwork();

            Debug.WriteLine($"Connecting MQTT to {BrokerHost}...");
            var client = new MqttClient(BrokerHost);
            var clientId = Guid.NewGuid().ToString();

            // Deliberately no retry/try-catch here -- a bare Connect() call, same as
            // isolating the failure requires. Let it throw if it throws.
            client.Connect(clientId);
            Debug.WriteLine("MQTT connected.");

            client.Subscribe(new[] { EchoTopic }, new[] { MqttQoSLevel.AtLeastOnce });
            client.MqttMsgPublishReceived += HandleIncomingMessage;

            client.Publish(StatusTopic, Encoding.UTF8.GetBytes("connected"), null, null, MqttQoSLevel.AtLeastOnce, false);
            Debug.WriteLine("Published initial status.");

            int counter = 0;
            while (true)
            {
                counter++;
                Debug.WriteLine($"Publishing heartbeat #{counter}...");
                client.Publish(HeartbeatTopic, Encoding.UTF8.GetBytes(counter.ToString()), null, null, MqttQoSLevel.AtMostOnce, false);
                Thread.Sleep(5000);
            }
        }

        private static void HandleIncomingMessage(object sender, MqttMsgPublishEventArgs e)
        {
            Debug.WriteLine($"Message received: {Encoding.UTF8.GetString(e.Message, 0, e.Message.Length)}");
        }

        /// <summary>
        /// This is a helper function to pick up first available network interface and use it for communication.
        /// </summary>
        private static void SetupAndConnectNetwork()
        {
            // Get the first WiFI Adapter
            var wifiAdapter = WifiAdapter.FindAllAdapters()[0];

            // Begin network scan.
            wifiAdapter.ScanAsync();

            // While networks are being scan, continue on configuration. If networks were set previously,
            // board may already be auto-connected, so reconnection is not even needed.
            var wiFiConfiguration = Wireless80211Configuration.GetAllWireless80211Configurations()[0];
            var ipAddress = NetworkInterface.GetAllNetworkInterfaces()[0].IPv4Address;
            var needToConnect = string.IsNullOrEmpty(ipAddress) || (ipAddress == "0.0.0.0");
            while (needToConnect)
            {
                foreach (var network in wifiAdapter.NetworkReport.AvailableNetworks)
                {
                    // Show all networks found
                    Debug.WriteLine($"Net SSID :{network.Ssid},  BSSID : {network.Bsid},  rssi : {network.NetworkRssiInDecibelMilliwatts},  signal : {network.SignalBars}");

                    // If its our Network then try to connect
                    if (network.Ssid == wiFiConfiguration.Ssid)
                    {

                        var result = wifiAdapter.Connect(network, WifiReconnectionKind.Automatic, wiFiConfiguration.Password);

                        if (result.ConnectionStatus == WifiConnectionStatus.Success)
                        {
                            Debug.WriteLine($"Connected to Wifi network {network.Ssid}.");
                            needToConnect = false;
                        }
                        else
                        {
                            Debug.WriteLine($"Error {result.ConnectionStatus} connecting to Wifi network {network.Ssid}.");
                        }
                    }
                }

                Thread.Sleep(10000);
            }

            ipAddress = NetworkInterface.GetAllNetworkInterfaces()[0].IPv4Address;
            Debug.WriteLine($"Connected to Wifi network with IP address {ipAddress}");
        }
    }
}
