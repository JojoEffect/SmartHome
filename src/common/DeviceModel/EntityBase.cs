namespace SmartHome.DeviceModel
{
    /// <summary>
    /// Anything in the device tree: a device, one of its nodes, or one of a node's
    /// properties.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <c>GetTopic()</c> here, and that single omission is what
    /// makes this model protocol-neutral. Its predecessor built <c>homie/&lt;device&gt;/
    /// &lt;node&gt;/&lt;property&gt;</c> from the parent chain, which put the Homie root
    /// topic and Homie's topic grammar into every entity in the tree -- so a second
    /// convention meant either a parallel publisher or a fork. An adapter now walks this
    /// chain and names things whichever way its own convention requires: Homie v4 nests
    /// under <c>homie/</c>, Homie v5 under <c>homie/5/</c>, and Home Assistant has no
    /// node level at all and flattens the two lower levels into one entity id.
    /// </remarks>
    public abstract class EntityBase
    {
        private readonly string _id;

        protected EntityBase(string id, EntityBase? parent = null)
        {
            _id = id;
            Parent = parent;
        }

        /// <summary>
        /// The entity one level up: a property's node, a node's device. Null for the
        /// device at the root.
        /// </summary>
        /// <remarks>
        /// Settable only from inside this assembly, and in practice only while a device
        /// is being built: the builder attaches a property to its node before it attaches
        /// that node to the device, so the chain is briefly incomplete and then fixed for
        /// the entity's life.
        /// </remarks>
        public EntityBase? Parent { get; internal set; }

        /// <summary>
        /// This entity's own id: one level of the tree, not a path.
        /// </summary>
        /// <remarks>
        /// Validated at construction by <see cref="NamedEntityBase.ValidateId"/>, so an
        /// id that no convention could carry is refused where it was written rather than
        /// on the wire.
        /// </remarks>
        public string Id => _id;
    }
}
