using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace PersonalAgent.ArchitectureTests;

public sealed class ArchitectureBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedProjectReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["PersonalAgent.Domain"] = [],
            ["PersonalAgent.Application"] = ["PersonalAgent.Domain"],
            ["PersonalAgent.Infrastructure"] = ["PersonalAgent.Application", "PersonalAgent.Domain"],
            ["PersonalAgent.Web"] =
            [
                "PersonalAgent.Application",
                "PersonalAgent.Domain",
                "PersonalAgent.Infrastructure",
                "PersonalAgent.ServiceDefaults"
            ],
            ["PersonalAgent.ServiceDefaults"] = [],
            ["PersonalAgent.AppHost"] = ["PersonalAgent.Web", "PersonalAgent.SimulatorEndpoints"],
            ["PersonalAgent.UnitTests"] =
            [
                "PersonalAgent.Application",
                "PersonalAgent.Domain",
                "PersonalAgent.TestSupport"
            ],
            ["PersonalAgent.ArchitectureTests"] =
            [
                "PersonalAgent.Application",
                "PersonalAgent.Domain",
                "PersonalAgent.Infrastructure",
                "PersonalAgent.ServiceDefaults",
                "PersonalAgent.Web"
            ],
            ["PersonalAgent.IntegrationTests"] =
            [
                "PersonalAgent.Application",
                "PersonalAgent.Domain",
                "PersonalAgent.Infrastructure",
                "PersonalAgent.TestSupport",
                "PersonalAgent.Web"
            ],
            ["PersonalAgent.SdkContractTests"] = ["PersonalAgent.Infrastructure", "PersonalAgent.TestSupport"],
            ["PersonalAgent.E2ETests"] = ["PersonalAgent.AppHost", "PersonalAgent.TestSupport"],
            ["PersonalAgent.TestSupport"] = []
        };

    [Fact]
    [Trait("Category", "Architecture")]
    public void ProjectReferencesMatchTheAllowedDependencyGraph()
    {
        var root = FindRepositoryRoot();

        foreach (var (projectName, allowed) in AllowedProjectReferences)
        {
            var projectPath = FindProject(root, projectName);
            var actual = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension((string?)reference.Attribute("Include")))
                .Where(name => name is not null)
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Empty(ArchitectureRules.FindUnexpectedReferences(actual, allowed));
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void CompiledProductionAssembliesHaveNoForbiddenLayerReferences()
    {
        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            ["PersonalAgent.Domain"] = typeof(PersonalAgent.Domain.ConversationId).Assembly,
            ["PersonalAgent.Application"] = typeof(PersonalAgent.Application.IAgentEngine).Assembly,
            ["PersonalAgent.Infrastructure"] = Assembly.Load("PersonalAgent.Infrastructure"),
            ["PersonalAgent.Web"] = Assembly.Load("PersonalAgent.Web"),
            ["PersonalAgent.ServiceDefaults"] = Assembly.Load("PersonalAgent.ServiceDefaults")
        };
        var infrastructure = assemblies["PersonalAgent.Infrastructure"];
        var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["PersonalAgent.Domain"] = [],
            ["PersonalAgent.Application"] = ["PersonalAgent.Domain"],
            ["PersonalAgent.Infrastructure"] = ["PersonalAgent.Application", "PersonalAgent.Domain"],
            ["PersonalAgent.Web"] =
            [
                "PersonalAgent.Application",
                "PersonalAgent.Domain",
                "PersonalAgent.Infrastructure",
                "PersonalAgent.ServiceDefaults"
            ],
            ["PersonalAgent.ServiceDefaults"] = []
        };

        foreach (var (projectName, assembly) in assemblies)
        {
            var references = assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty);
            Assert.Empty(ArchitectureRules.FindUnexpectedInternalReferences(
                references,
                allowed[projectName]));
            if (projectName is "PersonalAgent.Domain" or "PersonalAgent.Application")
            {
                Assert.Empty(ArchitectureRules.FindForbiddenFrameworkReferences(references));
            }
        }

        var misplacedCopilotTypes = infrastructure.GetTypes()
            .Where(type => ArchitectureRules.GetDeclaredTypeDependencies(type)
                .Any(name => name.StartsWith("GitHub.Copilot", StringComparison.Ordinal)))
            .Where(type => !ArchitectureRules.IsAllowedCopilotNamespace(type.Namespace))
            .Select(type => type.FullName ?? type.Name);
        Assert.Empty(misplacedCopilotTypes);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void CompiledTypeDependenciesRejectForbiddenFrameworkTypes()
    {
        var violations = ArchitectureRules.FindForbiddenFrameworkReferences(
            ArchitectureRules.GetDeclaredTypeDependencies(typeof(ForbiddenDependencyFixture)));

        Assert.Contains(violations, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void CopilotSdkReferenceIsConfinedToInfrastructureAdapterAssembly()
    {
        var root = FindRepositoryRoot();
        var violations = new List<string>();
        foreach (var assembly in new[]
                 {
                     typeof(PersonalAgent.Domain.ConversationId).Assembly,
                     typeof(PersonalAgent.Application.IAgentEngine).Assembly,
                     Assembly.Load("PersonalAgent.Infrastructure"),
                     Assembly.Load("PersonalAgent.Web")
                 })
        {
            var containsSdkReference = assembly.GetReferencedAssemblies()
                .Any(reference => reference.Name?.StartsWith("GitHub.Copilot", StringComparison.Ordinal) == true);
            if (containsSdkReference && assembly.GetName().Name != "PersonalAgent.Infrastructure")
            {
                violations.Add(assembly.GetName().Name ?? "(unnamed)");
            }
        }

        foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !Path.GetRelativePath(root, path).StartsWith("tools/sdk-validation/", StringComparison.Ordinal)))
        {
            var packageReferences = XDocument.Load(project)
                .Descendants("PackageReference")
                .Select(reference => (string?)reference.Attribute("Include"))
                .Where(name => name?.StartsWith("GitHub.Copilot", StringComparison.Ordinal) == true);
            if (packageReferences.Any() && Path.GetFileNameWithoutExtension(project) != "PersonalAgent.Infrastructure")
            {
                violations.Add(Path.GetFileNameWithoutExtension(project));
            }
        }

        Assert.Empty(violations);
        Assert.True(ArchitectureRules.IsAllowedCopilotNamespace(
            "PersonalAgent.Infrastructure.AgentEngine.Copilot"));
        Assert.False(ArchitectureRules.IsAllowedCopilotNamespace(
            "PersonalAgent.Infrastructure.AgentEngine.RenamedCopilot"));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void WebInfrastructureTypesAreUsedOnlyInCompositionRoot()
    {
        var webDirectory = Path.Combine(FindRepositoryRoot(), "src", "PersonalAgent.Web");
        var violations = ArchitectureRules.FindNonCompositionInfrastructureUsages(webDirectory);

        Assert.Empty(violations);
        Assert.Equal(
            ["Pages/Forbidden.cs"],
            ArchitectureRules.FindNonCompositionInfrastructureUsages(
                webDirectory,
                new Dictionary<string, string>
                {
                    ["Pages/Forbidden.cs"] = "using PersonalAgent.Infrastructure.TestFixture;"
                }));
        Assert.Equal(
            ["Pages/Program.cs"],
            ArchitectureRules.FindNonCompositionInfrastructureUsages(
                webDirectory,
                new Dictionary<string, string>
                {
                    ["Pages/Program.cs"] = "using PersonalAgent.Infrastructure.TestFixture;"
                }));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ProjectReferenceFixtureProvesForbiddenEdgesFail()
    {
        var violations = ArchitectureRules.FindUnexpectedReferences(
            ["PersonalAgent.Application", "PersonalAgent.Infrastructure"],
            ["PersonalAgent.Application"]);

        Assert.Equal(["PersonalAgent.Infrastructure"], violations);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static string FindProject(string root, string name)
    {
        var project = Directory.EnumerateFiles(root, $"{name}.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        return project ?? throw new FileNotFoundException($"Project {name} was not found.");
    }
}

internal static class ArchitectureRules
{
    private const string CopilotNamespace = "PersonalAgent.Infrastructure.AgentEngine.Copilot";

    internal static string[] FindUnexpectedReferences(
        IEnumerable<string> actual,
        IEnumerable<string> allowed) =>
        actual.Except(allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static string[] FindUnexpectedInternalReferences(
        IEnumerable<string> actual,
        IEnumerable<string> allowed) =>
        actual.Where(name => name.StartsWith("PersonalAgent.", StringComparison.Ordinal))
            .Except(allowed, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static string[] FindForbiddenFrameworkReferences(IEnumerable<string> references) =>
        references.Where(name =>
                name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || name.StartsWith("Aspire.", StringComparison.Ordinal)
                || name.StartsWith("GitHub.Copilot", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static bool IsAllowedCopilotNamespace(string? namespaceName) =>
        namespaceName is not null
        && (namespaceName.Equals(CopilotNamespace, StringComparison.Ordinal)
            || namespaceName.StartsWith($"{CopilotNamespace}.", StringComparison.Ordinal));

    internal static string[] GetDeclaredTypeDependencies(Type type)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        AddType(type.BaseType, dependencies);
        foreach (var contract in type.GetInterfaces())
        {
            AddType(contract, dependencies);
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            AddType(field.FieldType, dependencies);
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            AddType(property.PropertyType, dependencies);
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            AddType(method.ReturnType, dependencies);
            foreach (var parameter in method.GetParameters())
            {
                AddType(parameter.ParameterType, dependencies);
            }
        }

        return dependencies.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] FindNonCompositionInfrastructureUsages(string webDirectory) =>
        FindNonCompositionInfrastructureUsages(
            webDirectory,
            Directory.EnumerateFiles(webDirectory, "*.cs", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(webDirectory, path),
                    File.ReadAllText,
                    StringComparer.Ordinal));

    internal static string[] FindNonCompositionInfrastructureUsages(
        string webDirectory,
        IReadOnlyDictionary<string, string> sources) =>
        sources.Where(pair =>
                !pair.Key.Replace('\\', '/').Equals("Program.cs", StringComparison.Ordinal)
                && pair.Value.Contains("PersonalAgent.Infrastructure", StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AddType(Type? type, ISet<string> dependencies)
    {
        if (type is null)
        {
            return;
        }

        if (type.HasElementType)
        {
            AddType(type.GetElementType(), dependencies);
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                AddType(argument, dependencies);
            }
        }

        var assemblyName = type.Assembly.GetName().Name;
        if (assemblyName is not null)
        {
            dependencies.Add(assemblyName);
        }
    }
}
