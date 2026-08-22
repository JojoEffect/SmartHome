using SmartHome.Homie.V4.Enums;

namespace SmartHome.Homie.V4.EventArgs
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
