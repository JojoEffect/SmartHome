using SmartHome.Homie.V4.Enums;

namespace SmartHome.Homie.V4.Extensions
{
    public static class StateExtensions
    {
        public static string GetString(this State state) => state switch
        {
            State.Init => "init",
            State.Ready => "ready",
            State.Disconnected => "disconnected",
            State.Sleeping => "sleeping",
            State.Lost => "lost",
            State.Alert => "alert",
            _ => "unknown"
        };
    }
}
