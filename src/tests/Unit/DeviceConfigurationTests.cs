using SmartHome.DeviceConfiguration;
using SmartHome.DeviceModel.Enums;
using SmartHome.Protocol;
using nanoFramework.Logging;
using nanoFramework.Logging.Debug;
using nanoFramework.TestFramework;

namespace SmartHome.UnitTests
{
    // Reading a device's installation data from a file, and what happens when it cannot
    // be read. The file half needs storage and is one method; everything that decides
    // whether a device comes up configured or alerting is the parsing and the result,
    // which is why they are a separate class and why these cases can run on the virtual
    // device in CI.
    //
    // The theme of every case below is the same: a configuration that is wrong must be
    // refused, not defaulted. A JSON member that is absent leaves its field at zero, and
    // a device that reads pin 0 at 0ms intervals while announcing itself as healthy is
    // the failure this whole mechanism exists to prevent -- so "it parsed" is never the
    // bar, and each of these pins one of the ways that is enforced.
    [TestClass]
    public class DeviceConfigurationTests
    {
        private const string Source = "I:\\configuration.json";

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
        public void Parse_Reads_A_Configuration_With_A_Nested_Block()
        {
            // The nesting is the point, not incidental. A room-to-windows map or a set of
            // per-zone valve pins is a custom object inside a custom object, and this is
            // the evidence that the shape survives the round trip on this runtime rather
            // than an assumption carried over from a sample.
            var result = ConfigurationParser.Parse(
                "{ \"Name\": \"office\", \"Hardware\": { \"Pin\": 21, \"IntervalMs\": 5000 } }",
                typeof(FakeConfiguration),
                Source);

            Assert.IsTrue(result.IsValid, "a complete configuration is usable: " + result.FailureReason);

            var configuration = (FakeConfiguration)result.Value!;
            Assert.AreEqual("office", configuration.Name);
            Assert.IsNotNull(configuration.Hardware, "the nested block must survive the round trip");

            var hardware = configuration.Hardware!;
            Assert.AreEqual(21, hardware.Pin);
            Assert.AreEqual(5000, hardware.IntervalMs);
        }

        [TestMethod]
        public void Parse_Refuses_Text_That_Is_Not_Configuration()
        {
            // A file truncated by a deployment that was interrupted, or edited to
            // something that is no longer JSON at all.
            AssertRefused(ConfigurationParser.Parse("{ \"Name\": ", typeof(FakeConfiguration), Source));
            AssertRefused(ConfigurationParser.Parse("not json at all", typeof(FakeConfiguration), Source));
        }

        [TestMethod]
        public void Parse_Refuses_An_Empty_File()
        {
            // Distinguished from the case above because it has its own cause: a file
            // created but never written is what an interrupted deployment leaves behind,
            // and "empty" is a more useful thing to be told than "could not be parsed".
            AssertRefused(ConfigurationParser.Parse("", typeof(FakeConfiguration), Source));
            AssertRefused(ConfigurationParser.Parse("   \r\n  ", typeof(FakeConfiguration), Source));
        }

        [TestMethod]
        public void Parse_Refuses_A_Key_The_Configuration_Does_Not_Have()
        {
            // The single most likely mistake in a file whose whole purpose is to be
            // hand-edited, and the one with the worst silent outcome: 'Naem' would leave
            // Name null and every other field at its default, and without this the device
            // would come up looking configured. This is what
            // ThrowExceptionWhenPropertyNotFound buys, pinned here because it is an option
            // on somebody else's parser and nothing else in this repository would notice
            // it changing.
            AssertRefused(ConfigurationParser.Parse(
                "{ \"Naem\": \"office\", \"Hardware\": { \"Pin\": 21, \"IntervalMs\": 5000 } }",
                typeof(FakeConfiguration),
                Source));
        }

        [TestMethod]
        public void Parse_Refuses_A_Configuration_Its_Own_Rules_Reject()
        {
            // Parsed cleanly, and still not an installation: zero is what an absent key
            // leaves behind, so every bound a device declares has to reject it.
            var result = ConfigurationParser.Parse(
                "{ \"Name\": \"office\", \"Hardware\": { \"Pin\": 21, \"IntervalMs\": 0 } }",
                typeof(FakeConfiguration),
                Source);

            AssertRefused(result);
            Assert.IsTrue(
                result.FailureReason!.IndexOf("IntervalMs") >= 0,
                "the reason must name the field to fix, got: " + result.FailureReason);
        }

        [TestMethod]
        public void Parse_Refuses_A_Missing_Nested_Block()
        {
            // The block a device derives a whole node from. Absent, it leaves a null
            // reference the device would meet much later, at the first read.
            var result = ConfigurationParser.Parse("{ \"Name\": \"office\" }", typeof(FakeConfiguration), Source);

            AssertRefused(result);
            Assert.IsTrue(
                result.FailureReason!.IndexOf("Hardware") >= 0,
                "the reason must name the missing block, got: " + result.FailureReason);
        }

        [TestMethod]
        public void Every_Refusal_Names_Where_It_Came_From()
        {
            // The message is published as the alert and read by whoever has to correct
            // the file. A device with two configuration files, or a person holding an
            // alert from a device they did not deploy, needs to be told which one.
            var reasons = new string[]
            {
                ConfigurationParser.Parse("", typeof(FakeConfiguration), Source).FailureReason!,
                ConfigurationParser.Parse("not json", typeof(FakeConfiguration), Source).FailureReason!,
                ConfigurationParser.Parse("{ \"Name\": \"office\" }", typeof(FakeConfiguration), Source).FailureReason!,
            };

            foreach (var reason in reasons)
            {
                Assert.IsTrue(reason.IndexOf(Source) >= 0, "a failure must name its source, got: " + reason);
            }
        }

        [TestMethod]
        public void A_Refused_Configuration_Raises_The_Shared_Alert()
        {
            // The contract that makes this component shared rather than copied: every
            // device says the same thing, under the same id, with the reason as the
            // message. A device app that wrote its own version of this would drift from
            // the next one.
            var protocol = new RecordingProtocol();
            var result = ConfigurationParser.Parse("not json", typeof(FakeConfiguration), Source);

            result.ReportTo(protocol);

            Assert.AreEqual(1, protocol.RaisedIds.Length);
            Assert.AreEqual(ConfigurationResult.AlertId, protocol.RaisedIds[0]);
            Assert.AreEqual(result.FailureReason, protocol.RaisedMessages[0], "the alert carries the reason, not a summary of it");
        }

        [TestMethod]
        public void A_Usable_Configuration_Says_Nothing()
        {
            // Nothing is cleared either. A device reads its configuration once, at boot,
            // with no alert raised yet, so clearing one would be a no-op that reads as
            // though re-reading were supported.
            var protocol = new RecordingProtocol();
            var result = ConfigurationParser.Parse(
                "{ \"Name\": \"office\", \"Hardware\": { \"Pin\": 21, \"IntervalMs\": 5000 } }",
                typeof(FakeConfiguration),
                Source);

            result.ReportTo(protocol);

            Assert.AreEqual(0, protocol.RaisedIds.Length);
            Assert.AreEqual(0, protocol.ClearedIds.Length);
        }

        [TestMethod]
        public void A_Missing_File_Is_A_Result_Rather_Than_A_Throw()
        {
            // The store promises its caller a result for every outcome, and this is the
            // outcome a device meets first: firmware flashed, configuration never
            // deployed. A throw here would reboot the device straight back into the same
            // missing file.
            var result = new ConfigurationStore("I:\\no-such-configuration.json").Load(typeof(FakeConfiguration));

            AssertRefused(result);
            Assert.IsTrue(
                result.FailureReason!.IndexOf("no-such-configuration.json") >= 0,
                "the reason must name the file, got: " + result.FailureReason);
        }

        private static void AssertRefused(ConfigurationResult result)
        {
            Assert.IsFalse(result.IsValid, "an unusable configuration must be refused");
            Assert.IsNull(result.Value, "a refused configuration hands back nothing to use");
            Assert.IsNotNull(result.FailureReason);
            Assert.IsTrue(result.FailureReason.Length > 0, "a refusal without a reason cannot be acted on");
        }

        // A device's configuration, reduced to the two things that matter here: a value
        // at the top level and a nested block with a rule of its own.
        public class FakeConfiguration : IValidatableConfiguration
        {
            public string? Name { get; set; }

            public FakeHardwareConfiguration? Hardware { get; set; }

            public string? Validate()
            {
                if (Name == null || Name.Length == 0)
                {
                    return "Name is missing.";
                }

                if (Hardware == null)
                {
                    return "The Hardware block is missing.";
                }

                return Hardware.Validate();
            }
        }

        public class FakeHardwareConfiguration
        {
            public int Pin { get; set; }

            public int IntervalMs { get; set; }

            public string? Validate() => IntervalMs <= 0 ? $"Hardware.IntervalMs is {IntervalMs}; it must be greater than zero." : null;
        }

        // Records what a device would have said, so a case can assert the alert without a
        // broker. Only RaiseAlert and ClearAlert are reached by anything under test; the
        // rest exist because the interface has them.
        private class RecordingProtocol : IDeviceProtocol
        {
            public string[] RaisedIds { get; private set; } = new string[0];

            public string[] RaisedMessages { get; private set; } = new string[0];

            public string[] ClearedIds { get; private set; } = new string[0];

            public string DeviceId => "recording";

            public DeviceState State => DeviceState.Ready;

            public bool IsConnected => true;

            // Explicit no-op accessors rather than a field-like event: nothing here ever
            // raises it, and a field-like event that is never raised is a warning.
            public event DeviceCommandHandler? OnCommand
            {
                add { }
                remove { }
            }

            public bool Connect() => true;

            public bool ConnectWithRetry(int maxAttempts = 10, int retryDelayMs = 3000) => true;

            public void Disconnect()
            {
            }

            public void Ready()
            {
            }

            public void Sleep()
            {
            }

            public void RaiseAlert(string id, string message)
            {
                RaisedIds = Append(RaisedIds, id);
                RaisedMessages = Append(RaisedMessages, message);
            }

            public void ClearAlert(string id) => ClearedIds = Append(ClearedIds, id);

            private static string[] Append(string[] existing, string value)
            {
                var grown = new string[existing.Length + 1];
                System.Array.Copy(existing, grown, existing.Length);
                grown[existing.Length] = value;
                return grown;
            }
        }
    }
}
