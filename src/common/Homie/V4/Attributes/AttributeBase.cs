using System.Text;

namespace SmartHome.Homie.V4.Attributes
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
