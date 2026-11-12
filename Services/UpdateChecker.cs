using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Help → Check for updates: asks the project's repository for its latest
    /// published release and compares it with the running version.
    ///
    /// <para>It only LOOKS, and only when the user asks: nothing is checked in the
    /// background, nothing is downloaded or installed. A newer version is offered
    /// as a link to its release page, which the user opens (or not) themselves.</para>
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>owner/name of the repository whose releases are the updates.</summary>
        public const string Repository = "theworfer/SmartUnrealLocalization";

        public static string ReleasesPage => $"https://github.com/{Repository}/releases";
        private static string LatestReleaseApi => $"https://api.github.com/repos/{Repository}/releases/latest";

        public enum Outcome { UpToDate, NewVersion, NoRelease, Failed }

        /// <param name="Latest">The latest published version ("1.2.0"), when known.</param>
        /// <param name="Url">The page to open for <see cref="Outcome.NewVersion"/>.</param>
        /// <param name="Error">What went wrong, for <see cref="Outcome.Failed"/>.</param>
        public sealed record Result(Outcome Outcome, string Latest = "", string Url = "", string Error = "");

        /// <summary>Test seam: answers the request instead of the network.</summary>
        internal static HttpMessageHandler? HandlerOverride;

        /// <summary>The running version (the csproj's &lt;Version&gt;), three parts.</summary>
        public static Version CurrentVersion()
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version;
            return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        public static async Task<Result> CheckAsync(Version current, CancellationToken ct = default)
        {
            try
            {
                using var http = HandlerOverride is null ? new HttpClient() : new HttpClient(HandlerOverride, disposeHandler: false);
                http.Timeout = TimeSpan.FromSeconds(15);
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                // The API refuses requests without a User-Agent.
                request.Headers.UserAgent.ParseAdd($"SmartUnrealLocalization/{current}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await http.SendAsync(request, ct);
                string body = await response.Content.ReadAsStringAsync(ct);
                return Interpret(response.StatusCode, body, current);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new Result(Outcome.Failed, Error: "timeout");
            }
            catch (Exception ex)
            {
                return new Result(Outcome.Failed, Error: ex.Message);
            }
        }

        /// <summary>Turn the API's answer into an outcome. Pure, so it is tested without a network.</summary>
        internal static Result Interpret(HttpStatusCode status, string body, Version current)
        {
            // 404 = the repository has no published release (or is not public).
            if (status == HttpStatusCode.NotFound) return new Result(Outcome.NoRelease);
            if ((int)status < 200 || (int)status > 299) return new Result(Outcome.Failed, Error: $"HTTP {(int)status}");

            try
            {
                var json = JObject.Parse(body);
                string tag = (string?)json["tag_name"] ?? "";
                if (!TryParseVersion(tag, out var latest))
                    return new Result(Outcome.Failed, Error: $"unreadable version '{tag}'");

                // Only ever open a page of this repository, whatever the answer says.
                string url = (string?)json["html_url"] ?? "";
                if (!url.StartsWith($"https://github.com/{Repository}/", StringComparison.OrdinalIgnoreCase))
                    url = ReleasesPage;

                string shown = latest.ToString(3);
                return latest > current
                    ? new Result(Outcome.NewVersion, shown, url)
                    : new Result(Outcome.UpToDate, shown);
            }
            catch (Exception ex)
            {
                return new Result(Outcome.Failed, Error: ex.Message);
            }
        }

        /// <summary>"v1.2", "1.2.0", "v1.2.0-beta" → 1.2.0 (a pre-release suffix is ignored).</summary>
        internal static bool TryParseVersion(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string s = tag.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (!s.Contains('.')) s += ".0";
            if (!Version.TryParse(s, out var parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }
    }
}
