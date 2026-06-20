using NuGetCompatAssistant.Cli;
using System.IO;
using Xunit;

namespace NuGetCompatAssistant.Tests;

public class ProjectReaderTests : IDisposable
{
    // Temp directory cleaned up after each test
    private readonly string _tempDir;

    public ProjectReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NuGetCompatTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ReadTargetFrameworks — happy paths
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadTargetFrameworks_SingleTfm_ReturnsSingleElement()
    {
        // Arrange
        var csproj = WriteCsproj(_tempDir, "Single.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var tfms = ProjectReader.ReadTargetFrameworks(csproj);

        // Assert
        Assert.Single(tfms);
        Assert.Equal("net8.0", tfms[0]);
    }

    [Fact]
    public void ReadTargetFrameworks_MultipleTfms_ReturnsAllElements()
    {
        // Arrange
        var csproj = WriteCsproj(_tempDir, "Multi.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;netstandard2.0</TargetFrameworks>
  </PropertyGroup>
</Project>");

        // Act
        var tfms = ProjectReader.ReadTargetFrameworks(csproj);

        // Assert
        Assert.Equal(3, tfms.Count);
        Assert.Contains("net8.0", tfms);
        Assert.Contains("net9.0", tfms);
        Assert.Contains("netstandard2.0", tfms);
    }

    [Fact]
    public void ReadTargetFrameworks_TfmWithWhitespace_IsTrimmered()
    {
        // Arrange
        var csproj = WriteCsproj(_tempDir, "Whitespace.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>  net8.0 ; net9.0  </TargetFrameworks>
  </PropertyGroup>
</Project>");

        // Act
        var tfms = ProjectReader.ReadTargetFrameworks(csproj);

        // Assert
        Assert.Equal(2, tfms.Count);
        Assert.Equal("net8.0", tfms[0]);
        Assert.Equal("net9.0", tfms[1]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ReadTargetFrameworks — error cases
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadTargetFrameworks_MissingFile_ThrowsProjectReaderException()
    {
        var nonExistentPath = Path.Combine(_tempDir, "DoesNotExist.csproj");

        var ex = Assert.Throws<ProjectReaderException>(() =>
            ProjectReader.ReadTargetFrameworks(nonExistentPath));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadTargetFrameworks_NoTfmDeclared_ThrowsProjectReaderException()
    {
        var csproj = WriteCsproj(_tempDir, "NoTfm.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>MyLib</AssemblyName>
  </PropertyGroup>
</Project>");

        var ex = Assert.Throws<ProjectReaderException>(() =>
            ProjectReader.ReadTargetFrameworks(csproj));

        Assert.Contains("TargetFramework", ex.Message);
    }

    [Fact]
    public void ReadTargetFrameworks_InvalidXml_ThrowsProjectReaderException()
    {
        var badFile = Path.Combine(_tempDir, "Bad.csproj");
        File.WriteAllText(badFile, "THIS IS NOT XML <<< >>>");

        Assert.Throws<ProjectReaderException>(() =>
            ProjectReader.ReadTargetFrameworks(badFile));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FindCsprojInDirectory
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindCsprojInDirectory_ExactlyOne_ReturnsIt()
    {
        WriteCsproj(_tempDir, "MyApp.csproj", "<Project />");

        var found = ProjectReader.FindCsprojInDirectory(_tempDir);

        Assert.EndsWith("MyApp.csproj", found, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindCsprojInDirectory_NonePresent_ThrowsProjectReaderException()
    {
        Assert.Throws<ProjectReaderException>(() =>
            ProjectReader.FindCsprojInDirectory(_tempDir));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ReadPackageReferences
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadPackageReferences_ReturnsAllPackages()
    {
        var csproj = WriteCsproj(_tempDir, "Packages.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.3"" />
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>");

        var packages = ProjectReader.ReadPackageReferences(csproj);

        Assert.Equal(2, packages.Count);
        Assert.Contains(packages, p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3");
        Assert.Contains(packages, p => p.Id == "Serilog" && p.Version == "3.0.0");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsCentralPackageManagementEnabled
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsCentralPackageManagementEnabled_TrueInCsproj_ReturnsTrue()
    {
        var csproj = WriteCsproj(_tempDir, "CpmCsproj.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>");

        var isEnabled = ProjectReader.IsCentralPackageManagementEnabled(csproj);

        Assert.True(isEnabled);
    }

    [Fact]
    public void IsCentralPackageManagementEnabled_DirectoryPackagesPropsExists_ReturnsTrue()
    {
        // Create a subfolder to check recursive lookup
        var subDir = Path.Combine(_tempDir, "SubFolder");
        Directory.CreateDirectory(subDir);

        File.WriteAllText(
            Path.Combine(_tempDir, "Directory.Packages.props"),
            @"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");

        var csproj = WriteCsproj(subDir, "App.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var isEnabled = ProjectReader.IsCentralPackageManagementEnabled(csproj);

        Assert.True(isEnabled);
    }

    [Fact]
    public void IsCentralPackageManagementEnabled_DirectoryPackagesPropsDisabled_ReturnsFalse()
    {
        var subDir = Path.Combine(_tempDir, "SubFolderDisabled");
        Directory.CreateDirectory(subDir);

        File.WriteAllText(
            Path.Combine(_tempDir, "Directory.Packages.props"),
            @"<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");

        var csproj = WriteCsproj(subDir, "App.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var isEnabled = ProjectReader.IsCentralPackageManagementEnabled(csproj);

        Assert.False(isEnabled);
    }

    [Fact]
    public void IsCentralPackageManagementEnabled_NoCpm_ReturnsFalse()
    {
        var csproj = WriteCsproj(_tempDir, "NoCpm.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var isEnabled = ProjectReader.IsCentralPackageManagementEnabled(csproj);

        Assert.False(isEnabled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string WriteCsproj(string dir, string filename, string content)
    {
        var path = Path.Combine(dir, filename);
        File.WriteAllText(path, content.Trim());
        return path;
    }
}
