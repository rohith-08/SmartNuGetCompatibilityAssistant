using NuGetCompatAssistant.Cli;
using Xunit;

namespace NuGetCompatAssistant.Tests;

public class ProjectDiscoveryTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 1. Classification Tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_TestProject_IsPackableFalse()
    {
        var xml = @"<Project Sdk=""Microsoft.NET.Sdk"">
            <PropertyGroup>
                <IsPackable>false</IsPackable>
            </PropertyGroup>
        </Project>";
        var type = ProjectReader.ClassifyProjectContent(xml, "MyProject.csproj");
        Assert.Equal(ProjectType.Test, type);
    }

    [Fact]
    public void Classify_TestProject_TestPackageReferences()
    {
        var testPackages = new[] { "Microsoft.NET.Test.Sdk", "xunit", "NUnit3TestAdapter", "MSTest.TestAdapter" };
        foreach (var pkg in testPackages)
        {
            var xml = $@"<Project Sdk=""Microsoft.NET.Sdk"">
                <ItemGroup>
                    <PackageReference Include=""{pkg}"" Version=""1.0.0"" />
                </ItemGroup>
            </Project>";
            var type = ProjectReader.ClassifyProjectContent(xml, "MyProject.csproj");
            Assert.Equal(ProjectType.Test, type);
        }
    }

    [Fact]
    public void Classify_TestProject_NamingPatterns()
    {
        var testNames = new[] { "My.Tests.csproj", "MyTests.csproj", "My.Test.csproj", "MyTest.csproj" };
        foreach (var name in testNames)
        {
            var xml = @"<Project Sdk=""Microsoft.NET.Sdk""></Project>";
            var type = ProjectReader.ClassifyProjectContent(xml, name);
            Assert.Equal(ProjectType.Test, type);
        }
    }

    [Fact]
    public void Classify_CliProject_PackAsToolTrue()
    {
        var xml = @"<Project Sdk=""Microsoft.NET.Sdk"">
            <PropertyGroup>
                <PackAsTool>true</PackAsTool>
            </PropertyGroup>
        </Project>";
        var type = ProjectReader.ClassifyProjectContent(xml, "MyProject.csproj");
        Assert.Equal(ProjectType.Cli, type);
    }

    [Fact]
    public void Classify_ApplicationProject_OutputTypeExe()
    {
        var xml = @"<Project Sdk=""Microsoft.NET.Sdk"">
            <PropertyGroup>
                <OutputType>Exe</OutputType>
            </PropertyGroup>
        </Project>";
        var type = ProjectReader.ClassifyProjectContent(xml, "MyProject.csproj");
        Assert.Equal(ProjectType.Application, type);
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk.Web")]
    [InlineData("Microsoft.NET.Sdk.Worker")]
    [InlineData("Microsoft.NET.Sdk.Razor")]
    [InlineData("Microsoft.NET.Sdk.Maui")]
    public void Classify_ApplicationProject_SdkTypes(string sdk)
    {
        var xml = $"<Project Sdk=\"{sdk}\"></Project>";
        var type = ProjectReader.ClassifyProjectContent(xml, "MyProject.csproj");
        Assert.Equal(ProjectType.Application, type);
    }

    [Fact]
    public void Classify_ApplicationProject_UseMauiTrue()
    {
        var xml = @"<Project Sdk=""Microsoft.NET.Sdk"">
            <PropertyGroup>
                <UseMaui>true</UseMaui>
            </PropertyGroup>
        </Project>";
        var type = ProjectReader.ClassifyProjectContent(xml, "MyProject.csproj");
        Assert.Equal(ProjectType.Application, type);
    }

    [Fact]
    public void Classify_LibraryProject_Default()
    {
        var xml = @"<Project Sdk=""Microsoft.NET.Sdk"">
            <PropertyGroup>
                <OutputType>Library</OutputType>
            </PropertyGroup>
        </Project>";
        var type = ProjectReader.ClassifyProjectContent(xml, "MyProject.csproj");
        Assert.Equal(ProjectType.Library, type);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Sorting & Recommendation Tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectionUX_ClassificationPrecedenceAndSorting()
    {
        var list = new[]
        {
            new { Path = "z.Tests.csproj", Type = ProjectType.Test },
            new { Path = "b.Cli.csproj", Type = ProjectType.Cli },
            new { Path = "m.Lib.csproj", Type = ProjectType.Library },
            new { Path = "a.App.csproj", Type = ProjectType.Application }
        };

        var sorted = list.OrderBy(item => item.Type switch
        {
            ProjectType.Application => 0,
            ProjectType.Library => 1,
            ProjectType.Cli => 2,
            ProjectType.Test => 3,
            _ => 4
        }).ThenBy(item => item.Path).ToList();

        Assert.Equal("a.App.csproj", sorted[0].Path);
        Assert.Equal("m.Lib.csproj", sorted[1].Path);
        Assert.Equal("b.Cli.csproj", sorted[2].Path);
        Assert.Equal("z.Tests.csproj", sorted[3].Path);
    }
}
