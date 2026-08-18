using System.Text;

namespace HomieNano.Version4.Attributes
{
    public abstract class AttributeBase : HomieEntityBase
    {
        protected AttributeBase(string topicId, IHomieEntity parent) :
            base(topicId, parent)
        {
        }

        public abstract byte[] GetPayload();
    }
}
