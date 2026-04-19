namespace StageInfo.Core;

/// <summary>Per-feature debug toggles. True in DEBUG, false in Release.</summary>
static class DebugConfig
{
#if DEBUG
    public static bool StageInfo = true;
    public static bool Performance = false;
#else
    public static bool StageInfo = false;
    public static bool Performance = false;
#endif

    public static bool Any => StageInfo || Performance;
}
