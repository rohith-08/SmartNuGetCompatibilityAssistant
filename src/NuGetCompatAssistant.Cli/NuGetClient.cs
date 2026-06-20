using System.Net;
using System.Text.Json;

namespace NuGetCompatAssistant.Cli;

/// <summary>
/// Metadata for one published version of a NuGet package.
/// </summary>
public record PackageVersionInfo(
    string Version,
    List<DependencyGroup> DependencyGroups,
    bool IsListed = true
);

/// <summary>
/// A single dependency group within a package's catalog entry,
/// representing a target framework (TFM) the package supports.
/// </summary>
public record DependencyGroup(
    string? TargetFramework,
    List<PackageDependency> Dependencies
);

/// <summary>
/// A single dependency declared within a dependency group.
/// </summary>
public record PackageDependency(string Id, string VersionRange);

/// <summary>
/// Wraps HttpClient calls to the public NuGet v3 API.
/// </summary>
public class NuGetClient : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    // Well-known NuGet v3 API endpoint URLs (used as fallback if service index fails)
    private const string ServiceIndexUrl = "https://api.nuget.org/v3/index.json";
    private const string FallbackRegistrationsBase = "https://api.nuget.org/v3/registration5-semver1/";

    private string? _registrationsBaseUrl;

    public NuGetClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "nuget-compat-assistant/1.0 (https://github.com/nuget-compat-assistant)");
    }

    // -------------------------------------------------------------------------
    // Service index discovery
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initialises the registrations base URL from the NuGet service index.
    /// Falls back to the well-known URL if the index can't be reached.
    /// </summary>
    public async Task InitialiseServiceIndexAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(ServiceIndexUrl);
            using var doc = JsonDocument.Parse(json);

            var resources = doc.RootElement.GetProperty("resources");
            foreach (var resource in resources.EnumerateArray())
            {
                var type = resource.TryGetProperty("@type", out var t) ? t.GetString() : null;
                if (type is "RegistrationsBaseUrl/3.6.0" or "RegistrationsBaseUrl/3.0.0-beta" or "RegistrationsBaseUrl")
                {
                    _registrationsBaseUrl = resource.GetProperty("@id").GetString()?.TrimEnd('/') + "/";
                    break;
                }
            }
        }
        catch
        {
            // Fall back silently — the caller will use the hardcoded URLs
        }

        _registrationsBaseUrl ??= FallbackRegistrationsBase;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------


    /// <summary>
    /// Returns all published (listed) versions of a package along with their
    /// dependency group TFMs. Uses the registration index endpoint, and follows
    /// external page URLs when the index uses paginated references (common for large packages).
    /// </summary>
    public async Task<List<PackageVersionInfo>> GetAllVersionsAsync(string packageId)
    {
        if (_registrationsBaseUrl is null)
            await InitialiseServiceIndexAsync();

        var lowerId = packageId.ToLowerInvariant();
        var url = $"{_registrationsBaseUrl}{lowerId}/index.json";

        var indexJson = await FetchJsonAsync(url, packageId);
        return await ParseRegistrationIndexAsync(indexJson, packageId);
    }

    /// <summary>
    /// Returns the parsed catalog entry metadata for a specific version of a package.
    /// </summary>
    public async Task<PackageVersionInfo?> GetCatalogEntryAsync(string packageId, string version)
    {
        var all = await GetAllVersionsAsync(packageId);
        return all.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Parsing helpers
    // -------------------------------------------------------------------------

    private async Task<string> FetchJsonAsync(string url, string packageId)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url);
        }
        catch (TaskCanceledException)
        {
            throw new NuGetClientException($"Request timed out while fetching data for '{packageId}'. Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            throw new NuGetClientException($"Network error fetching data for '{packageId}': {ex.Message}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new NuGetClientException($"Package '{packageId}' was not found on nuget.org. Check the package ID and try again.");

        if (!response.IsSuccessStatusCode)
            throw new NuGetClientException($"NuGet API returned HTTP {(int)response.StatusCode} for package '{packageId}'.");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Parses the registration index JSON. For large packages, pages are referenced by URL
    /// rather than being inlined — this method fetches those external pages as needed.
    /// </summary>
    private async Task<List<PackageVersionInfo>> ParseRegistrationIndexAsync(string indexJson, string packageId)
    {
        var results = new List<PackageVersionInfo>();

        using var doc = JsonDocument.Parse(indexJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("items", out var pages))
            return results;

        foreach (var page in pages.EnumerateArray())
        {
            // Check whether this page has inline items
            if (page.TryGetProperty("items", out var items))
            {
                // Inline items — parse them directly
                ParseRegistrationPageItems(items, results);
            }
            else
            {
                // External page reference — the "@id" property holds the URL to fetch
                if (!page.TryGetProperty("@id", out var pageIdProp))
                    continue;

                var pageUrl = pageIdProp.GetString();
                if (string.IsNullOrWhiteSpace(pageUrl))
                    continue;

                try
                {
                    var pageJson = await FetchJsonAsync(pageUrl, packageId);
                    using var pageDoc = JsonDocument.Parse(pageJson);
                    if (pageDoc.RootElement.TryGetProperty("items", out var externalItems))
                        ParseRegistrationPageItems(externalItems, results);
                }
                catch (NuGetClientException)
                {
                    // Skip unloadable pages rather than aborting entirely
                }
            }
        }

        return results;
    }

    private static void ParseRegistrationPageItems(JsonElement items, List<PackageVersionInfo> results)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("catalogEntry", out var entry))
                continue;

            var versionStr = entry.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(versionStr))
                continue;

            // Skip unlisted packages
            var listed = !entry.TryGetProperty("listed", out var listedProp) || listedProp.GetBoolean();
            if (!listed)
                continue;

            var depGroups = new List<DependencyGroup>();

            if (entry.TryGetProperty("dependencyGroups", out var dgArray))
            {
                foreach (var dg in dgArray.EnumerateArray())
                {
                    var tfm = dg.TryGetProperty("targetFramework", out var tfmProp) ? tfmProp.GetString() : null;
                    var deps = new List<PackageDependency>();

                    if (dg.TryGetProperty("dependencies", out var depsArray))
                    {
                        foreach (var dep in depsArray.EnumerateArray())
                        {
                            var depId = dep.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            var depRange = dep.TryGetProperty("range", out var rangeProp) ? rangeProp.GetString() ?? "" : "";
                            deps.Add(new PackageDependency(depId, depRange));
                        }
                    }

                    depGroups.Add(new DependencyGroup(tfm, deps));
                }
            }

            results.Add(new PackageVersionInfo(versionStr!, depGroups, IsListed: true));
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _http.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Exception thrown by <see cref="NuGetClient"/> for user-facing error conditions.
/// </summary>
public class NuGetClientException : Exception
{
    public NuGetClientException(string message) : base(message) { }
    public NuGetClientException(string message, Exception innerException) : base(message, innerException) { }
}

