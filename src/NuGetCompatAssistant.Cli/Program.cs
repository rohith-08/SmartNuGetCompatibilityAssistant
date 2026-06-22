using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.CommandLine.Help;
using NuGetCompatAssistant.Cli;

// ─────────────────────────────────────────────────────────────────────────────
// Root command
// ─────────────────────────────────────────────────────────────────────────────

var rootCommand = new RootCommand(
    "Smart NuGet Compatibility Assistant — finds the best compatible package version for your project's TFM.");

// ─────────────────────────────────────────────────────────────────────────────
// Shared options
// ─────────────────────────────────────────────────────────────────────────────

var projectOption = new Option<string?>(
    aliases: ["--project", "-p"],
    description: "Path to .csproj file. If omitted, auto-detects in the current directory.");

var yesOption = new Option<bool>(
    aliases: ["--yes", "-y"],
    description: "Skip confirmation prompt and install immediately.");

var dryRunOption = new Option<bool>(
    aliases: ["--dry-run"],
    description: "Show the recommendation without installing anything.");

// ─────────────────────────────────────────────────────────────────────────────
// install <PackageId> [options]
// ─────────────────────────────────────────────────────────────────────────────

var packageIdArg = new Argument<string[]>(
    "PackageId",
    "One or more NuGet package IDs to install (e.g. Microsoft.EntityFrameworkCore Serilog AutoMapper).")
{
    Arity = ArgumentArity.OneOrMore
};

var versionOption = new Option<string?>(
    aliases: ["--version", "-v"],
    description: "Check compatibility of a specific version instead of auto-resolving the best one.");

var installCommand = new Command(
    "install",
    "Find the best compatible version of a NuGet package and optionally install it.")
{
    packageIdArg,
    projectOption,
    versionOption,
    yesOption,
    dryRunOption,
};

installCommand.SetHandler(
    async (string[] packageIds, string? projectPath, string? version, bool yes, bool dryRun) =>
    {
        int code;
        if (packageIds.Length == 1)
        {
            // Single-package path — exact same behaviour as v1.0
            code = await RunInstallAsync(packageIds[0], projectPath, version, yes, dryRun);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(version))
            {
                PrintError("The --version option cannot be used when installing multiple packages.");
                Environment.Exit(1);
                return;
            }
            code = await RunBatchInstallAsync(packageIds, projectPath, yes, dryRun);
        }
        Environment.Exit(code);
    },
    packageIdArg, projectOption, versionOption, yesOption, dryRunOption);

rootCommand.AddCommand(installCommand);

// ─────────────────────────────────────────────────────────────────────────────
// --report  (root-level flag, no sub-command)
// ─────────────────────────────────────────────────────────────────────────────

var reportOption = new Option<bool>(
    aliases: ["--report"],
    description: "Print a compatibility report for all packages already referenced in the project.");

rootCommand.AddOption(reportOption);
rootCommand.AddOption(projectOption);

rootCommand.SetHandler(async (bool report, string? projectPath) =>
{
    if (report)
    {
        int code = await RunReportAsync(projectPath);
        Environment.Exit(code);
    }
    else
    {
        await rootCommand.InvokeAsync("--help");
    }
},
reportOption, projectOption);

bool isVersionQuery = args.Contains("--version");
if (!isVersionQuery)
{
    PrintBanner();
}

var parser = new CommandLineBuilder(rootCommand)
    .UseDefaults()
    .UseHelpBuilder(context => new CustomHelpBuilder())
    .Build();

return await parser.InvokeAsync(args);

// ─────────────────────────────────────────────────────────────────────────────
// Command handlers
// ─────────────────────────────────────────────────────────────────────────────

static async Task<int> RunInstallAsync(
    string packageId,
    string? projectPath,
    string? specificVersion,
    bool yes,
    bool dryRun)
{
    // 1. Resolve project file
    string csprojPath;
    try
    {
        csprojPath = projectPath is not null
            ? Path.GetFullPath(projectPath)
            : ProjectReader.FindCsprojInDirectory();
    }
    catch (ProjectReaderException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    if (ProjectReader.IsCentralPackageManagementEnabled(csprojPath))
    {
        PrintError("Central Package Management detected. This version does not yet support Directory.Packages.props.");
        return 1;
    }

    // 2. Read TFMs
    List<string> tfms;
    try
    {
        tfms = ProjectReader.ReadTargetFrameworks(csprojPath);
    }
    catch (ProjectReaderException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    Console.WriteLine($"Project : {Path.GetFileName(csprojPath)}");
    Console.WriteLine($"TFM(s)  : {string.Join(", ", tfms)}");
    Console.WriteLine();

    using var nugetClient = new NuGetClient();
    var resolver = new CompatibilityResolver();

    // For multi-targeting projects, resolve against each TFM and pick the version
    // that is compatible with ALL of them (intersection). If no version satisfies all,
    // fall back to the primary (first) TFM and note the limitation.
    string projectTfm;
    if (tfms.Count > 1)
    {
        Console.WriteLine($"Note: Multi-targeting detected ({string.Join(", ", tfms)}).");
        Console.WriteLine($"      Resolving for primary TFM: {tfms[0]}");
        Console.WriteLine($"      Tip: Verify the chosen version also supports {string.Join(", ", tfms.Skip(1))}.");
        Console.WriteLine();
        projectTfm = tfms[0];
    }
    else
    {
        projectTfm = tfms[0];
    }

    // 3. If a specific version was requested, just check that one
    if (!string.IsNullOrWhiteSpace(specificVersion))
    {
        return await CheckSpecificVersionAsync(
            nugetClient, resolver, packageId, specificVersion, projectTfm, csprojPath, yes, dryRun);
    }

    // 4. Fetch all versions from NuGet
    Console.WriteLine($"Querying NuGet for '{packageId}' versions…");
    List<PackageVersionInfo> allVersions;
    try
    {
        allVersions = await nugetClient.GetAllVersionsAsync(packageId);
    }
    catch (NuGetClientException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    Console.WriteLine($"Found {allVersions.Count} listed versions.");

    // 5. Resolve best version
    CompatibilityResult result;
    try
    {
        result = resolver.Resolve(packageId, projectTfm, allVersions);
    }
    catch (CompatibilityResolverException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    // 6. Print explanation
    ExplanationGenerator.PrintRecommendation(result);

    if (string.IsNullOrEmpty(result.RecommendedVersion))
        return 1; // No compatible version

    if (dryRun)
    {
        PrintColored("Dry-run mode — nothing was installed.", ConsoleColor.Cyan);
        Console.WriteLine();
        return 0;
    }

    // 7. Install
    var runner = new InstallRunner();
    bool success = await runner.RunAsync(
        packageId, result.RecommendedVersion, csprojPath, skipConfirmation: yes);

    return success ? 0 : 1;
}

static async Task<int> CheckSpecificVersionAsync(
    NuGetClient nugetClient,
    CompatibilityResolver resolver,
    string packageId,
    string version,
    string projectTfm,
    string csprojPath,
    bool yes,
    bool dryRun)
{
    Console.WriteLine($"Checking compatibility of {packageId} {version} for {projectTfm}…");
    Console.WriteLine();

    PackageVersionInfo? entry;
    try
    {
        entry = await nugetClient.GetCatalogEntryAsync(packageId, version);
    }
    catch (NuGetClientException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    if (entry is null)
    {
        PrintError($"Version '{version}' of '{packageId}' was not found on nuget.org.");
        return 1;
    }

    var isCompat = resolver.IsCompatible(entry, projectTfm);
    var tfmList = entry.DependencyGroups.Select(dg => dg.TargetFramework ?? "(any)").ToList();

    if (isCompat)
    {
        PrintColored($"✔ {packageId} {version} IS compatible with {projectTfm}.", ConsoleColor.Green);
        Console.WriteLine();

        if (dryRun)
        {
            PrintColored("Dry-run mode — nothing was installed.", ConsoleColor.Cyan);
            Console.WriteLine();
            return 0;
        }

        var runner = new InstallRunner();
        bool success = await runner.RunAsync(packageId, version, csprojPath, skipConfirmation: yes);
        return success ? 0 : 1;
    }
    else
    {
        PrintColored($"✗ {packageId} {version} is NOT compatible with {projectTfm}.", ConsoleColor.Red);
        Console.WriteLine();

        if (tfmList.Count > 0)
            Console.WriteLine($"  This version declares dependency groups for: " +
                $"{string.Join(", ", tfmList.Select(t => $"'{t}'"))}");
        else
            Console.WriteLine("  This version has no dependency groups declared.");

        Console.WriteLine($"  None of these are compatible with '{projectTfm}'.");
        Console.WriteLine();
        Console.WriteLine("Tip: Run without --version to let the tool find the best compatible version automatically.");
        return 1;
    }
}

static async Task<int> RunReportAsync(string? projectPath)
{
    string csprojPath;
    try
    {
        csprojPath = projectPath is not null
            ? Path.GetFullPath(projectPath)
            : ProjectReader.FindCsprojInDirectory();
    }
    catch (ProjectReaderException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    if (ProjectReader.IsCentralPackageManagementEnabled(csprojPath))
    {
        PrintError("Central Package Management detected. This version does not yet support Directory.Packages.props.");
        return 1;
    }

    List<string> tfms;
    List<(string Id, string Version)> packages;
    try
    {
        tfms = ProjectReader.ReadTargetFrameworks(csprojPath);
        packages = ProjectReader.ReadPackageReferences(csprojPath);
    }
    catch (ProjectReaderException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    Console.WriteLine($"Project : {Path.GetFileName(csprojPath)}");
    Console.WriteLine($"TFM(s)  : {string.Join(", ", tfms)}");
    Console.WriteLine();

    if (packages.Count == 0)
    {
        Console.WriteLine("No PackageReference entries found in this project.");
        return 0;
    }

    Console.WriteLine($"Analysing {packages.Count} package(s)…");
    Console.WriteLine();
    Console.WriteLine(new string('─', 110));

    var projectTfm = tfms[0];
    using var nugetClient = new NuGetClient();
    var resolver = new CompatibilityResolver();

    int incompatibleCount = 0;
    int upgradeAvailableCount = 0;
    int errorCount = 0;

    foreach (var (id, installedVer) in packages)
    {
        List<PackageVersionInfo> allVersions;
        try
        {
            allVersions = await nugetClient.GetAllVersionsAsync(id);
        }
        catch (NuGetClientException ex)
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.Write("✗ ERROR");
            Console.ForegroundColor = prevColor;
            Console.WriteLine($" {id,-45} (could not fetch: {ex.Message})");
            errorCount++;
            continue;
        }

        CompatibilityResult result;
        try
        {
            result = resolver.Resolve(id, projectTfm, allVersions);
        }
        catch (CompatibilityResolverException ex)
        {
            Console.WriteLine($"? SKIP  {id,-45} ({ex.Message})");
            errorCount++;
            continue;
        }

        ExplanationGenerator.PrintReportRow(id, installedVer, result);

        if (!result.IsLatestStableCompatible)
            incompatibleCount++;
        else if (!string.Equals(installedVer, result.LatestStableVersion, StringComparison.OrdinalIgnoreCase))
            upgradeAvailableCount++;
    }

    Console.WriteLine(new string('─', 110));
    Console.WriteLine();

    // Summary
    if (errorCount > 0)
        PrintColored($"  ✗ {errorCount} package(s) could not be checked.", ConsoleColor.DarkRed);
    if (incompatibleCount == 0 && upgradeAvailableCount == 0 && errorCount == 0)
        PrintColored(
            $"✔ All {packages.Count} package(s) are up to date and fully compatible with {projectTfm}.",
            ConsoleColor.Green);
    else
    {
        if (incompatibleCount > 0)
            PrintColored(
                $"⚠ {incompatibleCount} package(s): latest stable version is NOT compatible with {projectTfm} — " +
                "see WARN rows above for recommended versions.",
                ConsoleColor.Yellow);
        if (upgradeAvailableCount > 0)
            PrintColored(
                $"↑ {upgradeAvailableCount} package(s) have a newer compatible version available (see INFO rows).",
                ConsoleColor.Cyan);
    }

    Console.WriteLine();
    return incompatibleCount > 0 ? 1 : 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Batch install handler
// ─────────────────────────────────────────────────────────────────────────────

static async Task<int> RunBatchInstallAsync(
    string[] packageIds,
    string? projectPath,
    bool yes,
    bool dryRun)
{
    // 1. Resolve project file
    string csprojPath;
    try
    {
        csprojPath = projectPath is not null
            ? Path.GetFullPath(projectPath)
            : ProjectReader.FindCsprojInDirectory();
    }
    catch (ProjectReaderException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    if (ProjectReader.IsCentralPackageManagementEnabled(csprojPath))
    {
        PrintError("Central Package Management detected. This version does not yet support Directory.Packages.props.");
        return 1;
    }

    // 2. Read TFMs (only once)
    List<string> tfms;
    try
    {
        tfms = ProjectReader.ReadTargetFrameworks(csprojPath);
    }
    catch (ProjectReaderException ex)
    {
        PrintError(ex.Message);
        return 1;
    }

    Console.WriteLine($"Project : {Path.GetFileName(csprojPath)}");
    Console.WriteLine($"TFM(s)  : {string.Join(", ", tfms)}");
    Console.WriteLine();

    string projectTfm;
    if (tfms.Count > 1)
    {
        Console.WriteLine($"Note: Multi-targeting detected ({string.Join(", ", tfms)}).");
        Console.WriteLine($"      Resolving for primary TFM: {tfms[0]}");
        Console.WriteLine();
        projectTfm = tfms[0];
    }
    else
    {
        projectTfm = tfms[0];
    }

    // 3. Resolve all packages
    Console.WriteLine($"Resolving {packageIds.Length} package(s)…");
    Console.WriteLine();

    using var nugetClient = new NuGetClient();
    var resolver = new CompatibilityResolver();
    var orchestrator = new BatchInstallOrchestrator(
        nugetClient.GetAllVersionsAsync, resolver);

    var results = await orchestrator.ResolveAllAsync(packageIds, projectTfm);

    // 4. Display summary table
    ExplanationGenerator.PrintBatchSummaryTable(results);

    // 5. Handle dry-run
    if (dryRun)
    {
        PrintColored("Dry-run mode — nothing was installed.", ConsoleColor.Cyan);
        Console.WriteLine();
        return 0;
    }

    // 6. Filter installable packages
    var installable = results.Where(r => r.IsInstallable).ToList();
    if (installable.Count == 0)
    {
        PrintColored("No installable packages found.", ConsoleColor.Yellow);
        Console.WriteLine();
        return 1;
    }

    // 7. Prompt once for all packages
    if (!yes)
    {
        Console.Write($"Install {installable.Count} package(s)? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is not ("y" or "yes"))
        {
            Console.WriteLine("Installation cancelled.");
            return 0;
        }
    }

    Console.WriteLine();

    // 8. Install sequentially using the existing InstallRunner
    var runner = new InstallRunner();
    var (installed, failed) = await BatchInstallOrchestrator.InstallAllAsync(
        installable,
        csprojPath,
        runner.InstallPackageAsync);

    // 9. Final summary
    int skipped = packageIds.Length - installable.Count;
    Console.WriteLine();
    Console.WriteLine(new string('─', 60));
    Console.WriteLine($"  Total requested : {packageIds.Length}");
    PrintColored($"  Installed       : {installed}",
        installed > 0 ? ConsoleColor.Green : ConsoleColor.Gray);
    PrintColored($"  Failed          : {failed}",
        failed > 0 ? ConsoleColor.Red : ConsoleColor.Gray);
    PrintColored($"  Skipped         : {skipped}",
        skipped > 0 ? ConsoleColor.Yellow : ConsoleColor.Gray);
    Console.WriteLine(new string('─', 60));
    Console.WriteLine();

    return failed > 0 ? 1 : 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Utility helpers
// ─────────────────────────────────────────────────────────────────────────────

static void PrintBanner()
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    var infoVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion;
        
    var version = infoVersion?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "1.0";
    var title = $"Smart NuGet Compatibility Assistant  v{version}";
    
    int totalInnerWidth = 57;
    int leftPadding = (totalInnerWidth - title.Length) / 2;
    int rightPadding = totalInnerWidth - title.Length - leftPadding;
    
    if (leftPadding < 1) leftPadding = 1;
    if (rightPadding < 1) rightPadding = 1;

    var paddedTitle = new string(' ', leftPadding) + title + new string(' ', rightPadding);

    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("┌" + new string('─', totalInnerWidth) + "┐");
    Console.WriteLine($"│{paddedTitle}│");
    Console.WriteLine("└" + new string('─', totalInnerWidth) + "┘");
    Console.ForegroundColor = prev;
    Console.WriteLine();
}

static void PrintError(string message)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Error: {message}");
    Console.ForegroundColor = prev;
}

static void PrintColored(string message, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}

internal class CustomHelpBuilder : HelpBuilder
{
    public CustomHelpBuilder() : base(LocalizationResources.Instance) {}

    public override void Write(HelpContext context)
    {
        using var stringWriter = new StringWriter();
        var newContext = new HelpContext(this, context.Command, stringWriter, context.ParseResult);
        base.Write(newContext);

        var output = stringWriter.ToString();
        var modified = output.Replace("<PackageId>...", "<PackageId> [<PackageId>...]");
        context.Output.Write(modified);
    }
}
