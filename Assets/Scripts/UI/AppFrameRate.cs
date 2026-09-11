using UnityEngine;

// The app's frame-rate ceiling, set once, everywhere.
//
// Nothing set one before this: there is no other Application.targetFrameRate in the project and
// both quality levels carry vSyncCount: 0, so the app ran at whatever the platform happened to
// choose. That is unstated behaviour on a device with a thermal budget — on a ProMotion phone it
// means rendering the menu as fast as the hardware allows, for nothing.
//
// 60 rather than 120: physics is a fixed step (see the project's 100 Hz setting), so nothing about
// how the robot drives depends on the render rate, and the second sixty frames buy no visible
// smoothness on a menu or a chase camera. What they cost is heat, and a hot phone throttles the
// frames it was already drawing.
//
// [RuntimeInitializeOnLoadMethod] rather than a component on the home screen: this has to hold in
// the field scene too, including when that scene is opened directly in the Editor without ever
// passing through HomeScene. BeforeSceneLoad runs before any scene's Awake, in every entry path.
public static class AppFrameRate
{
    public const int TargetFrameRate = 60;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Apply()
    {
        // vSync overrides targetFrameRate entirely when it is on, so the cap below would be
        // silently ignored on any quality level that enabled it later.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = TargetFrameRate;
    }
}
