using SmartHome.Homie.V4.Enums;
using SmartHome.Homie.V4.Extensions;
using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public class StateAttribute : AttributeBase
    {
        private State _value;

        public StateAttribute(IHomieEntity parent, State state)
            : base($"{Constants.StateAttributeTopicId}", parent)
        {
            _value = state;
        }

        public State Value { get { return _value; } internal set { _value = value; } }

        public override byte[] GetPayload() => Encoding.UTF8.GetBytes(Value.GetString());
    }
}
