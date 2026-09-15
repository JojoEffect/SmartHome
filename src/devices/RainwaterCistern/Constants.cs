namespace SmartHome.Devices.RainwaterCistern
{
    internal class Constants
    {
        public const string DeviceTopicId = "rainwater-cistern";
        public const string DeviceName = "Regenwasser Zisterne";

        public const string NodeTankTopicId = "tank";
        public const string NodeTankName = "Zisterne";
        public const string NodeTankType = "Hydrostatic 4-20mA probe";

        public const string PropertyWaterDepthTopicId = "water-depth";
        public const string PropertyWaterDepthName = "Wasserstand";

        public const string PropertyLevelTopicId = "level";
        public const string PropertyLevelName = "Fuellstand";

        public const string PropertyVolumeTopicId = "volume";
        public const string PropertyVolumeName = "Volumen";

        public const string PropertyLoopCurrentTopicId = "loop-current";
        public const string PropertyLoopCurrentName = "Schleifenstrom";
    }
}
