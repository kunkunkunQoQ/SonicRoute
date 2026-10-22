namespace SonicRoute
{
    /// <summary>Keep packaged activation and commands separate from portable installs.</summary>
    internal sealed class AppRuntimeIdentity
    {
#if NET48
        internal static AppRuntimeIdentity Current { get; } = new AppRuntimeIdentity(false, true);
#else
        internal static AppRuntimeIdentity Current { get; } = new AppRuntimeIdentity(
            StoreInterop.NativePackageIdentity.IsPackaged(), false);
#endif

        internal bool IsPackaged { get; }
        internal string MutexName { get; }
        internal string ActivateSinkTitle { get; }
        internal string ActivateMessageName { get; }
        internal string CommandEdition { get; }

        internal AppRuntimeIdentity(bool packaged, bool legacy)
        {
            if (packaged && legacy) throw new System.ArgumentException("Legacy is not a packaged edition.");
            IsPackaged = packaged;
            string? instanceId = System.Environment.GetEnvironmentVariable("SONICROUTE_INSTANCE_ID");
            string instanceSuffix = string.IsNullOrWhiteSpace(instanceId) ? "" : "_" + instanceId.Trim();
            string suffix = instanceSuffix + (packaged ? "_Store" : legacy ? "_Legacy" : "");
            MutexName = @"Local\SonicRoute" + suffix + "_9NQZGRTPM1NT";
            ActivateSinkTitle = "SonicRoute" + suffix + "_ActivateSink";
            ActivateMessageName = "SonicRoute" + suffix + "_ActivateMain";
            CommandEdition = (string.IsNullOrWhiteSpace(instanceId) ? "" : instanceId.Trim() + "_") + (packaged ? "Store" : legacy ? "Legacy" : "Lite");
        }
    }
}
