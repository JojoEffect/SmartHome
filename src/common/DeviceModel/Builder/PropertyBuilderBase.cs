using SmartHome.DeviceModel.Enums;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;

namespace SmartHome.DeviceModel.Builder
{
    /// <summary>
    /// What every property builder carries, whatever its datatype.
    /// </summary>
    /// <remarks>
    /// The format is deliberately not here. Each datatype restricts its values in its own
    /// shape -- a numeric range, a set of enum options, a pair of boolean labels, a list
    /// of colour encodings -- and putting a single <c>string _format</c> on this base is
    /// exactly what let the old model hand the same text to readers that disagreed about
    /// what it meant. Each derived builder therefore declares its own format method,
    /// typed to what that datatype can actually say.
    ///
    /// What every datatype does share is what happens when that text does not parse,
    /// and that part is here: <see cref="ReportUnparsedFormat"/>.
    /// </remarks>
    public abstract class PropertyBuilderBase
    {
        protected readonly string _id;
        protected readonly string _name;
        protected readonly DataType _dataType;

        protected bool _settable = false;
        protected bool _retained = true;
        protected string _unit = Units.None;
        protected QuantityKind _quantityKind = QuantityKind.None;

        protected readonly NodeBuilder _nodeBuilder;

        protected PropertyBuilderBase(NodeBuilder nodeBuilder, string id, string name, DataType dataType)
        {
            _nodeBuilder = nodeBuilder;
            _id = id;
            _name = name;
            _dataType = dataType;
        }

        public abstract NodeBuilder BuildProperty();

        /// <summary>
        /// Reports format text that did not parse, naming the property and the text.
        /// </summary>
        /// <param name="format">The text a <c>WithFormat</c> call was handed.</param>
        /// <param name="expected">What it should have read as, e.g. <c>"a range"</c>.</param>
        /// <remarks>
        /// Text that does not parse declares nothing -- a device author's malformed
        /// declaration is not a reason to stop the device from being built -- but it
        /// does not do so in silence. A property keeps only the parsed value, never the
        /// text, so once a builder has let the text go nothing downstream can tell a
        /// declaration that failed from one that was never written, an adapter included.
        /// This is the last moment anything holds the text, which is why the report is
        /// made here, once, for every datatype.
        ///
        /// Empty text is not reported. It is how a caller with nothing to declare says
        /// so -- a helper whose format is optional passes an empty one straight through
        /// -- rather than text that was meant to declare something and did not.
        ///
        /// The logger is fetched here rather than held, so a builder that is never handed
        /// a malformed format never creates one.
        /// </remarks>
        protected void ReportUnparsedFormat(string format, string expected)
        {
            if (string.IsNullOrEmpty(format))
            {
                return;
            }

            this.GetCurrentClassLogger().LogWarning($"Property '{_id}' ignored the format '{format}': it does not parse as {expected}.");
        }
    }
}
