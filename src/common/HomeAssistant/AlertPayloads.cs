using SmartHome.DeviceModel;
using System.Text;

namespace SmartHome.HomeAssistant
{
    /// <summary>
    /// What a device's raised alerts look like on the two topics the diagnostic entity
    /// reads.
    /// </summary>
    /// <remarks>
    /// The state topic can say only whether anything is wrong, so everything that makes
    /// one alert different from another -- the id, and the message -- goes to the
    /// attributes topic. Each raised alert becomes one attribute, keyed by its id, which
    /// is exactly what the id is carried in the model for.
    /// </remarks>
    internal static class AlertPayloads
    {
        /// <summary>The key carrying every raised id, whether or not each also got its own.</summary>
        /// <remarks>
        /// Redundant on purpose. Home Assistant drops attributes whose key is one of its
        /// own entity properties (<c>MQTT_ATTRIBUTES_BLOCKED</c>), and three of those --
        /// <c>state</c>, <c>available</c> and <c>icon</c> -- are ids a device may
        /// legitimately raise an alert under. Those alerts are filtered out of the
        /// attributes silently, in Home Assistant, where this library cannot see it; this
        /// one key is never filtered, so the raised set is always readable even when one
        /// of its messages is not.
        /// </remarks>
        internal const string AllAlertsKey = "alerts";

        /// <summary>
        /// <c>ON</c> while any alert is raised.
        /// </summary>
        /// <remarks>
        /// The whole of what a lifecycle-free convention can say about health, which is
        /// why the model keeps alerts as a keyed set and lets each adapter collapse it.
        /// </remarks>
        internal static string State(bool hasAlerts)
            => hasAlerts ? HomeAssistantTopics.On : HomeAssistantTopics.Off;

        /// <summary>
        /// The raised alerts as a JSON object: every id, and each alert's message under
        /// its own id.
        /// </summary>
        /// <remarks>
        /// Sorted by id, and that is not cosmetic: the model's alert set is a hashtable
        /// whose enumeration order is neither stable nor the author's, so an unsorted
        /// rendering would publish a different payload for the same set of alerts and
        /// every capture comparing two runs would disagree for no reason.
        ///
        /// An alert with an empty message contributes no attribute of its own -- the
        /// writer skips an empty value, since a key Home Assistant has to accept is
        /// different from one it can default -- but its id is still in
        /// <see cref="AllAlertsKey"/>.
        ///
        /// An empty set renders as <c>{}</c>, which Home Assistant reads as "no
        /// attributes" rather than as an error.
        ///
        /// The array is sorted in place. <c>Device.Alerts</c> hands out a fresh copy
        /// every time it is read, so nothing else is looking at this one.
        /// </remarks>
        internal static string Attributes(Alert[] alerts)
        {
            SortById(alerts);

            var json = new JsonWriter();

            json.String(AllAlertsKey, JoinIds(alerts));

            for (int i = 0; i < alerts.Length; i++)
            {
                json.String(alerts[i].Id, alerts[i].Message);
            }

            return json.ToJson();
        }

        /// <remarks>
        /// An insertion sort, written out because this runtime's <c>Array</c> carries no
        /// <c>Sort</c>. A device has a handful of alerts at most, and in the usual case
        /// -- none, or one -- the loop body never runs.
        /// </remarks>
        private static void SortById(Alert[] alerts)
        {
            for (int i = 1; i < alerts.Length; i++)
            {
                var current = alerts[i];
                var j = i - 1;

                while (j >= 0 && string.Compare(alerts[j].Id, current.Id) > 0)
                {
                    alerts[j + 1] = alerts[j];
                    j--;
                }

                alerts[j + 1] = current;
            }
        }

        private static string JoinIds(Alert[] alerts)
        {
            var builder = new StringBuilder();

            for (int i = 0; i < alerts.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(alerts[i].Id);
            }

            return builder.ToString();
        }
    }
}
