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
using nanoFramework.Hardware.Esp32;
using System.Device.I2c;
using Iot.Device.Bmxx80;
using Iot.Device.Common;
using UnitsNet;
using Iot.Device.Bmxx80.FilteringMode;


namespace Test
{
    public class Program
    {
        public static void Main()
        {

            // STEP 1: setup network
            // You need to set Wifi connection credentials in the configuration first!
            // Go to Device Explorer -> Edit network configuration -> Wifi proiles and set SSID and password there.
            SetupAndConnectNetwork();

            // STEP 2: connect to MQTT broker
            // Warning: test.mosquitto.org is very slow and congested, and is only suitable for very basic validation testing.
            // Change it to your local broker as soon as possible.
            var client = new MqttClient("192.168.1.22");
            var clientId = Guid.NewGuid().ToString();
            client.Connect(clientId);

            // STEP 3: subscribe to topics you want
            client.Subscribe(new[] { "test/topic" }, new[] { MqttQoSLevel.AtLeastOnce });
            client.MqttMsgPublishReceived += HandleIncomingMessage;

            Debug.WriteLine("Hello Bme280!");

            //////////////////////////////////////////////////////////////////////
            // when connecting to an ESP32 device, need to configure the I2C GPIOs
            // used for the bus
            Configuration.SetPinFunction(21, DeviceFunction.I2C1_DATA);
            Configuration.SetPinFunction(22, DeviceFunction.I2C1_CLOCK);

            // bus id on the MCU
            const int busId = 1;
            // set this to the current sea level pressure in the area for correct altitude readings
            Pressure defaultSeaLevelPressure = WeatherHelper.MeanSeaLevel;

            I2cConnectionSettings i2cSettings = new(busId, Bme280.SecondaryI2cAddress);
            using I2cDevice i2cDevice = I2cDevice.Create(i2cSettings);
            using Bme280 bme80 = new Bme280(i2cDevice)
            {
                // set higher sampling
                TemperatureSampling = Sampling.LowPower,
                PressureSampling = Sampling.UltraHighResolution,
                HumiditySampling = Sampling.Standard,
            };

            while (true)
            {
                // Perform a synchronous measurement
                var readResult = bme80.Read();

                // Note that if you already have the pressure value and the temperature, you could also calculate altitude by using
                // var altValue = WeatherHelper.CalculateAltitude(preValue, defaultSeaLevelPressure, tempValue) which would be more performant.
                bme80.TryReadAltitude(defaultSeaLevelPressure, out var altValue);

                if (readResult.TemperatureIsValid)
                {
                    Debug.WriteLine($"Temperature: {readResult.Temperature.DegreesCelsius}\u00B0C");
                }
                if (readResult.PressureIsValid)
                {
                    Debug.WriteLine($"Pressure: {readResult.Pressure.Hectopascals}hPa");
                }

                if (readResult.TemperatureIsValid && readResult.PressureIsValid)
                {
                    Debug.WriteLine($"Altitude: {altValue.Meters}m");
                }

                if (readResult.HumidityIsValid)
                {
                    Debug.WriteLine($"Relative humidity: {readResult.Humidity.Percent}%");
                }

                // WeatherHelper supports more calculations, such as saturated vapor pressure, actual vapor pressure and absolute humidity.
                if (readResult.TemperatureIsValid && readResult.HumidityIsValid)
                {
                    Debug.WriteLine($"Heat index: {WeatherHelper.CalculateHeatIndex(readResult.Temperature, readResult.Humidity).DegreesCelsius}\u00B0C");
                    Debug.WriteLine($"Dew point: {WeatherHelper.CalculateDewPoint(readResult.Temperature, readResult.Humidity).DegreesCelsius}\u00B0C");
                }

                Thread.Sleep(1000);

                // change sampling and filter
                bme80.TemperatureSampling = Sampling.UltraHighResolution;
                bme80.PressureSampling = Sampling.UltraLowPower;
                bme80.HumiditySampling = Sampling.UltraLowPower;
                bme80.FilterMode = Bmx280FilteringMode.X2;

                // Perform an asynchronous measurement
                readResult = bme80.Read();

                // Note that if you already have the pressure value and the temperature, you could also calculate altitude by using
                // var altValue = WeatherHelper.CalculateAltitude(preValue, defaultSeaLevelPressure, tempValue) which would be more performant.
                bme80.TryReadAltitude(defaultSeaLevelPressure, out altValue);

                if (readResult.TemperatureIsValid)
                {
                    Debug.WriteLine($"Temperature: {readResult.Temperature.DegreesCelsius}\u00B0C");
                }
                if (readResult.PressureIsValid)
                {
                    Debug.WriteLine($"Pressure: {readResult.Pressure.Hectopascals}hPa");
                }

                Debug.WriteLine($"Altitude: {altValue.Meters}m");

                if (readResult.HumidityIsValid)
                {
                    Debug.WriteLine($"Relative humidity: {readResult.Humidity.Percent}%");
                }

                // WeatherHelper supports more calculations, such as saturated vapor pressure, actual vapor pressure and absolute humidity.
                if (readResult.TemperatureIsValid && readResult.HumidityIsValid)
                {
                    Debug.WriteLine($"Heat index: {WeatherHelper.CalculateHeatIndex(readResult.Temperature, readResult.Humidity).DegreesCelsius}\u00B0C");
                    Debug.WriteLine($"Dew point: {WeatherHelper.CalculateDewPoint(readResult.Temperature, readResult.Humidity).DegreesCelsius}\u00B0C");
                }

                client.Publish("test/topic", Encoding.UTF8.GetBytes("===== Hello MQTT! ====="), null, null, MqttQoSLevel.AtLeastOnce, false);

                Thread.Sleep(5000);
            }

            // STEP 5: disconnecting
            client.Disconnect();
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