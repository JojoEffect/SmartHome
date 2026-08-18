namespace HomieNano.Version4.Settings
{
    public class HomiePublishSettings
    {
        /// <summary>
        /// Gets or sets the settings used to configure device state publishing behavior.
        /// </summary>
        public PublishSettings DeviceStatePublishSettings { get; set; } = new PublishSettings();

        /// <summary>
        /// Gets or sets the settings used to configure how device information is published when the device is initialized.
        /// </summary>
        public PublishSettings DeviceInfoPublishSettings { get; set; } = new PublishSettings();

        /// <summary>
        /// Gets or sets the settings used to configure node information publishing behaviour.
        /// </summary>
        public PublishSettings NodeInfoPublishSettings { get; set; } = new PublishSettings();

        /// <summary>
        /// Gets or sets the settings used to configure property information publishing behavior.
        /// </summary>
        public PublishSettings PropertyInfoPublishSettings { get; set; } = new PublishSettings();

        /// <summary>
        /// Gets or sets the settings used to configure how property updates are published.
        /// </summary>
        public PublishSettings PropertyUpdatePublishSettings { get; set; } = new PublishSettings();
    }
}
