using System.Xml.Linq;

namespace NuGetCompatAssistant.Cli;

/// <summary>
/// Reads a .csproj file and extracts its target framework moniker(s).
/// </summary>
public class ProjectReader
{
    /// <summary>
    /// Finds the first .csproj file in the given directory (or current directory if null).
    /// </summary>
    public static string FindCsprojInDirectory(string? directory = null)
    {
        var dir = directory ?? Directory.GetCurrentDirectory();
        var files = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly);

        if (files.Length == 0)
            throw new ProjectReaderException($"No .csproj file found in directory: {dir}");

        if (files.Length > 1)
            Console.WriteLine($"Warning: Multiple .csproj files found. Using '{Path.GetFileName(files[0])}'. Use --project to specify one explicitly.");

        return files[0];
    }

    /// <summary>
    /// Reads the target framework moniker(s) from a .csproj file.
    /// </summary>
    /// <param name="csprojPath">Absolute or relative path to the .csproj file.</param>
    /// <returns>A non-empty list of TFMs (e.g. ["net8.0"] or ["net8.0","net9.0"]).</returns>
    /// <exception cref="ProjectReaderException">Thrown when the file is missing, unreadable, or has no TFM declared.</exception>
    public static List<string> ReadTargetFrameworks(string csprojPath)
    {
        if (!File.Exists(csprojPath))
            throw new ProjectReaderException($"Project file not found: {csprojPath}");

        XDocument doc;
        try
        {
            doc = XDocument.Load(csprojPath);
        }
        catch (Exception ex)
        {
            throw new ProjectReaderException($"Failed to parse project file '{csprojPath}': {ex.Message}", ex);
        }

        // Try <TargetFramework> (single) first
        var singleTfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(singleTfm))
            return new List<string> { singleTfm };

        // Try <TargetFrameworks> (multi-target, semicolon-separated)
        var multiTfm = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(multiTfm))
        {
            var tfms = multiTfm
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (tfms.Count > 0)
                return tfms;
        }

        throw new ProjectReaderException(
            $"No <TargetFramework> or <TargetFrameworks> element found in '{csprojPath}'. " +
            "Make sure the project file is a valid SDK-style .csproj.");
    }

    /// <summary>
    /// Reads all existing PackageReference entries from the .csproj.
    /// Returns a list of (PackageId, Version) tuples.
    /// </summary>
    public static List<(string Id, string Version)> ReadPackageReferences(string csprojPath)
    {
        if (!File.Exists(csprojPath))
            throw new ProjectReaderException($"Project file not found: {csprojPath}");

        XDocument doc;
        try
        {
            doc = XDocument.Load(csprojPath);
        }
        catch (Exception ex)
        {
            throw new ProjectReaderException($"Failed to parse project file '{csprojPath}': {ex.Message}", ex);
        }

        return doc
            .Descendants("PackageReference")
            .Select(pr => (
                Id: pr.Attribute("Include")?.Value?.Trim() ?? string.Empty,
                Version: pr.Attribute("Version")?.Value?.Trim() ?? string.Empty
            ))
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .ToList();
    }

    /// <summary>
    /// Checks if Central Package Management (CPM) is enabled for the project.
    /// CPM is active if <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    /// is defined in the project file, or if a Directory.Packages.props file is found in any ancestor directory
    /// and does not explicitly disable it.
    /// </summary>
    public static bool IsCentralPackageManagementEnabled(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var mpc = doc.Descendants("ManagePackageVersionsCentrally").FirstOrDefault()?.Value?.Trim();
            if (string.Equals(mpc, "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // Fall through to file checks
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath));
        while (dir != null)
        {
            var propsPath = Path.Combine(dir, "Directory.Packages.props");
            if (File.Exists(propsPath))
            {
                try
                {
                    var doc = XDocument.Load(propsPath);
                    var mpc = doc.Descendants("ManagePackageVersionsCentrally").FirstOrDefault()?.Value?.Trim();
                    if (string.Equals(mpc, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        // Explicitly disabled
                    }
                    else
                    {
                        return true;
                    }
                }
                catch
                {
                    return true; // Assume true if the file exists but fails to parse
                }
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        return false;
    }
}

/// <summary>
/// Exception thrown by <see cref="ProjectReader"/> for user-facing error conditions.
/// </summary>
public class ProjectReaderException : Exception
{
    public ProjectReaderException(string message) : base(message) { }
    public ProjectReaderException(string message, Exception innerException) : base(message, innerException) { }
}
