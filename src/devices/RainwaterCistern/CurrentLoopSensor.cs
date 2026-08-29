using System;
using System.Device.I2c;
using System.Threading;

namespace SmartHome.Devices.RainwaterCistern
{
    /// <summary>
    /// Reads the hydrostatic probe's 4-20 mA loop as a current, through the burden
    /// resistor on an ADS1115 input.
    /// </summary>
    /// <remarks>
    /// The I2C half of the story; the register arithmetic lives in
    /// <see cref="Ads1115Protocol"/> so it can be tested without a bus.
    /// </remarks>
    internal sealed class CurrentLoopSensor : IDisposable
    {
        // A 128 SPS conversion takes about 7.8 ms, so this polls well past the point
        // where a healthy converter has answered. It exists to fail loudly rather than
        // to wait: a bus that never sets the ready bit is a wiring fault, and reading
        // the conversion register anyway would return the previous sample forever.
        private const int ConversionPollIntervalMs = 2;
        private const int ConversionPollAttempts = 20;

        private readonly I2cDevice _adc;

        public CurrentLoopSensor(I2cDevice adc)
        {
            _adc = adc ?? throw new ArgumentNullException(nameof(adc));
        }

        /// <summary>
        /// Averages several conversions into one loop current, in milliamps.
        /// </summary>
        /// <remarks>
        /// Averaged because the level being measured moves far slower than the noise on
        /// a long two-wire run into a wet hole in the ground, and because a single
        /// count of ADC noise is a fifth of a millimetre of water either way -- cheap to
        /// average out, annoying to watch a controller redraw.
        /// </remarks>
        public double ReadMilliAmps(int samples)
        {
            if (samples < 1)
            {
                throw new ArgumentException($"At least one sample is needed, was asked for {samples}.");
            }

            var sum = 0.0;
            for (int i = 0; i < samples; i++)
            {
                var raw = ReadRaw();
                var volts = Ads1115Protocol.RawToVolts(raw, Calibration.AdcFullScaleVolts);
                sum += LevelCalculation.VoltsToMilliAmps(volts, Calibration.ShuntOhms);
            }

            return sum / samples;
        }

        private short ReadRaw()
        {
            StartConversion();
            WaitForConversion();

            SpanByte address = new byte[1] { Ads1115Protocol.ConversionRegister };
            SpanByte result = new byte[2];
            _adc.WriteRead(address, result);

            return (short)Ads1115Protocol.ToRegisterWord(result[0], result[1]);
        }

        private void StartConversion()
        {
            var config = Ads1115Protocol.BuildSingleShotConfig(Calibration.AdcChannel);

            SpanByte command = new byte[3]
            {
                Ads1115Protocol.ConfigRegister,
                (byte)(config >> 8),
                (byte)(config & 0xFF),
            };

            _adc.Write(command);
        }

        private void WaitForConversion()
        {
            SpanByte address = new byte[1] { Ads1115Protocol.ConfigRegister };
            SpanByte config = new byte[2];

            for (int attempt = 0; attempt < ConversionPollAttempts; attempt++)
            {
                Thread.Sleep(ConversionPollIntervalMs);

                _adc.WriteRead(address, config);

                if (Ads1115Protocol.IsConversionComplete(Ads1115Protocol.ToRegisterWord(config[0], config[1])))
                {
                    return;
                }
            }

            throw new Exception($"The ADS1115 did not finish a conversion within {ConversionPollAttempts * ConversionPollIntervalMs} ms.");
        }

        public void Dispose() => _adc.Dispose();
    }
}
