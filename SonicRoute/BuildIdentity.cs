using System.Reflection;

namespace SonicRoute;

internal static class BuildIdentity
{
    internal static string ProductVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(BuildIdentity).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version!.ToString(2);
        int suffix = version.IndexOf('+');
        return suffix < 0 ? version : version.Substring(0, suffix);
    }

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
