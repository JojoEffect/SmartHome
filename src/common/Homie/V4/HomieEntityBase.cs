using System.Text;

namespace SmartHome.Homie.V4
{
    public abstract class HomieEntityBase : IHomieEntity
    {
        private readonly string _topicId;
        private IHomieEntity? _parent;
        private string? _topic;

        protected HomieEntityBase(string topicId, IHomieEntity? parent = null)
        {
            _topicId = topicId;
            _parent = parent;
        }

        public virtual IHomieEntity? Parent
        {
            get => _parent;
            internal set
            {
                _parent = value;
                // The cached topic is derived from the parent chain, so re-parenting
                // invalidates it. In practice this is set once, during building.
                _topic = null;
            }
        }

        public virtual string TopicId => _topicId;

        public virtual byte[] GetPayload() => Encoding.UTF8.GetBytes(string.Empty);

        // Cached: the topic is derived from a readonly id and a parent chain that is
        // fixed once building finishes, yet this was recomputed on every publish --
        // walking the chain and allocating a string at each level. A property update is
        // three nested calls; an announce is one per attribute of every node and
        // property, and it re-runs on every reconnect.
        public virtual string GetTopic()
        {
            if (_topic is not null)
            {
                return _topic;
            }

            var topic = Parent is null
                ? $"{Constants.RootTopicId}{Constants.TopicSeparator}{TopicId}"
                : $"{Parent.GetTopic()}{Constants.TopicSeparator}{TopicId}";

            // Only memoise once the chain reaches a Device. The builder attaches a
            // property to its node before it attaches that node to the device, so during
            // that window a property's chain is incomplete and its topic is missing the
            // device level. Caching then would make it permanently wrong, and clearing
            // _topic when Parent changes does not help -- that only clears the entity
            // being re-parented, never the descendants whose topics also just changed.
            // The walk costs one pass per entity, and only until the tree is finished.
            if (IsRootedAtDevice())
            {
                _topic = topic;
            }

            return topic;
        }

        private bool IsRootedAtDevice()
        {
            IHomieEntity entity = this;
            while (entity.Parent is not null)
            {
                entity = entity.Parent;
            }

            return entity is Device;
        }
    }
}
