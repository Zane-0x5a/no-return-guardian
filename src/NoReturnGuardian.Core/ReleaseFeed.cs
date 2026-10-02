using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NoReturnGuardian.Core
{
    /// <summary>
    /// The published releases of this project on GitHub. A check reads only where the latest-release page redirects:
    /// it needs no API quota (shared proxy exits exhaust GitHub's unauthenticated API limit) and carries no body.
    /// Only the version number is taken from it; every page and download address is built from that version.
    /// </summary>
    public static class ReleaseFeed
    {
        public const string Repository = "Zane-0x5a/no-return-guardian";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        public const string LatestPage = ReleasesPage + "/latest";
        /// <summary>The checksum list scripts\package.ps1 publishes beside the installer and the zip.</summary>
        public const string ChecksumsName = "SHA256SUMS.txt";

        private const string ReleasesPath = "/" + Repository + "/releases";
        private const string TagPath = ReleasesPath + "/tag/";
        private const int Timeout = 30000;
        // 安装程序约 40 MB；校验和清单只有两行。
        private const long MaxSetupBytes = 256L * 1024 * 1024;
        private const int MaxChecksumsBytes = 64 * 1024;
        private static readonly Regex Tag = new Regex(@"^v(\d{1,5})\.(\d{1,5})\.(\d{1,5})$", RegexOptions.CultureInvariant);
        private static readonly Regex ChecksumLine = new Regex(@"^([0-9a-fA-F]{64}) [ *](.+)$", RegexOptions.CultureInvariant);

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

        /// <summary>Asks the latest-release page where it redirects: one HEAD request, no redirect followed, no body read.</summary>
        public static Version FetchLatest(Version client)
        {
            HttpWebRequest request = Request(LatestPage, client);
            request.Method = "HEAD";
            request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                int status = (int)response.StatusCode;
                if (status < 300 || status >= 400)
                {
                    throw new FormatException("The latest-release page answered " + status + " instead of redirecting.");
                }

                return ParseLatestRedirect(response.Headers[HttpResponseHeader.Location]);
            }
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

        /// <summary>The installer's file name, as scripts\package.ps1 names it.</summary>
        public static string SetupName(Version version)
        {
            return "NoReturnGuardian-" + Display(version) + "-setup.exe";
        }

        /// <summary>Where a release's files download from; GitHub redirects these to its asset storage.</summary>
        public static string AssetsFor(Version version)
        {
            return ReleasesPage + "/download/v" + Display(version) + "/";
        }

        /// <summary>The SHA-256 a checksum list gives for <paramref name="file"/>, in lowercase, or null when it lists none.</summary>
        public static string ChecksumFor(string sums, string file)
        {
            foreach (string line in (sums ?? string.Empty).Split('\n'))
            {
                Match match = ChecksumLine.Match(line.Trim());
                if (match.Success && string.Equals(match.Groups[2].Value, file, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups[1].Value.ToLowerInvariant();
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads the installer of <paramref name="version"/> from <paramref name="assets"/> (normally
        /// <see cref="AssetsFor"/>) into <paramref name="folder"/>. Returns its path only once it matches the checksum the
        /// release lists and carries that version; otherwise throws and leaves no installer behind.
        /// </summary>
        public static string DownloadSetup(string assets, Version version, string folder, Version client, Action<int> progress)
        {
            string name = SetupName(version);
            string expected = ChecksumFor(DownloadText(assets + ChecksumsName, client), name);
            if (expected == null)
            {
                throw new FormatException(ChecksumsName + " lists no " + name + ".");
            }

            Directory.CreateDirectory(folder);
            string partial = Path.Combine(folder, name + ".partial");
            string setup = Path.Combine(folder, name);
            File.Delete(setup);
            try
            {
                DownloadFile(assets + name, partial, client, progress);
                if (!string.Equals(FileTools.Sha256(partial, false), expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(name + " does not match " + ChecksumsName + ".");
                }

                FileVersionInfo info = FileVersionInfo.GetVersionInfo(partial);
                if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart).ToString(3) != Display(version))
                {
                    throw new InvalidDataException(name + " carries version " + info.FileVersion + ".");
                }

                File.Move(partial, setup);
                return setup;
            }
            finally
            {
                File.Delete(partial);
            }
        }

        private static HttpWebRequest Request(string url, Version client)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "NoReturnGuardian/" + Display(client);
            request.Timeout = Timeout;
            request.ReadWriteTimeout = Timeout;
            return request;
        }

        private static string DownloadText(string url, Version client)
        {
            using (var response = (HttpWebResponse)Request(url, client).GetResponse())
            using (Stream body = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                Copy(body, buffer, MaxChecksumsBytes, response.ContentLength, null);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static void DownloadFile(string url, string path, Version client, Action<int> progress)
        {
            using (var response = (HttpWebResponse)Request(url, client).GetResponse())
            using (Stream body = response.GetResponseStream())
            using (FileStream file = File.Create(path))
            {
                Copy(body, file, MaxSetupBytes, response.ContentLength, progress);
            }
        }

        private static void Copy(Stream source, Stream target, long limit, long length, Action<int> progress)
        {
            if (length > limit)
            {
                throw new InvalidDataException("The download is " + length + " bytes, more than " + limit + ".");
            }

            var buffer = new byte[81920];
            long done = 0;
            int reported = -1;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                done += read;
                if (done > limit)
                {
                    throw new InvalidDataException("The download grew past " + limit + " bytes.");
                }

                target.Write(buffer, 0, read);
                int percent = length > 0 ? (int)(done * 100 / length) : 0;
                if (progress != null && percent != reported)
                {
                    reported = percent;
                    progress(percent);
                }
            }

            if (length >= 0 && done != length)
            {
                throw new IOException("The download ended after " + done + " of " + length + " bytes.");
            }
        }
    }
}
