# Smart NuGet Compatibility Assistant

A **.NET 8 global CLI tool** that finds the **best compatible version** of a NuGet package for your project's target framework — before you install it.

---

## The Problem

`dotnet add package Microsoft.EntityFrameworkCore` installs the **latest stable version**, which may target `net10.0` or `net9.0` — making it fail to install, or worse, installing but breaking at runtime, if your project targets `net8.0`.  
This tool reads your `.csproj`, queries the NuGet API for all available versions, uses **NuGet's own framework compatibility logic** (via the official `NuGet.Frameworks` library) to find the highest version that actually works, and explains exactly why it made that choice — in plain English.

---

## Quick Start

```bash
# 1. Clone / download the repo
git clone https://github.com/your-org/nuget-compat-assistant
cd nuget-compat-assistant

# 2. Pack and install as a global tool
dotnet pack src/NuGetCompatAssistant.Cli
dotnet tool install --global --add-source ./nupkg nuget-compat-assistant

# 3. Use it — single package
nuget-compat-assistant install Microsoft.EntityFrameworkCore --dry-run

# 3b. Or batch install multiple packages at once (v1.1+)
nuget-compat-assistant install AutoMapper Serilog Microsoft.EntityFrameworkCore --dry-run
```

Or run directly without installing:

```bash
dotnet run --project src/NuGetCompatAssistant.Cli -- install Microsoft.EntityFrameworkCore --dry-run
```

---

## Installation

### As a .NET Global Tool (recommended)

```bash
# Pack the tool (creates a .nupkg in ./nupkg/)
dotnet pack src/NuGetCompatAssistant.Cli

# Install globally from local feed
dotnet tool install --global --add-source ./nupkg nuget-compat-assistant

# Verify
nuget-compat-assistant --help
```

### Uninstall

```bash
dotnet tool uninstall --global nuget-compat-assistant
```

---

## Usage

### Install the best compatible version of a package

```bash
nuget-compat-assistant install Microsoft.EntityFrameworkCore
```

**Sample output:**

```
┌─────────────────────────────────────────────────────────────┐
│        Smart NuGet Compatibility Assistant  v1.1.0           │
└─────────────────────────────────────────────────────────────┘

Project : MyApi.csproj
TFM(s)  : net8.0

Querying NuGet for 'Microsoft.EntityFrameworkCore' versions…
Found 312 listed versions.

✔ Recommending Microsoft.EntityFrameworkCore 8.0.21

Reason:
  Your project targets net8.0. The latest published version (10.0.2)
  only supports 'net10.0' and 'net9.0', so it would fail to install
  or cause runtime issues. Version 8.0.21 is the newest release that
  explicitly supports net8.0.

  ⚠ Latest version (10.0.2) is NOT compatible with net8.0.
    It only supports: 'net10.0', 'net9.0'
  ✔ Downgraded to 8.0.21 which supports net8.0.

Install this package? [y/N]
```

### Batch install multiple packages (v1.1+)

```bash
nuget-compat-assistant install AutoMapper Serilog Microsoft.EntityFrameworkCore --dry-run
```

**Sample output:**

```
Project : SampleApp.csproj
TFM(s)  : net8.0

Resolving 3 package(s)…

──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
 Package                                  Latest Stable      Recommended        Status                 Reason
──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
 ✔ AutoMapper                              16.1.1             16.1.1             Compatible             Latest stable is compatible
 ✔ Serilog                                 4.3.1              4.3.1              Compatible             Latest stable is compatible
 ⚠ Microsoft.EntityFrameworkCore           10.0.9             9.0.17             Downgrade              Latest (10.0.9) incompatible; downgraded
──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Dry-run mode — nothing was installed.
```

When you install without `--dry-run`, you'll be prompted once for all packages:

```
Install 3 package(s)? (y/n)
```

Then each package is installed sequentially with progress:

```
[1/3] Installing AutoMapper 16.1.1...
[2/3] Installing Serilog 4.3.1...
[3/3] Installing Microsoft.EntityFrameworkCore 9.0.17...
```

---

### CLI Options

```
nuget-compat-assistant install <PackageId> [<PackageId>...] [options]

Arguments:
  PackageId                  One or more NuGet package IDs
                             (e.g. Microsoft.EntityFrameworkCore Serilog AutoMapper)

Options:
  --project, -p <path>       Path to .csproj (auto-detects in current directory if omitted)
  --version, -v <version>    Check a specific version's compatibility (single-package only)
  --yes, -y                  Skip confirmation prompt and install immediately
  --dry-run                  Only show the recommendation; never install

nuget-compat-assistant [options]

Options:
  --report                   Print a compatibility report for ALL packages in the project
  --project, -p <path>       Path to .csproj (used with --report)
```

### Examples

```bash
# Dry-run: show recommendation without installing
nuget-compat-assistant install Microsoft.EntityFrameworkCore --dry-run

# Batch install: resolve and install multiple packages at once
nuget-compat-assistant install AutoMapper Serilog Microsoft.EntityFrameworkCore

# Batch dry-run: see the summary table without installing
nuget-compat-assistant install AutoMapper Serilog Microsoft.EntityFrameworkCore --dry-run

# Specify the project file explicitly
nuget-compat-assistant install Serilog --project ./src/MyApi/MyApi.csproj

# Auto-confirm the install (skip prompt)
nuget-compat-assistant install Newtonsoft.Json --yes

# Check whether a specific version is compatible
nuget-compat-assistant install Dapper --version 2.1.28 --dry-run

# Report on ALL existing packages in the project
nuget-compat-assistant --report

# Report for a specific project
nuget-compat-assistant --report --project ./src/MyApi/MyApi.csproj
```

### Sample --report output

```
┌─────────────────────────────────────────────────────────────┐
│        Smart NuGet Compatibility Assistant  v1.1.0           │
└─────────────────────────────────────────────────────────────┘

Project : SampleApp.csproj
TFM(s)  : net8.0

Analysing 3 package(s)…

────────────────────────────────────────────────────────────────────────────────
✔ OK   Newtonsoft.Json                               installed: 13.0.3          latest: 13.0.3          [up to date]
⚠ WARN Microsoft.EntityFrameworkCore                 installed: 7.0.0           latest: 10.0.2          → recommend: 8.0.21
✔ OK   Serilog                                       installed: 3.1.1           latest: 4.1.0           [up to date]

────────────────────────────────────────────────────────────────────────────────

⚠ 1 package(s) have newer versions that are not compatible with net8.0.
```

---

## How Compatibility Resolution Works

This tool uses **`NuGet.Frameworks`** — the same library that `dotnet add package` and the NuGet CLI itself use internally.

Key types used:
- **`NuGetFramework.Parse(tfm)`** — parses TFM strings like `"net8.0"`, `"netstandard2.0"`, `"net472"` into a strongly-typed framework object.
- **`DefaultCompatibilityProvider.IsCompatible(projectFramework, packageFramework)`** — returns `true` if a project targeting `projectFramework` can consume a package built for `packageFramework`.

This means:
- `net8.0` is compatible with packages built for `net6.0`, `net5.0`, `netstandard2.1`, `netstandard2.0` (and earlier), because of NuGet's **framework fallback** rules.
- `net8.0` is **not** compatible with packages that only target `net9.0`, `net10.0`, or `net472` (different stack).
- No hand-rolled string-matching — the official library handles all edge cases including `netcoreapp*`, `uap*`, `MonoAndroid*`, etc.

The algorithm:
1. Fetch all listed versions from the NuGet registration index.
2. For each version, check whether any of its **dependency group TFMs** are compatible with the project TFM (using `DefaultCompatibilityProvider`).
3. Sort all compatible versions descending by `NuGetVersion` (the official semver type).
4. Pick the **first** (highest) compatible version.
5. Compare with the latest overall version — if different, explain why the downgrade was needed.

---

## Architecture

### `ProjectReader`
Parses a `.csproj` file using `System.Xml.Linq` and extracts `<TargetFramework>` (single) or `<TargetFrameworks>` (semicolon-separated multi-target) values. Also reads `<PackageReference>` entries for the `--report` command. Throws `ProjectReaderException` with clear messages on missing files or missing TFM declarations.

### `NuGetClient`
Wraps `HttpClient` calls to the NuGet v3 REST API. Starts by reading the **NuGet Service Index** (`index.json`) to discover the live registration base URL, then fetches all listed versions from the package registration endpoint. Parses dependency groups (which encode the supported TFMs) from the catalog entry JSON. Handles network timeouts and 404s with user-friendly error messages — never exposes raw stack traces.

### `CompatibilityResolver`
The core logic class. Uses `NuGet.Frameworks.DefaultCompatibilityProvider` to test whether the project's TFM can satisfy each package version's declared TFMs. Sorts compatible versions using `NuGet.Versioning.NuGetVersion` (the official semver comparison) and returns the highest compatible version alongside metadata for explanation (whether the latest was compatible, what TFMs the latest declares, etc.).

### `ExplanationGenerator`
A static utility that converts a `CompatibilityResult` into coloured terminal output. Generates two styles of explanation: "latest is fine" (green) and "latest is incompatible — here's why, and here's what we picked instead" (yellow warning + green recommendation). Also produces compact report rows for `--report` mode, and a summary table for batch install results (v1.1+).

### `BatchInstallOrchestrator` *(v1.1+)*
Orchestrates batch package resolution and installation. Accepts a fetch function and a `CompatibilityResolver` via constructor injection (using `Func<>` delegates), making it fully testable without mocking frameworks or real network calls. Resolves each package independently and collects results into `BatchPackageResult` records.

### `InstallRunner`
Optionally prompts for `y/N` confirmation, then shells out to `dotnet add package {id} --version {version}` using `System.Diagnostics.Process`, streaming stdout and stderr live to the terminal. Never touches the `.csproj` XML directly — always delegates to the official dotnet CLI so the real NuGet resolver handles the actual package graph. In batch mode (v1.1+), the prompt-free `InstallPackageAsync` method is used instead, with the batch handler managing its own single confirmation prompt.

### `Program.cs`
The CLI entry point using `System.CommandLine`. Wires up the `install <PackageId> [<PackageId>...]` sub-command and the `--report` root flag. For a single package, uses the original v1.0 code path. For multiple packages (v1.1+), dispatches to `RunBatchInstallAsync` which orchestrates: read project → resolve all → summary table → prompt → install.

---

## Building & Testing

```bash
# Build the solution
dotnet build

# Run all unit tests
dotnet test

# Run a specific test class
dotnet test --filter "ClassName=CompatibilityResolverTests"

# Pack the tool for local install
dotnet pack src/NuGetCompatAssistant.Cli

# End-to-end dry run against the sample project
dotnet run --project src/NuGetCompatAssistant.Cli -- install Microsoft.EntityFrameworkCore --project samples/SampleApp/SampleApp.csproj --dry-run
```

---

## Known Limitations

- **No Central Package Management (CPM) support**: The tool does not support projects using Central Package Management (via `Directory.Packages.props`). To prevent incorrect or misleading outputs, the tool automatically detects if CPM is enabled and exits cleanly with an error warning.
- **Relies on author-declared TFMs**: The tool reads `dependencyGroups` from the NuGet catalog entry. If a package author mis-declares their TFMs (or uses a very old `packages.config`-era format without dependency groups), the compatibility check may be inaccurate.
- **No transitive dependency simulation**: The tool checks whether the selected package version is directly compatible with your TFM, but it does not simulate a full `dotnet restore` graph. Installing may still fail if a transitive dependency of the chosen version is incompatible.
- **Single primary TFM**: For multi-targeting projects (`<TargetFrameworks>`), the tool currently resolves against the first listed TFM. A future version will intersect compatible versions across all TFMs.
- **nuget.org only**: Private NuGet feeds (Azure Artifacts, GitHub Packages, etc.) are not yet supported.
- **No vulnerability scanning**: This tool does not check for known security vulnerabilities. Use `dotnet list package --vulnerable` for that.
- **Pre-release versions**: Pre-release packages are excluded from consideration (they are typically unlisted in the registration index). Support for pre-release resolution is a future enhancement.

---

## Roadmap / Future Enhancements

- **VS Code extension / VSIX**: Surface recommendations directly in the editor when you edit a `.csproj` or run `dotnet add package` from the integrated terminal.
- **Security vulnerability scanning**: Integrate `dotnet list package --vulnerable` output to warn when a recommended version has known CVEs.
- **Breaking-change detection**: Cross-reference GitHub release notes / NuGet changelogs to flag major-version upgrades that are known to have breaking changes.
- **Full restore-graph simulation**: Run `dotnet restore --dry-run` (or equivalent) before committing to a version, to verify the entire transitive dependency tree resolves correctly.
- **Private feed support**: Support authenticated NuGet feeds (Azure Artifacts, GitHub Packages, Nexus) via NuGet credential providers or explicit PAT flags.
- **Pre-release version support**: Add a `--pre-release` flag to include pre-release packages in the compatibility search.
- **Team policy config**: A `.nugetcompat.json` file per repo that pins allowed version ranges, enforces TFM-specific policies, and blocks known-bad versions.

---

## What's New

### v1.1.0 — Multi-Package Batch Install

- **Batch install**: Pass multiple package IDs in a single command:  
  `nuget-compat-assistant install AutoMapper Serilog Microsoft.EntityFrameworkCore`
- Each package is resolved independently against your project's TFM using the existing compatibility logic.
- A summary table shows the status of every package (Compatible, Downgrade, Not Found, No Compatible Version) before anything is installed.
- `--dry-run` stops after the summary table — no prompt, no installation.
- A single confirmation prompt (`Install N package(s)? (y/n)`) replaces per-package prompts.
- Sequential installation with progress display (`[1/3] Installing AutoMapper 16.1.1...`).
- Final summary shows total requested, installed, failed, and skipped counts.
- Exit code 0 only when every installation succeeds; exit code 1 if any fails.
- **100% backward compatible**: single-package usage follows the exact same v1.0 code path.
- 13 new unit tests covering all batch scenarios (49 total, up from 36).

### v1.0.0 — Initial Release

- Single-package compatibility resolution with NuGet's official framework compatibility logic.
- `--dry-run`, `--yes`, `--version`, `--report` support.
- Detailed plain-English explanations for every recommendation.
- Full compatibility report for all packages already referenced in a project.

---

## License

MIT
