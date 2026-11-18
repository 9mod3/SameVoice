namespace SameVoice.App;

public static class BuildEdition
{
#if DUALROUTE
    public const bool DualRoute = true;
    public const string DisplayName = "双路由版";
#else
    public const bool DualRoute = false;
    public const string DisplayName = "本机静音版";
#endif

    public const string Version = "1.6.1";
}
