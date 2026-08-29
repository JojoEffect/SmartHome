using nanoFramework.TestFramework;
using SmartHome.Devices.RainwaterCistern;
using System;

namespace SmartHome.UnitTests
{
    /// <summary>
    /// The ADS1115 register arithmetic RainwaterCistern hand-rolls instead of taking
    /// the full nanoFramework.Iot.Device.Ads1115 driver.
    /// </summary>
    /// <remarks>
    /// This is the part of a hand-rolled driver that is genuinely easy to get wrong and
    /// impossible to notice: a misplaced bit in the config word produces a reading, just
    /// the wrong one, from the wrong input or at the wrong gain. The expected words
    /// below are spelled out as hex rather than recomputed from the same constants the
    /// code uses, so a changed constant fails a test instead of moving the answer with
    /// it.
    /// </remarks>
    [TestClass]
    public class Ads1115ProtocolTests
    {
        private const double Tolerance = 1e-9;

        private const double FullScaleVolts = 4.096;

        [TestMethod]
        public void BuildSingleShotConfig_Composes_The_Documented_Word()
        {
            // OS=1 (start), MUX=100 (AIN0 single-ended), PGA=001 (+/-4.096 V),
            // MODE=1 (single-shot), DR=100 (128 SPS), COMP_QUE=11 (comparator off).
            Assert.AreEqual((ushort)0xC383, Ads1115Protocol.BuildSingleShotConfig(0), "AIN0 single-shot at +/-4.096 V");
        }

        [TestMethod]
        public void BuildSingleShotConfig_Selects_The_Requested_Input()
        {
            // Only the MUX field moves: AIN0 is 100, AIN3 is 111.
            Assert.AreEqual((ushort)0xD383, Ads1115Protocol.BuildSingleShotConfig(1), "AIN1");
            Assert.AreEqual((ushort)0xE383, Ads1115Protocol.BuildSingleShotConfig(2), "AIN2");
            Assert.AreEqual((ushort)0xF383, Ads1115Protocol.BuildSingleShotConfig(3), "AIN3");
        }

        [TestMethod]
        public void BuildSingleShotConfig_Rejects_An_Input_The_Chip_Does_Not_Have()
        {
            Assert.ThrowsException(typeof(ArgumentException), () => Ads1115Protocol.BuildSingleShotConfig(4));
            Assert.ThrowsException(typeof(ArgumentException), () => Ads1115Protocol.BuildSingleShotConfig(255));
        }

        [TestMethod]
        public void IsConversionComplete_Reads_The_Operational_Status_Bit()
        {
            // The OS bit means the opposite on the way in and on the way out: writing 1
            // starts a conversion, reading 1 says none is running.
            Assert.IsTrue(Ads1115Protocol.IsConversionComplete(0xC383), "OS set means the result is ready");
            Assert.IsFalse(Ads1115Protocol.IsConversionComplete(0x4383), "OS clear means a conversion is still running");
        }

        [TestMethod]
        public void ToRegisterWord_Puts_The_First_Byte_On_The_Wire_First()
        {
            // The ADS1115 is big-endian on the bus; swapping these silently scales every
            // reading by 256.
            Assert.AreEqual((ushort)0xC383, Ads1115Protocol.ToRegisterWord(0xC3, 0x83), "high byte first");
            Assert.AreEqual((ushort)0x0000, Ads1115Protocol.ToRegisterWord(0x00, 0x00), "zero");
            Assert.AreEqual((ushort)0xFFFF, Ads1115Protocol.ToRegisterWord(0xFF, 0xFF), "all ones");
        }

        [TestMethod]
        public void RawToVolts_Scales_Counts_By_The_Full_Scale_Range()
        {
            // The two counts that matter for this device: 4800 is the 0.600 V that a
            // 4 mA loop puts across the 150 ohm burden resistor, 24000 is the 3.000 V of
            // a 20 mA loop.
            AssertClose(0.6, Ads1115Protocol.RawToVolts(4800, FullScaleVolts), "4800 counts is 0.6 V");
            AssertClose(3.0, Ads1115Protocol.RawToVolts(24000, FullScaleVolts), "24000 counts is 3.0 V");
            AssertClose(0.0, Ads1115Protocol.RawToVolts(0, FullScaleVolts), "zero counts is zero volts");
        }

        [TestMethod]
        public void RawToVolts_Treats_The_Conversion_Result_As_Signed()
        {
            // A negative result is what a reversed burden connection looks like, and it
            // has to stay negative: read as unsigned it would come back as a plausible
            // near-full-scale reading.
            AssertClose(-0.6, Ads1115Protocol.RawToVolts(-4800, FullScaleVolts), "negative counts stay negative");
        }

        /// <summary>
        /// Compares two doubles within a tolerance, for the same reason
        /// <see cref="LevelCalculationTests"/> does.
        /// </summary>
        private static void AssertClose(double expected, double actual, string because)
        {
            var difference = expected - actual;
            var within = difference <= Tolerance && difference >= -Tolerance;

            Assert.IsTrue(within, $"{because}: expected {expected}, was {actual}.");
        }
    }
}
