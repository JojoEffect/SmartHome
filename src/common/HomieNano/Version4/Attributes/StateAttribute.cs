using HomieNano.Version4.Enums;

namespace HomieNano.Version4.Attributes
{
    public class StateAttribute : AttributeBase
    {
        private readonly State _value;

        public StateAttribute(IHomieEntity parent, State state)
            : base($"{Constants.StateAttributeTopicId}", parent)
        {
            _value = state;
        }

        public State Value => _value;

        public override string GetPayload() => Value.ToString().ToLower();
    }
}
