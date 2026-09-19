using SmartHome.DeviceModel.Builder;
using SmartHome.DeviceModel.Formats;
using SmartHome.DeviceModel.Properties;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using nanoFramework.TestFramework;
using System;
using System.Collections;
using System.Reflection;

namespace SmartHome.UnitTests
{
    // The structured formats exist to end a specific failure: a raw format string that
    // every consumer re-read for itself, with the readers drifting apart until a payload
    // the property refused was advertised to controllers as acceptable. There is now one
    // reader per format, and these are its cases.
    [TestClass]
    public class DeviceModelFormatTests
    {
        private const string _deviceId = "super-car";
        private const string _deviceName = "Super car";
        private const string _nodeId = "matrix";
        private const string _nodeName = "Datatype matrix";
        private const string _nodeType = "conformance";

        [TestMethod]
        public void NumericRange_Reads_A_Closed_Range()
        {
            Assert.IsTrue(NumericRange.TryParse("0:100", out var range));
            Assert.IsNotNull(range);
            Assert.IsTrue(range!.HasMinimum);
            Assert.IsTrue(range.HasMaximum);
            Assert.IsFalse(range.HasStep);
            Assert.AreEqual(0.0, range.Minimum);
            Assert.AreEqual(100.0, range.Maximum);
        }

        [TestMethod]
        public void NumericRange_Trims_Whitespace_Around_Each_Bound()
        {
            // A device author writing "5 : 30" means the range 5 to 30. The old reader
            // trimmed and this one has to as well, or every payload would be refused
            // against bounds that never parsed.
            Assert.IsTrue(NumericRange.TryParse("5 : 30", out var range));
            Assert.IsNotNull(range);
            Assert.AreEqual(5.0, range!.Minimum);
            Assert.AreEqual(30.0, range.Maximum);
        }

        [TestMethod]
        public void NumericRange_Refuses_A_Minimum_Above_Its_Maximum()
        {
            // "30:5" contains no value at all. Read as a range it would refuse every
            // payload, so it declares nothing instead -- a device author's typo must not
            // turn into a property no controller can ever set.
            Assert.IsFalse(NumericRange.TryParse("30:5", out var range));
            Assert.IsNull(range);
        }

        [TestMethod]
        public void NumericRange_Reads_A_Bound_Written_Without_Its_Leading_Zero()
        {
            // ".5" is a number to this runtime's native parser -- it has an explicit
            // branch for a string that starts with the decimal point -- so a range
            // written that way is a real range, not a malformed one.
            Assert.IsTrue(NumericRange.TryParse(".5:10", out var range));
            Assert.IsNotNull(range);
            Assert.AreEqual(0.5, range!.Minimum);
            Assert.AreEqual(10.0, range.Maximum);
        }

        [TestMethod]
        public void NumericRange_Reads_An_Open_Ended_Range()
        {
            Assert.IsTrue(NumericRange.TryParse("0:", out var atLeast));
            Assert.IsNotNull(atLeast);
            Assert.IsTrue(atLeast!.HasMinimum);
            Assert.IsFalse(atLeast.HasMaximum);
            Assert.IsTrue(atLeast.Contains(1000000.0), "an open upper end excludes nothing above the minimum");
            Assert.IsFalse(atLeast.Contains(-1.0));

            Assert.IsTrue(NumericRange.TryParse(":10", out var atMost));
            Assert.IsNotNull(atMost);
            Assert.IsFalse(atMost!.HasMinimum);
            Assert.IsTrue(atMost.HasMaximum);
            Assert.IsTrue(atMost.Contains(-1000000.0));
            Assert.IsFalse(atMost.Contains(11.0));
        }

        [TestMethod]
        public void NumericRange_Reads_A_Step()
        {
            Assert.IsTrue(NumericRange.TryParse("2:6:2", out var range));
            Assert.IsNotNull(range);
            Assert.IsTrue(range!.HasStep);
            Assert.AreEqual(2.0, range.Step);

            // Carried, not enforced. Rounding a value to the nearest step would change
            // what a controller asked for; this model either applies a payload as sent or
            // drops it.
            Assert.IsTrue(range.Contains(3.0), "the step must not narrow what the range accepts");
        }

        [TestMethod]
        public void NumericRange_Refuses_Formats_That_Declare_Nothing_Usable()
        {
            Assert.IsFalse(NumericRange.TryParse("", out _));
            Assert.IsFalse(NumericRange.TryParse("100", out _), "no colon is not a range");
            Assert.IsFalse(NumericRange.TryParse(":", out _), "neither end declared");
            Assert.IsFalse(NumericRange.TryParse("low:high", out _));
            Assert.IsFalse(NumericRange.TryParse("1:2:3:4", out _));
            Assert.IsFalse(NumericRange.TryParse("0:10:0", out _), "a step must be greater than zero");
        }

        [TestMethod]
        public void NumericRange_Includes_Both_Of_Its_Bounds()
        {
            var range = NumericRange.Between(0, 100);

            // A range check that is off by one at the edge is the usual way this kind of
            // validation goes wrong.
            Assert.IsTrue(range.Contains(0));
            Assert.IsTrue(range.Contains(100));
            Assert.IsFalse(range.Contains(-0.5));
            Assert.IsFalse(range.Contains(100.5));
        }

        [TestMethod]
        public void NumericRange_Refuses_A_Step_That_Is_Not_Positive()
        {
            Assert.ThrowsException(typeof(ArgumentException), () => NumericRange.Between(0, 10).WithStep(0));
            Assert.ThrowsException(typeof(ArgumentException), () => NumericRange.Between(0, 10).WithStep(-1));
        }

        [TestMethod]
        public void EnumOptions_Trims_Its_Values_Once_At_Construction()
        {
            // "ready, alert" names the same two values as "ready,alert". Trimming here
            // means no consumer has to remember to.
            Assert.IsTrue(EnumOptions.TryParse("ready, alert", out var options));
            Assert.IsNotNull(options);
            Assert.AreEqual(2, options!.Count);
            Assert.IsTrue(options.Contains("alert"));

            // The declaration is trimmed; the payload is not. " alert" is a different
            // value, and a controller sending it is asking for something not declared.
            Assert.IsFalse(options.Contains(" alert"));
        }

        [TestMethod]
        public void EnumOptions_Drops_Entries_That_Are_Empty_After_Trimming()
        {
            // An empty string is not a valid payload for any datatype, so an empty option
            // could never be selected -- carrying it would only give a payload of ""
            // something to match.
            Assert.IsTrue(EnumOptions.TryParse("low,,high", out var options));
            Assert.IsNotNull(options);
            Assert.AreEqual(2, options!.Count);
            Assert.IsFalse(options.Contains(string.Empty));
        }

        [TestMethod]
        public void EnumOptions_Refuses_A_Format_With_Nothing_In_It()
        {
            Assert.IsFalse(EnumOptions.TryParse("", out _));
            Assert.IsFalse(EnumOptions.TryParse(" , , ", out _));
            Assert.ThrowsException(typeof(ArgumentException), () => new EnumOptions(new string[0]));
        }

        [TestMethod]
        public void EnumOptions_Keeps_The_Declared_Order_And_Hands_Out_A_Copy()
        {
            Assert.IsTrue(EnumOptions.TryParse("low,medium,high", out var options));
            Assert.IsNotNull(options);

            var values = options!.Values;
            Assert.AreEqual("low", values[0]);
            Assert.AreEqual("high", values[2]);

            // Rewriting the array handed out must not rewrite a declaration the device
            // may already have announced.
            values[0] = "tampered";
            Assert.AreEqual("low", options.Values[0]);
        }

        [TestMethod]
        public void BooleanLabels_Read_Both_Names()
        {
            Assert.IsTrue(BooleanLabels.TryParse("closed, open", out var labels));
            Assert.IsNotNull(labels);
            Assert.AreEqual("closed", labels!.False);
            Assert.AreEqual("open", labels.True);
            Assert.AreEqual("closed,open", labels.ToString());
        }

        [TestMethod]
        public void BooleanLabels_Refuse_Anything_That_Is_Not_Exactly_Two_Names()
        {
            Assert.IsFalse(BooleanLabels.TryParse("open", out _));
            Assert.IsFalse(BooleanLabels.TryParse("off,on,unknown", out _));
            Assert.IsFalse(BooleanLabels.TryParse(",on", out _));
            Assert.ThrowsException(typeof(ArgumentException), () => new BooleanLabels("off", string.Empty));
        }

        [TestMethod]
        public void ColorFormats_Read_A_Preference_Ordered_List()
        {
            Assert.IsTrue(ColorFormats.TryParse("rgb,hsv", out var formats));
            Assert.IsNotNull(formats);
            Assert.AreEqual("rgb", formats!.Preferred, "the first entry is the one an adapter limited to a single encoding publishes");
            Assert.IsTrue(formats.Supports("hsv"));
            Assert.IsFalse(formats.Supports("xyz"));
            Assert.AreEqual("rgb,hsv", formats.ToString());
        }

        [TestMethod]
        public void ColorFormats_Refuse_An_Encoding_They_Do_Not_Know()
        {
            Assert.IsFalse(ColorFormats.TryParse("cmyk", out var formats));
            Assert.IsNull(formats);
            Assert.IsFalse(ColorFormats.TryParse("rgb,cmyk", out _), "one bad entry spoils the declaration");
            Assert.ThrowsException(typeof(ArgumentException), () => new ColorFormats(new string[] { "cmyk" }));
        }

        // The builders' text route into the readers above. Text that does not parse
        // still declares nothing, as it always has; what these pin is that it no longer
        // does so in silence. A property keeps the parsed value and never the text, so
        // the builder is the last thing that can say what was wrong -- and until it did,
        // a malformed declaration was indistinguishable from one never written.

        [TestMethod]
        public void IntegerPropertyBuilder_Reports_A_Format_That_Does_Not_Parse()
        {
            // A dash where the colon belongs: about the likeliest typo there is, and one
            // that leaves a property accepting any integer while its author believes it
            // is bounded.
            IntegerProperty? property = null;
            var warnings = WarningsWhile(() => property = BuildInteger("0-100"));

            Assert.IsNull(property!.Range, "text that does not parse still declares nothing");
            AssertReported(warnings, "speed", "0-100");
        }

        [TestMethod]
        public void FloatPropertyBuilder_Reports_A_Format_That_Does_Not_Parse()
        {
            // Shaped like a range and refused anyway: its ends are the wrong way round,
            // so it contains nothing, and declaring nothing is the forgiving reading.
            FloatProperty? property = null;
            var warnings = WarningsWhile(() => property = BuildFloat("30:5"));

            Assert.IsNull(property!.Range, "text that does not parse still declares nothing");
            AssertReported(warnings, "temperature", "30:5");
        }

        [TestMethod]
        public void EnumPropertyBuilder_Reports_A_Format_That_Does_Not_Parse()
        {
            EnumProperty? property = null;
            var warnings = WarningsWhile(() => property = BuildEnum(" , , "));

            Assert.IsNull(property!.Options, "separators alone declare no options");
            AssertReported(warnings, "mode", " , , ");
        }

        [TestMethod]
        public void BooleanPropertyBuilder_Reports_A_Format_That_Does_Not_Parse()
        {
            BooleanProperty? property = null;
            var warnings = WarningsWhile(() => property = BuildBoolean("off,on,unknown"));

            Assert.IsNull(property!.Labels, "three names are not a pair");
            AssertReported(warnings, "running", "off,on,unknown");
        }

        [TestMethod]
        public void ColorPropertyBuilder_Reports_A_Format_That_Does_Not_Parse()
        {
            ColorProperty? property = null;
            var warnings = WarningsWhile(() => property = BuildColor("rgb,cmyk"));

            Assert.IsNull(property!.Formats, "one unknown encoding spoils the declaration");
            AssertReported(warnings, "tint", "rgb,cmyk");
        }

        [TestMethod]
        public void PropertyBuilders_Declare_A_Format_That_Parses_Without_Reporting_It()
        {
            // The half every device in this tree depends on: text that parses declares
            // exactly what it always did, and costs nothing in the log.
            IntegerProperty? integer = null;
            FloatProperty? real = null;
            EnumProperty? choice = null;
            BooleanProperty? flag = null;
            ColorProperty? colour = null;

            var warnings = WarningsWhile(() =>
            {
                integer = BuildInteger("0:100");
                real = BuildFloat("-10:40:0.5");
                choice = BuildEnum("low, medium, high");
                flag = BuildBoolean("closed, open");
                colour = BuildColor("rgb,hsv");
            });

            Assert.AreEqual(0, warnings.Count, "a format that parses is not reported");

            Assert.AreEqual(0.0, integer!.Range!.Minimum);
            Assert.AreEqual(100.0, integer.Range.Maximum);
            Assert.IsFalse(integer.Range.HasStep);

            Assert.AreEqual(-10.0, real!.Range!.Minimum);
            Assert.AreEqual(40.0, real.Range.Maximum);
            Assert.AreEqual(0.5, real.Range.Step);

            Assert.AreEqual("low,medium,high", choice!.Options!.ToString());
            Assert.AreEqual("closed,open", flag!.Labels!.ToString());
            Assert.AreEqual("rgb,hsv", colour!.Formats!.ToString());
        }

        [TestMethod]
        public void PropertyBuilders_Report_Nothing_For_An_Empty_Format()
        {
            // Empty text is how a caller with nothing to declare says so -- the value
            // tests' own builders pass it by default -- so it is not a mistake to report.
            IntegerProperty? integer = null;
            FloatProperty? real = null;
            EnumProperty? choice = null;
            BooleanProperty? flag = null;
            ColorProperty? colour = null;

            var warnings = WarningsWhile(() =>
            {
                integer = BuildInteger(string.Empty);
                real = BuildFloat(string.Empty);
                choice = BuildEnum(string.Empty);
                flag = BuildBoolean(string.Empty);
                colour = BuildColor(string.Empty);
            });

            Assert.AreEqual(0, warnings.Count, "empty text is no format, not a malformed one");

            Assert.IsNull(integer!.Range);
            Assert.IsNull(real!.Range);
            Assert.IsNull(choice!.Options);
            Assert.IsNull(flag!.Labels);
            Assert.IsNull(colour!.Formats);
        }

        private static void AssertReported(ArrayList warnings, string propertyId, string format)
        {
            Assert.AreEqual(1, warnings.Count, "one malformed declaration, one warning");

            var warning = (string)warnings[0];
            Assert.Contains($"'{propertyId}'", warning, "the warning names the property");
            Assert.Contains($"'{format}'", warning, "the warning quotes the text exactly as written");
        }

        /// <summary>
        /// Runs <paramref name="build"/> with every logger recording into one list, and
        /// hands back the warnings it logged.
        /// </summary>
        /// <remarks>
        /// Restores whatever factory was installed before, whether or not the build threw:
        /// the factory is process-wide, and the classes around this one install their own
        /// in their setup.
        /// </remarks>
        private static ArrayList WarningsWhile(Action build)
        {
            var previous = LogDispatcher.LoggerFactory;
            var recorder = new WarningRecorder();
            LogDispatcher.LoggerFactory = recorder;

            try
            {
                build();
            }
            finally
            {
                LogDispatcher.LoggerFactory = previous;
            }

            return recorder.Warnings;
        }

        private static IntegerProperty BuildInteger(string format)
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddIntegerProperty("speed", "Speed", 0)
                        .WithFormat(format)
                    .BuildProperty(out IntegerProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static FloatProperty BuildFloat(string format)
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddFloatProperty("temperature", "Temperature", 0)
                        .WithFormat(format)
                    .BuildProperty(out FloatProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static EnumProperty BuildEnum(string format)
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddEnumProperty("mode", "Mode", "low")
                        .WithFormat(format)
                    .BuildProperty(out EnumProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static BooleanProperty BuildBoolean(string format)
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddBooleanProperty("running", "Running", false)
                        .WithFormat(format)
                    .BuildProperty(out BooleanProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        private static ColorProperty BuildColor(string format)
        {
            new DeviceBuilder(_deviceId, _deviceName)
                .AddNode(_nodeId, _nodeName, _nodeType)
                    .AddColorProperty("tint", "Tint", default)
                        .WithFormat(format)
                    .BuildProperty(out ColorProperty property)
                .BuildNode()
                .BuildDevice();

            return property;
        }

        // Both the factory and the one logger it hands out, so that whichever category
        // logs, it lands in the same list.
        private sealed class WarningRecorder : ILoggerFactory, ILogger
        {
            public ArrayList Warnings { get; } = new ArrayList();

            public ILogger CreateLogger(string categoryName) => this;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log(LogLevel logLevel, EventId eventId, string state, Exception? exception, MethodInfo? format)
            {
                if (logLevel == LogLevel.Warning)
                {
                    Warnings.Add(state);
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
