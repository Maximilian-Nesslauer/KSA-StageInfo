namespace StageInfo.Core;

/// <summary>Per-feature debug toggles. Defaults are DEBUG-vs-Release; they can
/// be flipped at runtime (e.g. from a debugger).</summary>
internal static class DebugConfig
{
#if DEBUG
    public static bool StageInfo = true;
    public static bool Performance = true;
#else
    public static bool StageInfo = false;
    public static bool Performance = false;
#endif

    public static bool Any => StageInfo || Performance;
}
