using nanoFramework.M2Mqtt.Messages;

namespace SmartHome.Homie.V4.Settings
{
    public class HomieClientSettings
    {
        /// <summary>
        /// MQTT client id. Left empty, <see cref="HomieClient"/> fills it with the
        /// device's topic id.
        /// </summary>
        /// <remarks>
        /// Deliberately not defaulted to a fresh Guid. That made the stable-client-id
        /// rule opt-out by accident: anyone constructing this type to set a username or
        /// a keep-alive also got a per-boot random id, and with one the broker keeps the
        /// dead session alive until keep-alive expires, delivering its 'lost' will after
        /// the rebooted device has already announced 'ready'. See the note in
        /// <see cref="HomieClient"/>'s constructor.
        /// </remarks>
        public string? ClientId { get; set; } = null;

        public string? UserName { get; set; } = null;

        public string? Password { get; set; } = null;

        public bool CleanSession { get; set; } = true;

        public ushort KeepAlivePeriod {  get; set; } = 10;
    }
}
