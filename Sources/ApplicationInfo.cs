using System;
using System.Reflection;

namespace DROPME
{
    /// <summary>
    /// Provides application metadata used by the desktop UI.
    /// </summary>
    internal static class ApplicationInfo
    {
        public static readonly string VersionText = GetVersionText();

        private static string GetVersionText()
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? string.Empty : version.ToString(3);
        }
    }
}
