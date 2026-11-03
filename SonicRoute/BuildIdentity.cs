namespace SonicRoute;

internal static class BuildIdentity
{
#if NET48
    internal const string Edition = "Legacy";
    public const string DisplayName = "SonicRoute Legacy";
#elif SONICROUTE_PHONE
    internal const string Edition = "Full";
    public const string DisplayName = "SonicRoute";
#else
    internal const string Edition = "Lite";
    public const string DisplayName = "SonicRoute Lite";
#endif
}
