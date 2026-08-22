using System.Text;

namespace SmartHome.Homie.V4.Attributes
{
    public abstract class AttributeBase : HomieEntityBase
    {
        protected AttributeBase(string topicId, IHomieEntity parent) :
            base(topicId, parent)
        {
        }

        // `override`, not a new member: HomieEntityBase.GetPayload() is virtual and
        // returns an empty payload. Redeclaring it as plain `abstract` hid it instead of
        // overriding it, so a call through IHomieEntity or a HomieEntityBase reference
        // would have dispatched to the empty base rather than to the attribute. Nothing
        // publishes attributes through a base reference today, which is the only reason
        // it never bit.
        public abstract override byte[] GetPayload();
    }
}
