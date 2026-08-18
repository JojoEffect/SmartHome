using HomieNano.Version4.Attributes;

namespace HomieNano.Version4
{
    public class NamedHomieEntityBase : HomieEntityBase
    {
        private readonly NameAttribute _nameAttribute;

        public NamedHomieEntityBase(string topicId, string name, IHomieEntity? parent = null) :
            base(topicId, parent)
        {
            _nameAttribute = new(this, name);
        }

        public virtual NameAttribute NameAttribute => _nameAttribute;
    }
}
