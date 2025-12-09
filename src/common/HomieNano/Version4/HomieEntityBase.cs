using System.Text;

namespace HomieNano.Version4
{
    public abstract class HomieEntityBase : IHomieEntity
    {
        private readonly string _topicId;
        private IHomieEntity? _parent;

        protected HomieEntityBase(string topicId, IHomieEntity? parent = null) 
        {
            _topicId = topicId;
            _parent = parent;
        }

        public virtual IHomieEntity? Parent
        {
            get => _parent;
            internal set => _parent = value;
        }

        public virtual string TopicId => _topicId;

        public virtual byte[] GetPayload() => Encoding.UTF8.GetBytes(string.Empty);

        public virtual string GetTopic()
        {
            if (Parent is null)
            {
                return $"{Constants.RootTopicId}{Constants.TopicSeparator}{TopicId}";
            }

            return $"{Parent.GetTopic()}{Constants.TopicSeparator}{TopicId}";
        }
    }
}
