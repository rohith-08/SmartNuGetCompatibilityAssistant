using System.Diagnostics;

namespace NuGetCompatAssistant.Cli;

/// <summary>
/// Prompts the user for confirmation and shells out to
/// <c>dotnet add package {id} --version {version}</c> to perform the actual installation.
/// We never edit the .csproj XML ourselves — we always delegate to the official dotnet CLI.
/// </summary>
public class InstallRunner
{
    /// <summary>
    /// Runs the install flow: optionally confirms with the user, then shells out to dotnet CLI.
    /// </summary>
    /// <param name="packageId">The NuGet package ID to install.</param>
    /// <param name="version">The specific version to install.</param>
    /// <param name="csprojPath">Path to the .csproj to add the package to.</param>
    /// <param name="skipConfirmation">If true, skips the y/n prompt (--yes flag).</param>
    /// <returns>True if the install succeeded; false otherwise.</returns>
    public async Task<bool> RunAsync(
        string packageId,
        string version,
        string csprojPath,
        bool skipConfirmation = false)
    {
        Console.WriteLine();
        Console.WriteLine($"  Package : {packageId}");
        Console.WriteLine($"  Version : {version}");
        Console.WriteLine($"  Project : {csprojPath}");
        Console.WriteLine();

        if (!skipConfirmation)
        {
            Console.Write("Install this package? [y/N] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                Console.WriteLine("Installation cancelled.");
                return false;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Running: dotnet add \"{csprojPath}\" package {packageId} --version {version}");
        Console.WriteLine(new string('─', 60));

        var exitCode = await RunDotnetAddPackageAsync(packageId, version, csprojPath);

        Console.WriteLine(new string('─', 60));
        if (exitCode == 0)
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ Successfully installed {packageId} {version}");
            Console.ForegroundColor = prevColor;
            return true;
        }
        else
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ Failed to install {packageId}: dotnet exited with code {exitCode}.");
            Console.ForegroundColor = prevColor;
            return false;
        }
    }

    /// <summary>
    /// Installs a package without any interactive prompts or verbose headers.
    /// Used by the batch install flow, which handles its own confirmation and progress display.
    /// </summary>
    /// <param name="packageId">The NuGet package ID to install.</param>
    /// <param name="version">The specific version to install.</param>
    /// <param name="csprojPath">Path to the .csproj to add the package to.</param>
    /// <returns>True if the install succeeded; false otherwise.</returns>
    public async Task<bool> InstallPackageAsync(
        string packageId,
        string version,
        string csprojPath)
    {
        Console.WriteLine($"  Running: dotnet add \"{csprojPath}\" package {packageId} --version {version}");

        var exitCode = await RunDotnetAddPackageAsync(packageId, version, csprojPath);

        if (exitCode == 0)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✔ Successfully installed {packageId} {version}");
            Console.ForegroundColor = prev;
            return true;
        }
        else
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ✗ Failed to install {packageId}: dotnet exited with code {exitCode}.");
            Console.ForegroundColor = prev;
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private static async Task<int> RunDotnetAddPackageAsync(
        string packageId,
        string version,
        string csprojPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"add \"{csprojPath}\" package {packageId} --version {version}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = startInfo };

        try
        {
            // Stream stdout and stderr live to the console
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    Console.WriteLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(e.Data);
                    Console.ForegroundColor = prev;
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Use CancellationToken.None — we want to wait indefinitely for dotnet to finish
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to launch 'dotnet': {ex.Message}");
            Console.Error.WriteLine("Make sure the .NET SDK is installed and on your PATH.");
            return -1;
        }
        finally
        {
            // Always dispose the process to release OS handles
            process.Dispose();
        }
    }
}
