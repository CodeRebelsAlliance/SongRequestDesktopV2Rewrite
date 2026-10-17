using System.Reflection;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Application-wide metadata (version etc.) without any WPF dependency.
    /// Replaces the <c>About.version</c> field that used to live on the WPF window.
    ///
    /// <para>
    /// The version comes from the project's <c>&lt;Version&gt;</c> (surfaced as the
    /// assembly informational version), so there is a single place to bump it. It is
    /// shown in the UI, compared against the GitHub release tag by the updater, used
    /// as the release tag by CI and written into the Windows "Apps &amp; features"
    /// entry.
    /// </para>
    /// </summary>
    public static class AppInfo
    {
        /// <summary>The app version, e.g. <c>4.0</c>.</summary>
        public static string version { get; } = ResolveVersion();

        private static string ResolveVersion()
        {
            try
            {
                var informational = typeof(AppInfo).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informational))
                {
                    // Drop source-revision metadata the SDK appends (e.g. "4.0+abc1234").
                    var plus = informational.IndexOf('+');
                    return (plus > 0 ? informational[..plus] : informational).Trim();
                }
            }
            catch
            {
                // Fall through to the fallback below.
            }

            return "0.0";
        }
    }
}
