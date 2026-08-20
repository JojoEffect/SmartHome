using nanoFramework.M2Mqtt.Messages;
using System;

namespace SmartHome.Homie.V4.Settings
{
    public class HomieClientSettings
    {
        public string ClientId { get; set; } = Guid.NewGuid().ToString();

        public string? UserName { get; set; } = null;

        public string? Password { get; set; } = null;

        public bool CleanSession { get; set; } = true;

        public ushort KeepAlivePeriod {  get; set; } = 10;
    }
}
