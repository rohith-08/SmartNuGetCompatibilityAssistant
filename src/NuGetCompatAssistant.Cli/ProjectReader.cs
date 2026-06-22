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
        
        // Search current directory first
        var files = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly);
        
        // If not found, search recursively
        if (files.Length == 0)
        {
            files = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories);
        }

        if (files.Length == 0)
            throw new ProjectReaderException(
                $"No .NET project (.csproj) was found in '{dir}' or any of its subdirectories.\n" +
                "Please navigate to a project or solution directory, or specify a project path explicitly using the --project <path> option.");

        if (files.Length == 1)
            return files[0];

        if (Console.IsInputRedirected)
        {
            throw new ProjectReaderException(
                $"Multiple .csproj files found in '{dir}', but the console cannot accept input.\n" +
                "Please specify the project explicitly using the --project <path> option.");
        }

        var classified = files
            .Select(f => new { Path = f, Type = ClassifyProject(f) })
            .OrderBy(item => item.Type switch
            {
                ProjectType.Application => 0,
                ProjectType.Library => 1,
                ProjectType.Cli => 2,
                ProjectType.Test => 3,
                _ => 4
            })
            .ThenBy(item => Path.GetFileName(item.Path))
            .ToList();

        var appProjects = classified.Where(c => c.Type == ProjectType.Application).ToList();
        int? recommendedIndex = null;
        if (appProjects.Count == 1)
        {
            var recommendedPath = appProjects[0].Path;
            for (int i = 0; i < classified.Count; i++)
            {
                if (classified[i].Path == recommendedPath)
                {
                    recommendedIndex = i + 1;
                    break;
                }
            }
        }

        Console.WriteLine("Detected projects:");
        Console.WriteLine();
        for (int i = 0; i < classified.Count; i++)
        {
            var item = classified[i];
            var filenameWithoutExt = Path.GetFileNameWithoutExtension(item.Path);
            var typeLabel = item.Type switch
            {
                ProjectType.Application => "Application Project",
                ProjectType.Library => "Library Project",
                ProjectType.Cli => "CLI Project",
                ProjectType.Test => "Test Project",
                _ => "Project"
            };

            int displayIndex = i + 1;
            if (recommendedIndex.HasValue && recommendedIndex.Value == displayIndex)
            {
                Console.WriteLine($"  ⭐ [{displayIndex}] {filenameWithoutExt} (Application Project - Recommended)");
            }
            else
            {
                Console.WriteLine($"    [{displayIndex}] {filenameWithoutExt} ({typeLabel})");
            }
        }
        Console.WriteLine();

        while (true)
        {
            if (recommendedIndex.HasValue)
            {
                Console.Write($"Select project [{recommendedIndex.Value}]: ");
            }
            else
            {
                Console.Write("Select project: ");
            }

            var input = Console.ReadLine();
            if (string.IsNullOrEmpty(input) && recommendedIndex.HasValue)
            {
                Console.WriteLine();
                return classified[recommendedIndex.Value - 1].Path;
            }

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= classified.Count)
            {
                Console.WriteLine();
                return classified[choice - 1].Path;
            }
            Console.WriteLine("Invalid selection. Please enter a valid number.");
        }
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

    public static ProjectType ClassifyProject(string csprojPath)
    {
        try
        {
            var content = File.ReadAllText(csprojPath);
            return ClassifyProjectContent(content, csprojPath);
        }
        catch
        {
            return ProjectType.Library;
        }
    }

    public static ProjectType ClassifyProjectContent(string xmlContent, string csprojPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlContent);
        }
        catch
        {
            return ProjectType.Library;
        }

        // 1. Test Project Check
        var isPackableElement = doc.Descendants("IsPackable").FirstOrDefault();
        bool hasIsPackableFalse = isPackableElement != null && 
                                  string.Equals(isPackableElement.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);

        var packageRefs = doc.Descendants("PackageReference")
            .Select(pr => pr.Attribute("Include")?.Value?.Trim() ?? string.Empty)
            .Where(val => !string.IsNullOrEmpty(val))
            .ToList();

        bool hasTestPackages = packageRefs.Any(pr => 
            string.Equals(pr, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pr, "xunit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pr, "NUnit3TestAdapter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pr, "MSTest.TestAdapter", StringComparison.OrdinalIgnoreCase));

        var filename = Path.GetFileNameWithoutExtension(csprojPath);
        bool hasTestName = filename.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                           filename.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
                           filename.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
                           filename.EndsWith("Test", StringComparison.OrdinalIgnoreCase);

        if (hasIsPackableFalse || hasTestPackages || hasTestName)
        {
            return ProjectType.Test;
        }

        // 2. CLI/Tool Project Check
        var packAsToolElement = doc.Descendants("PackAsTool").FirstOrDefault();
        bool isPackAsTool = packAsToolElement != null && 
                             string.Equals(packAsToolElement.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        if (isPackAsTool)
        {
            return ProjectType.Cli;
        }

        // 3. Application Project Check
        var outputTypeElement = doc.Descendants("OutputType").FirstOrDefault();
        bool isExe = outputTypeElement != null && 
                     string.Equals(outputTypeElement.Value.Trim(), "Exe", StringComparison.OrdinalIgnoreCase);

        var sdkAttr = doc.Root?.Attribute("Sdk")?.Value?.Trim() ?? string.Empty;
        bool hasWebSdk = string.Equals(sdkAttr, "Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        bool hasWorkerSdk = string.Equals(sdkAttr, "Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);
        bool hasRazorSdk = string.Equals(sdkAttr, "Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase);
        bool hasMauiSdk = string.Equals(sdkAttr, "Microsoft.NET.Sdk.Maui", StringComparison.OrdinalIgnoreCase);

        var useMauiElement = doc.Descendants("UseMaui").FirstOrDefault();
        bool useMaui = useMauiElement != null && 
                       string.Equals(useMauiElement.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        if (isExe || hasWebSdk || hasWorkerSdk || hasRazorSdk || hasMauiSdk || 
            (string.Equals(sdkAttr, "Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) && useMaui))
        {
            return ProjectType.Application;
        }

        return ProjectType.Library;
    }
}

public enum ProjectType
{
    Application,
    Library,
    Cli,
    Test
}


/// <summary>
/// Exception thrown by <see cref="ProjectReader"/> for user-facing error conditions.
/// </summary>
public class ProjectReaderException : Exception
{
    public ProjectReaderException(string message) : base(message) { }
    public ProjectReaderException(string message, Exception innerException) : base(message, innerException) { }
}
