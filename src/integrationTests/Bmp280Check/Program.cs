// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using nanoFramework.Hardware.Esp32;
using System.Device.I2c;
using Iot.Device.Bmxx80;
using Iot.Device.Common;
using Iot.Device.Bmxx80.FilteringMode;
using SmartHome.IntegrationTests.TestSupport;

namespace SmartHome.IntegrationTests.Bmp280Check
{
    // Isolated sensor check: only verifies the BMP280 (Bme280 driver) reads valid
    // temperature/pressure/humidity over I2C. No WiFi, no MQTT.
    //
    // The IntegrationTest.Pass/Fail markers are what scripts\Run-IntegrationTests.ps1
    // greps for. IntegrationTest.cs is compiled in as a linked file rather than via a
    // ProjectReference to TestSupport on purpose: TestSupport references the WiFi and
    // networking assemblies, and this test is deliberately network-free.
    public class Program
    {
        private const string TestName = "Bmp280Check";

        public static void Main()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                IntegrationTest.Fail(TestName, ex.Message);
            }
        }

        private static void Run()
        {
            Debug.WriteLine("Hello Bme280!");

            //////////////////////////////////////////////////////////////////////
            // when connecting to an ESP32 device, need to configure the I2C GPIOs
            // used for the bus
            Configuration.SetPinFunction(21, DeviceFunction.I2C1_DATA);
            Configuration.SetPinFunction(22, DeviceFunction.I2C1_CLOCK);

            // bus id on the MCU
            const int busId = 1;
            // set this to the current sea level pressure in the area for correct altitude readings
            UnitsNet.Pressure defaultSeaLevelPressure = WeatherHelper.MeanSeaLevel;

            I2cConnectionSettings i2cSettings = new(busId, Bme280.SecondaryI2cAddress);
            using I2cDevice i2cDevice = I2cDevice.Create(i2cSettings);
            using Bme280 bme80 = new Bme280(i2cDevice)
            {
                // set higher sampling
                TemperatureSampling = Sampling.LowPower,
                PressureSampling = Sampling.UltraHighResolution,
                HumiditySampling = Sampling.Standard,
            };

            // The suite outcome is decided by the FIRST measurement only -- later
            // iterations are just there so a human watching the output can see the
            // sensor keep working.
            bool outcomeReported = false;

            while (true)
            {
                // Perform a synchronous measurement
                var readResult = bme80.Read();

                // Note that if you already have the pressure value and the temperature, you could also calculate altitude by using
                // var altValue = WeatherHelper.CalculateAltitude(preValue, defaultSeaLevelPressure, tempValue) which would be more performant.
                bme80.TryReadAltitude(defaultSeaLevelPressure, out var altValue);

                if (readResult.TemperatureIsValid)
                {
                    Debug.WriteLine($"Temperature: {readResult.Temperature.DegreesCelsius}°C");
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
                    Debug.WriteLine($"Heat index: {WeatherHelper.CalculateHeatIndex(readResult.Temperature, readResult.Humidity).DegreesCelsius}°C");
                    Debug.WriteLine($"Dew point: {WeatherHelper.CalculateDewPoint(readResult.Temperature, readResult.Humidity).DegreesCelsius}°C");
                }

                if (!outcomeReported)
                {
                    outcomeReported = true;

                    if (readResult.TemperatureIsValid && readResult.PressureIsValid && readResult.HumidityIsValid)
                    {
                        IntegrationTest.Pass(TestName, $"{readResult.Temperature.DegreesCelsius}°C, {readResult.Pressure.Hectopascals}hPa, {readResult.Humidity.Percent}%RH");
                    }
                    else
                    {
                        IntegrationTest.Fail(TestName, $"invalid read (temperature: {readResult.TemperatureIsValid}, pressure: {readResult.PressureIsValid}, humidity: {readResult.HumidityIsValid}) -- check the I2C wiring and the BMP280 address");
                    }
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
                    Debug.WriteLine($"Temperature: {readResult.Temperature.DegreesCelsius}°C");
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
                    Debug.WriteLine($"Heat index: {WeatherHelper.CalculateHeatIndex(readResult.Temperature, readResult.Humidity).DegreesCelsius}°C");
                    Debug.WriteLine($"Dew point: {WeatherHelper.CalculateDewPoint(readResult.Temperature, readResult.Humidity).DegreesCelsius}°C");
                }

                Thread.Sleep(5000);
            }
        }
    }
}
