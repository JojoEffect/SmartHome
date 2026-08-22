using SmartHome.Homie.V4.Attributes;
using System;

namespace SmartHome.Homie.V4
{
    public class NamedHomieEntityBase : HomieEntityBase
    {
        private readonly NameAttribute _nameAttribute;

        public NamedHomieEntityBase(string topicId, string name, IHomieEntity? parent = null) :
            base(ValidateTopicId(topicId), parent)
        {
            _nameAttribute = new(this, name);
        }

        /// <summary>
        /// Enforces the Homie v4 rule for a topic level id, at construction rather than
        /// on the wire.
        /// </summary>
        /// <remarks>
        /// The spec: an id "MAY contain lowercase letters from a to z, numbers from 0 to
        /// 9 as well as the hyphen character", and "MUST NOT start or end with a hyphen".
        /// This sits on NamedHomieEntityBase, which is exactly device, node and property
        /// -- attributes derive from HomieEntityBase directly and their ids start with
        /// '$', which the spec reserves for them.
        /// </remarks>
        internal static string ValidateTopicId(string topicId)
        {
            if (topicId == null || topicId.Length == 0)
            {
                throw new ArgumentException("A Homie topic id must not be empty.");
            }

            if (topicId[0] == '-' || topicId[topicId.Length - 1] == '-')
            {
                throw new ArgumentException($"Invalid Homie topic id '{topicId}': it must not start or end with a hyphen.");
            }

            // Indexed, not foreach: nanoFramework's string is not IEnumerable<char>, so
            // foreach hands back object and the comparisons below don't compile.
            for (int i = 0; i < topicId.Length; i++)
            {
                var c = topicId[i];
                var allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!allowed)
                {
                    throw new ArgumentException($"Invalid Homie topic id '{topicId}': only lowercase a-z, digits 0-9 and hyphens are allowed.");
                }
            }

            return topicId;
        }

        public virtual NameAttribute NameAttribute => _nameAttribute;
    }
}
