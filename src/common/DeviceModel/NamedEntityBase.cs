using System;

namespace SmartHome.DeviceModel
{
    /// <summary>
    /// An entity with an id and a friendly name: a device, a node or a property.
    /// </summary>
    public abstract class NamedEntityBase : EntityBase
    {
        protected NamedEntityBase(string id, string name, EntityBase? parent = null)
            : base(ValidateId(id), parent)
        {
            Name = name;
        }

        /// <summary>The human-readable name, which unlike the id has no character rule.</summary>
        public string Name { get; }

        /// <summary>
        /// Enforces the id rule at construction rather than on the wire.
        /// </summary>
        /// <remarks>
        /// An id "MAY contain lowercase letters from a to z, numbers from 0 to 9 as well
        /// as the hyphen character", and "MUST NOT start or end with a hyphen".
        ///
        /// That is Homie's rule, and it stays in the model rather than moving to the
        /// Homie adapter because it is the *intersection* of what the conventions this
        /// model targets will accept: Homie v4 and v5 state it outright, and while Home
        /// Assistant is laxer about entity ids, an id that satisfies this is safe
        /// everywhere. Validating the loosest thing here and the strictest thing in each
        /// adapter would mean a device that builds and then fails to publish.
        ///
        /// Alert ids are held to the same rule, for the same reason: Homie v5 puts them
        /// in a topic level.
        /// </remarks>
        internal static string ValidateId(string id)
        {
            if (id == null || id.Length == 0)
            {
                throw new ArgumentException("An entity id must not be empty.");
            }

            if (id[0] == '-' || id[id.Length - 1] == '-')
            {
                throw new ArgumentException($"Invalid id '{id}': it must not start or end with a hyphen.");
            }

            // Indexed, not foreach: nanoFramework's string is not IEnumerable<char>, so
            // foreach hands back object and the comparisons below don't compile.
            for (int i = 0; i < id.Length; i++)
            {
                var c = id[i];
                var allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!allowed)
                {
                    throw new ArgumentException($"Invalid id '{id}': only lowercase a-z, digits 0-9 and hyphens are allowed.");
                }
            }

            return id;
        }
    }
}
