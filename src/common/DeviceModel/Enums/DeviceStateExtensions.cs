namespace SmartHome.DeviceModel.Enums
{
    public static class DeviceStateExtensions
    {
        /// <summary>
        /// A readable name for a state, for logs and test failure messages.
        /// </summary>
        /// <remarks>
        /// **Not a wire token, and shaped so it cannot be mistaken for one.** Homie
        /// publishes lowercase <c>init</c>, <c>ready</c>, <c>disconnected</c>,
        /// <c>sleeping</c> and <c>lost</c>; two of those are not even spelled like the
        /// model's names for the same states. Choosing the token is the adapter's job,
        /// and an adapter that published these capitalised names instead would fail its
        /// own conformance run immediately, which is the point of capitalising them.
        ///
        /// Written out rather than left to <c>Enum.ToString()</c> because that reads the
        /// enum's names through reflection, which a NoReflection firmware target does
        /// not carry.
        /// </remarks>
        public static string GetName(this DeviceState state) => state switch
        {
            DeviceState.Connecting => "Connecting",
            DeviceState.Ready => "Ready",
            DeviceState.Disconnecting => "Disconnecting",
            DeviceState.Sleeping => "Sleeping",
            DeviceState.Lost => "Lost",
            _ => "Unknown",
        };
    }
}
