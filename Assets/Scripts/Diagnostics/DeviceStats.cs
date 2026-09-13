using System.Runtime.InteropServices;

// What iOS knows about the phone that Unity doesn't pass on: how hot it is, whether Low Power Mode is on, how much
// memory iOS counts against the app and how much more it may take, and how long ago the process started. The native
// half is Assets/Plugins/iOS/RoboSimDeviceStats.mm. Everywhere else, the editor included, every answer is "unknown":
// Thermal.Unknown, or -1.
public static class DeviceStats
{
    // NSProcessInfoThermalState, in iOS's own order.
    public enum Thermal { Unknown = -1, Nominal = 0, Fair = 1, Serious = 2, Critical = 3 }

#if UNITY_IOS && !UNITY_EDITOR
    public static Thermal ThermalState => (Thermal)RoboSimThermalState();
    public static int LowPowerMode => RoboSimLowPowerMode();
    public static long FootprintBytes => RoboSimFootprintBytes();
    public static long AvailableBytes => RoboSimAvailableMemoryBytes();
    public static double SecondsSinceProcessStart => RoboSimSecondsSinceProcessStart();

    // Each name must be a function in RoboSimDeviceStats.mm and each type its C type: int is int, long is int64_t,
    // double is double. A name that isn't there compiles here and fails in Xcode as "Undefined symbol"; a type that
    // doesn't match links and returns garbage. Validate Performance Overlay compares the two files.
    [DllImport("__Internal")] private static extern int RoboSimThermalState();
    [DllImport("__Internal")] private static extern int RoboSimLowPowerMode();
    [DllImport("__Internal")] private static extern long RoboSimFootprintBytes();
    [DllImport("__Internal")] private static extern long RoboSimAvailableMemoryBytes();
    [DllImport("__Internal")] private static extern double RoboSimSecondsSinceProcessStart();
#else
    public static Thermal ThermalState => Thermal.Unknown;
    public static int LowPowerMode => -1;
    public static long FootprintBytes => -1;
    public static long AvailableBytes => -1;
    public static double SecondsSinceProcessStart => -1;
#endif
}
