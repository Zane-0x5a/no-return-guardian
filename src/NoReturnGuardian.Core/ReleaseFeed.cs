using System;
using System.Text.RegularExpressions;

namespace NoReturnGuardian.Core
{
    /// <summary>
    /// The published releases of this project on GitHub. A check reads only where the latest-release page redirects:
    /// it needs no API quota (shared proxy exits exhaust GitHub's unauthenticated API limit) and carries no body.
    /// Only the version number is taken from it; the page Guardian opens is always built from that version.
    /// </summary>
    public static class ReleaseFeed
    {
        public const string Repository = "Zane-0x5a/no-return-guardian";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        public const string LatestPage = ReleasesPage + "/latest";

        private const string ReleasesPath = "/" + Repository + "/releases";
        private const string TagPath = ReleasesPath + "/tag/";
        private static readonly Regex Tag = new Regex(@"^v(\d{1,5})\.(\d{1,5})\.(\d{1,5})$", RegexOptions.CultureInvariant);

        /// <summary>
        /// GitHub redirects the latest-release page to the tag page of the newest published release (drafts and
        /// pre-releases never count), or back to the release list while nothing is published. Returns that release's
        /// version, or null when nothing is published or its tag is not vX.Y.Z. Any other target (another site, a
        /// sign-in page, a missing header) throws FormatException, so the caller treats it as a failed check.
        /// </summary>
        public static Version ParseLatestRedirect(string location)
        {
            Uri target;
            if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(new Uri(LatestPage), location, out target))
            {
                throw new FormatException("The latest-release page did not redirect.");
            }

            string path = target.AbsolutePath.TrimEnd('/');
            if (target.Scheme != Uri.UriSchemeHttps || !string.Equals(target.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("The latest-release page redirected to another site.");
            }

            if (string.Equals(path, ReleasesPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!path.StartsWith(TagPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("The latest-release page redirected outside this project's releases.");
            }

            Match match = Tag.Match(Uri.UnescapeDataString(path.Substring(TagPath.Length)));
            return match.Success
                ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value))
                : null;
        }

        /// <summary>A stored version string from an earlier check, or null when it is missing or malformed.</summary>
        public static Version ParseStored(string value)
        {
            Version version;
            return !string.IsNullOrWhiteSpace(value) && Version.TryParse(value, out version) && version.Build >= 0
                ? new Version(version.Major, version.Minor, version.Build)
                : null;
        }

        /// <summary>Whether <paramref name="latest"/> is newer, comparing major.minor.patch only.</summary>
        public static bool IsNewer(Version latest, Version current)
        {
            return latest != null && current != null
                && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        }

        public static string Display(Version version)
        {
            return version.ToString(3);
        }

        public static string PageFor(Version version)
        {
            return version == null ? ReleasesPage : ReleasesPage + "/tag/v" + Display(version);
        }
    }
}
