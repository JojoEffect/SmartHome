using SmartHome.DeviceModel;
using SmartHome.DeviceModel.Properties;

namespace SmartHome.Homie.V4
{
    /// <summary>
    /// Builds the v4 topic for anything in a device tree.
    /// </summary>
    /// <remarks>
    /// The model has no <c>GetTopic()</c>, by design: its predecessor built a path from
    /// the parent chain, which put this convention's root and this convention's grammar
    /// into every entity of every device. The walk lives here instead, so a second
    /// adapter can name the same tree its own way.
    ///
    /// The ids need no checking or rewriting on the way through. The model's
    /// <c>NamedEntityBase.ValidateId</c> already holds every id to lowercase a-z, digits
    /// and the hyphen, with no leading or trailing hyphen, which is v4's rule -- refused
    /// where it was written rather than on the wire.
    /// </remarks>
    public static class HomieTopics
    {
        /// <summary>
        /// The entity's own topic: <c>homie/&lt;device&gt;[/&lt;node&gt;[/&lt;property&gt;]]</c>.
        /// </summary>
        /// <remarks>
        /// Nothing is memoised here, unlike the entity-owned version this replaces. That
        /// one had to cache because it was called on every publish and could be reached
        /// before the tree was finished; an adapter calls this once per entity, at
        /// construction, on a tree that can no longer change, and caches the result
        /// itself.
        /// </remarks>
        public static string Of(EntityBase entity)
            => entity.Parent == null
                ? $"{Constants.RootTopicId}{Constants.TopicSeparator}{entity.Id}"
                : $"{Of(entity.Parent)}{Constants.TopicSeparator}{entity.Id}";

        /// <summary>
        /// One of the entity's <c>$</c>-prefixed attributes, e.g.
        /// <c>homie/&lt;device&gt;/$name</c>.
        /// </summary>
        /// <param name="attributeTopicId">
        /// The attribute's topic id, including its <c>$</c> -- see <see cref="Constants"/>.
        /// The spec reserves that prefix for attributes, which is why an entity id may
        /// not contain one.
        /// </param>
        public static string Attribute(EntityBase entity, string attributeTopicId)
            => $"{Of(entity)}{Constants.TopicSeparator}{attributeTopicId}";

        /// <summary>
        /// Where a controller writes to a settable property:
        /// <c>homie/&lt;device&gt;/&lt;node&gt;/&lt;property&gt;/set</c>.
        /// </summary>
        /// <remarks>
        /// A separate topic from the property's value, and the distinction is not
        /// cosmetic: a device that subscribed to the value topic instead would never
        /// receive a command, and would re-consume its own retained publishes as if a
        /// controller had sent them.
        /// </remarks>
        public static string Command(PropertyBase property)
            => $"{Of(property)}{Constants.TopicSeparator}{Constants.SetPropertyTopicId}";
    }
}
