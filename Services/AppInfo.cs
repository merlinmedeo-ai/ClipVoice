using System.Reflection;

namespace ClipVoice.Services;

public static class AppInfo
{
    public static string Version
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is null) return "1.0.0";
            var build = version.Build < 0 ? 0 : version.Build;
            return $"{version.Major}.{version.Minor}.{build}";
        }
    }

    public static string DisplayVersion => "v" + Version;
}
