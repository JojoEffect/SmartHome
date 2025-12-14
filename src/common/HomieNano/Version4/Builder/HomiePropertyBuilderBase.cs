using HomieNano.Version4.Enums;

namespace HomieNano.Version4.Builder
{
    public abstract class HomiePropertyBuilderBase
    {
        protected readonly string _topicId;
        protected readonly string _name;
        protected readonly DataType _dataType;

        protected string _format = string.Empty;
        protected bool _settable = false;
        protected bool _retained = true;
        protected Unit _unit = Unit.None;

        protected readonly HomieNodeBuilder _nodeBuilder;

        protected HomiePropertyBuilderBase(HomieNodeBuilder nodeBuilder, string topicId, string name, DataType dataType)
        {
            _nodeBuilder = nodeBuilder;
            _topicId = topicId;
            _name = name;
            _dataType = dataType;
        }

        public abstract HomieNodeBuilder BuildProperty();
    }
}
