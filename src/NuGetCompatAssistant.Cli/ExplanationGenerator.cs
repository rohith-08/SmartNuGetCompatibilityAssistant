namespace NuGetCompatAssistant.Cli;

/// <summary>
/// Generates plain-English explanations for compatibility resolution results,
/// with colour-coded terminal output.
/// </summary>
public static class ExplanationGenerator
{
    /// <summary>
    /// Prints a formatted recommendation block to the console.
    /// </summary>
    public static void PrintRecommendation(CompatibilityResult result)
    {
        Console.WriteLine();

        if (string.IsNullOrEmpty(result.RecommendedVersion))
        {
            PrintColored("✗ No compatible version found", ConsoleColor.Red);
            Console.WriteLine();
            PrintColored("Reason:", ConsoleColor.Yellow);
            Console.WriteLine($"  {result.Reason}");
            Console.WriteLine();
            return;
        }

        // Header
        PrintColored($"✔ Recommending {result.PackageId} {result.RecommendedVersion}", ConsoleColor.Green);
        Console.WriteLine();

        // Reason paragraph
        PrintColored("Reason:", ConsoleColor.Cyan);
        Console.WriteLine($"  {BuildReason(result)}");
        Console.WriteLine();

        // Compatibility status badge
        if (result.IsLatestStableCompatible)
        {
            PrintColored("  ✔ Latest stable version is compatible — no downgrade needed.", ConsoleColor.Green);
        }
        else
        {
            PrintColored(
                $"  ⚠ Latest stable version ({result.LatestStableVersion}) is NOT compatible with {result.ProjectTfm}.",
                ConsoleColor.Yellow);

            if (result.IncompatibleLatestTfms.Count > 0)
            {
                var tfmList = string.Join(", ", result.IncompatibleLatestTfms.Select(t => $"'{t}'"));
                Console.WriteLine($"    It only supports: {tfmList}");
            }

            PrintColored(
                $"  ✔ Downgraded to {result.RecommendedVersion} which supports {result.ProjectTfm}.",
                ConsoleColor.Green);
        }

        // Show if there are pre-release versions beyond the stable recommendation
        if (!string.Equals(result.LatestOverallVersion, result.LatestStableVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            PrintColored(
                $"  ℹ  Pre-release versions exist up to {result.LatestOverallVersion} (excluded by default).",
                ConsoleColor.DarkGray);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Builds the full plain-English reason string for a resolved result.
    /// </summary>
    public static string BuildReason(CompatibilityResult result)
    {
        if (string.IsNullOrEmpty(result.RecommendedVersion))
            return result.Reason;

        if (result.IsLatestStableCompatible)
        {
            return
                $"Your project targets {result.ProjectTfm}. " +
                $"Version {result.RecommendedVersion} is the latest stable release " +
                $"and is fully compatible with {result.ProjectTfm} — no downgrade needed.";
        }
        else
        {
            var tfmList = result.IncompatibleLatestTfms.Count > 0
                ? string.Join(" and ", result.IncompatibleLatestTfms.Select(t => $"'{t}'"))
                : "newer target frameworks";

            return
                $"Your project targets {result.ProjectTfm}. " +
                $"The latest stable version ({result.LatestStableVersion}) only supports {tfmList}, " +
                $"so it would fail to install or cause runtime issues. " +
                $"Version {result.RecommendedVersion} is the newest stable release that explicitly " +
                $"supports {result.ProjectTfm}.";
        }
    }

    /// <summary>
    /// Prints a compatibility report row for use in --report mode.
    /// Each row ends with a newline.
    /// </summary>
    public static void PrintReportRow(
        string packageId,
        string installedVersion,
        CompatibilityResult result)
    {
        // Determine row status:
        // GREEN  = latest stable is compatible AND installed matches latest stable
        // YELLOW = latest stable is incompatible (needs downgrade from what dotnet add would pick)
        // CYAN   = latest stable is compatible but installed is behind (upgrade available)

        bool newerCompatibleExists = result.IsLatestStableCompatible &&
            !string.Equals(installedVersion, result.LatestStableVersion,
                StringComparison.OrdinalIgnoreCase);

        if (!result.IsLatestStableCompatible)
        {
            PrintColored("⚠ WARN ", ConsoleColor.Yellow, newline: false);
            Console.Write($"{packageId,-45} ");
            Console.Write($"installed: {installedVersion,-15} ");
            Console.Write($"latest stable: {result.LatestStableVersion,-15}");
            PrintColored($" → recommend: {result.RecommendedVersion}", ConsoleColor.Yellow);
        }
        else if (newerCompatibleExists)
        {
            PrintColored("↑ INFO ", ConsoleColor.Cyan, newline: false);
            Console.Write($"{packageId,-45} ");
            Console.Write($"installed: {installedVersion,-15} ");
            Console.Write($"latest stable: {result.LatestStableVersion,-15}");
            PrintColored(" [upgrade available]", ConsoleColor.Cyan);
        }
        else
        {
            PrintColored("✔ OK   ", ConsoleColor.Green, newline: false);
            Console.Write($"{packageId,-45} ");
            Console.Write($"installed: {installedVersion,-15} ");
            Console.Write($"latest stable: {result.LatestStableVersion,-15}");
            PrintColored(" [up to date]", ConsoleColor.Green);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void PrintColored(string text, ConsoleColor color, bool newline = true)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        if (newline)
            Console.WriteLine(text);
        else
            Console.Write(text);
        Console.ForegroundColor = prev;
    }
}
