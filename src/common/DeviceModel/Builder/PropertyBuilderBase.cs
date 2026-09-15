using SmartHome.DeviceModel.Enums;

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
    }
}
