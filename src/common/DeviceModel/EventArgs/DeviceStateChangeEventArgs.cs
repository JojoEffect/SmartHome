using SmartHome.DeviceModel.Enums;

namespace SmartHome.DeviceModel.EventArgs
{
    /// <summary>The device moved from one lifecycle state to another.</summary>
    public class DeviceStateChangeEventArgs : System.EventArgs
    {
        internal DeviceStateChangeEventArgs(Device device, DeviceState oldState, DeviceState currentState)
        {
            Device = device;
            PreviousState = oldState;
            CurrentState = currentState;
        }

        public Device Device { get; }

        public DeviceState PreviousState { get; }

        public DeviceState CurrentState { get; }
    }
}
