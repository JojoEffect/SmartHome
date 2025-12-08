using HomieNano.Version4.Enums;

namespace HomieNano.Version4.EventArgs
{
    public class DeviceStateChangeEventArgs : System.EventArgs
    {
        internal DeviceStateChangeEventArgs(Device device, State oldState, State currentState)
        {
            Device = device;
            PreviousState = oldState;
            CurrentState = currentState;
        }

        public Device Device { get; }
        public State PreviousState { get; }
        public State CurrentState { get; }
    }
}
