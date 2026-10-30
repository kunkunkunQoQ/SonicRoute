using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute.Updates
{
    internal sealed class UpdateTransport : IDisposable
    {
        private readonly HttpClient _http;
        internal UpdateTransport(HttpMessageHandler? handler = null)
        {
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SonicRoute-Updater/1");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        internal static bool TrustedAssetUrl(string url, string tag, string name) => string.Equals(url,
            UpdateProtocol.Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name), StringComparison.Ordinal);

        private static bool TrustedRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com"
             || uri.Host == "github-releases.githubusercontent.com");

        private async Task<HttpResponseMessage> OpenAsync(string url, CancellationToken token)
        {
            var uri = new Uri(url);
            for (int i = 0; i < 6; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location == null) throw new HttpRequestException("Missing redirect.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!TrustedRedirect(uri)) throw new HttpRequestException("Untrusted update redirect.");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var code = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException("GitHub HTTP " + (int)code);
                }
                return response;
            }
            throw new HttpRequestException("Too many update redirects.");
        }

        private async Task<byte[]> ReadAsync(string url, int maxBytes, CancellationToken token)
        {
            using var response = await OpenAsync(url, token).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var cancel = token.Register(() => stream.Dispose());
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > maxBytes) throw new InvalidDataException("Update metadata too large.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }

        internal static UpdateRelease ParseRelease(byte[] json, out Dictionary<string, ReleaseAsset> assets)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("Not a stable release.");
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (UpdateProtocol.ParseVersion(tag) == null) throw new InvalidDataException("Not a release version.");
            var result = new UpdateRelease { Tag = tag, Notes = root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "",
                Url = UpdateProtocol.Repository + "/releases/tag/" + Uri.EscapeDataString(tag) };
            assets = new Dictionary<string, ReleaseAsset>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("assets").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString() ?? "";
                string url = item.GetProperty("browser_download_url").GetString() ?? "";
                if (assets.ContainsKey(name)) throw new InvalidDataException("Duplicate release asset.");
                if (!TrustedAssetUrl(url, tag, name)) throw new InvalidDataException("Untrusted release asset.");
                string digest = item.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                assets.Add(name, new ReleaseAsset { Name = name, Url = url, Size = item.GetProperty("size").GetInt64(), Digest = digest });
            }
            return result;
        }

        private async Task<byte[]> MetadataAsync(ReleaseAsset asset, int limit, CancellationToken token)
        {
            if (asset.Size <= 0 || asset.Size > limit || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal)
                || !UpdateProtocol.IsHash(asset.Digest.Substring(7))) throw new InvalidDataException("Missing GitHub asset digest.");
            byte[] bytes = await ReadAsync(asset.Url, limit, token).ConfigureAwait(false);
            using var sha = System.Security.Cryptography.SHA256.Create();
            if (bytes.Length != asset.Size || !UpdateProtocol.Hex(sha.ComputeHash(bytes)).Equals(asset.Digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Metadata checksum mismatch.");
            return bytes;
        }

        internal async Task<UpdateRelease> CheckAsync(string edition, string architecture, string current, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            token = timeout.Token;
            var release = ParseRelease(await ReadAsync(UpdateProtocol.LatestApi, 2 * 1024 * 1024, token).ConfigureAwait(false), out var assets);
            if (UpdateProtocol.ParseVersion(release.Tag) <= UpdateProtocol.ParseVersion(current)) return release;
            // Releases made before the update protocol remain readable, but cannot be installed automatically.
            if (!assets.TryGetValue(UpdateProtocol.ManifestName, out var manifestAsset)
                || !assets.TryGetValue("SHA256SUMS.txt", out var sumsAsset)) return release;
            byte[] manifestBytes = await MetadataAsync(manifestAsset, 1024 * 1024, token).ConfigureAwait(false);
            var sums = UpdateProtocol.ReadChecksums(Encoding.UTF8.GetString(await MetadataAsync(sumsAsset, 65536, token).ConfigureAwait(false)));
            if (!sums.TryGetValue(UpdateProtocol.ManifestName, out var manifestHash) || !manifestHash.Equals(manifestAsset.Digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest absent from checksums.");
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, UpdateProtocol.Json) ?? throw new InvalidDataException("Empty manifest.");
            if (manifest.SchemaVersion != 1 || (manifest.ProtocolVersion != 1 && manifest.ProtocolVersion != 2)
                || UpdateProtocol.ParseVersion(manifest.Version) != UpdateProtocol.ParseVersion(release.Tag))
                throw new InvalidDataException("Unsupported update manifest.");
            // Never reinterpret unpublished, ambiguous dual-runtime protocol-1 manifests.
            // Protocol 2 makes runtime mandatory and makes old clients fail closed.
            if (manifest.Packages == null || manifest.Packages.Any(p => p == null))
                throw new InvalidDataException("Invalid update packages.");
            if (manifest.ProtocolVersion == 1 && manifest.Packages.Any(p => !string.IsNullOrEmpty(p.Runtime) || p.Edition == "Lite-NET8")) return release;
            if (manifest.ProtocolVersion == 2 && manifest.Packages.Any(p => string.IsNullOrEmpty(p.Runtime)))
                throw new InvalidDataException("Missing update runtime.");
            var matching = manifest.Packages.Where(p => p.Edition == edition && p.Architecture == architecture
                && UpdateProtocol.PackageRuntime(p) == UpdateProtocol.CurrentRuntime).ToList();
            if (matching.Count == 0) return release;
            if (matching.Count != 1) throw new InvalidDataException("Ambiguous update package.");
            var package = matching[0];
            UpdateProtocol.ValidatePackage(package, edition, architecture);
            if (!assets.TryGetValue(package.Asset, out var asset)) return release;
            if (asset.Size != package.Size || !sums.TryGetValue(package.Asset, out var sum) || !sum.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)
                || !asset.Digest.Equals("sha256:" + package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Inconsistent update metadata.");
            release.Package = package; release.Asset = asset;
            return release;
        }

        internal async Task DownloadAsync(UpdateRelease release, string destination, Action<long, double> progress, CancellationToken token, Action? verifying = null)
        {
            var asset = release.Asset ?? throw new InvalidOperationException("No matching update asset.");
            var package = release.Package ?? throw new InvalidOperationException("No matching update package.");
            if (!TrustedAssetUrl(asset.Url, release.Tag, asset.Name)) throw new InvalidDataException("Untrusted update source.");
            UpdateProtocol.RejectReparsePoints(destination);
            UpdateProtocol.RejectReparsePoints(destination + ".partial");
            if (File.Exists(destination))
            {
                try { verifying?.Invoke(); UpdateProtocol.Verify(destination, package.Sha256, package.Size, token); return; }
                catch (InvalidDataException) { File.Delete(destination); }
            }
            string partial = destination + ".partial";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await OpenAsync(asset.Url, idle.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength is long size && size != package.Size) throw new InvalidDataException("Wrong download size.");
                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var cancel = idle.Token.Register(() => input.Dispose());
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var watch = Stopwatch.StartNew();
                    long total = 0, lastReport = -250;
                    var buffer = new byte[65536];
                    int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > package.Size) throw new InvalidDataException("Download exceeded expected size.");
                        await output.WriteAsync(buffer, 0, count, idle.Token).ConfigureAwait(false);
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        if (watch.ElapsedMilliseconds - lastReport >= 250)
                        { progress(total, total / Math.Max(0.001, watch.Elapsed.TotalSeconds)); lastReport = watch.ElapsedMilliseconds; }
                    }
                    output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                verifying?.Invoke();
                UpdateProtocol.Verify(partial, package.Sha256, package.Size, token);
                token.ThrowIfCancellationRequested();
                File.Move(partial, destination);
                progress(package.Size, 0);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        public void Dispose() => _http.Dispose();
    }
}
