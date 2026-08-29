using System;

namespace SmartHome.Devices.RainwaterCistern
{
    /// <summary>
    /// The register layout of a TI ADS1115, reduced to the one thing this device does
    /// with it: a single-shot, single-ended conversion.
    /// </summary>
    /// <remarks>
    /// nanoFramework ships a full driver as nanoFramework.Iot.Device.Ads1115, and it is
    /// the right choice for anything using the comparator, the ALERT/RDY pin or
    /// continuous mode. It is not used here because it is not in this repo's packages\
    /// folder or in this machine's NuGet cache, and Restore-Packages.ps1 is offline by
    /// design -- adding it would leave the solution unbuildable until someone restores
    /// from the network. What is left after dropping everything this device does not
    /// need is one config word and two register reads, and composing that word is
    /// covered by unit tests, which is what a hand-rolled driver actually risks getting
    /// wrong.
    ///
    /// Pure bit arithmetic, no I2C types, so it links into SmartHome.UnitTests the same
    /// way <see cref="LevelCalculation"/> does. Swapping in the official driver later
    /// means replacing this file and <see cref="CurrentLoopSensor"/>, and nothing else.
    /// That swap is issue #38, which also notes that Ads1115ProtocolTests goes with this
    /// file rather than being retargeted: it covers a hand-rolled config word, which is
    /// a risk that stops existing once a maintained driver composes it.
    /// </remarks>
    public static class Ads1115Protocol
    {
        /// <summary>Register holding the result of the last conversion.</summary>
        public const byte ConversionRegister = 0x00;

        /// <summary>Register holding the operating configuration.</summary>
        public const byte ConfigRegister = 0x01;

        /// <summary>Default I2C address, with the ADDR pin tied to GND.</summary>
        public const byte DefaultI2cAddress = 0x48;

        /// <summary>Counts a full-scale reading corresponds to (2^15).</summary>
        public const double FullScaleCounts = 32768.0;

        // OS: writing 1 begins a single conversion; reading 1 means no conversion is in
        // flight, which is how the poll below tells that a result is ready.
        private const ushort OperationalStatusBit = 0x8000;

        // MUX bit 14 set selects single-ended mode, and bits 13-12 then carry the input
        // number: AIN0 is 0b100, AIN3 is 0b111.
        private const ushort SingleEndedMuxBase = 0x4000;
        private const int MuxShift = 12;

        // PGA = 0b001, full scale +/-4.096 V. Chosen to match the 0.600-3.000 V the
        // 150 ohm burden resistor produces, with headroom for an over-range current.
        private const ushort Pga4096Millivolts = 0x0200;

        // MODE = 1, single-shot. The device idles between conversions instead of
        // converting 128 times a second for a level that moves in millimetres per hour.
        private const ushort SingleShotModeBit = 0x0100;

        // DR = 0b100, 128 SPS. The default, and fast enough that a conversion completes
        // well inside the poll below.
        private const ushort DataRate128Sps = 0x0080;

        // COMP_QUE = 0b11, comparator disabled. Anything else drives the ALERT/RDY pin,
        // which is not wired.
        private const ushort ComparatorDisabled = 0x0003;

        /// <summary>
        /// Builds the config word that starts one single-ended conversion on the given
        /// input.
        /// </summary>
        /// <param name="channel">Single-ended input, 0 to 3, matching AIN0 to AIN3.</param>
        public static ushort BuildSingleShotConfig(byte channel)
        {
            if (channel > 3)
            {
                throw new ArgumentException($"The ADS1115 has inputs AIN0 to AIN3; channel {channel} does not exist.");
            }

            var mux = (ushort)(SingleEndedMuxBase | (channel << MuxShift));

            return (ushort)(OperationalStatusBit
                | mux
                | Pga4096Millivolts
                | SingleShotModeBit
                | DataRate128Sps
                | ComparatorDisabled);
        }

        /// <summary>
        /// True when a config word read back from the device says its conversion has
        /// finished.
        /// </summary>
        public static bool IsConversionComplete(ushort configWord) =>
            (configWord & OperationalStatusBit) != 0;

        /// <summary>Combines the two bytes of a register read into a word.</summary>
        public static ushort ToRegisterWord(byte high, byte low) => (ushort)((high << 8) | low);

        /// <summary>
        /// Converts a signed conversion result into volts for the configured full-scale
        /// range.
        /// </summary>
        public static double RawToVolts(short raw, double fullScaleVolts) =>
            raw * fullScaleVolts / FullScaleCounts;
    }
}
