namespace HomieNano.Version4.Enums
{
    public enum State
    {
        Init = 0,
        Ready = 1,
        Disconnected = 2,
        Sleeping = 3,
        Lost = 4,
        Alert = 5
    }

    public static class StateExtensions
    {
        public static string ToHomieString(this State state) => state switch
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
