using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

const string Usage = """
Usage:
  PersonalAgent.Validation gate-self-test
  PersonalAgent.Validation coverage <unit-results-dir> <integration-results-dir>
  PersonalAgent.Validation verify-discovery <trx-file>
  PersonalAgent.Validation verify-browser-failure <trx-file>
  PersonalAgent.Validation docs
  PersonalAgent.Validation playwright <Playwright CLI arguments...>
""";

try
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    switch (args[0])
    {
        case "gate-self-test" when args.Length == 1:
            CoverageGate.RunSelfTests();
            Console.WriteLine("Coverage, report, and discovery gate fixtures rejected invalid inputs.");
            return 0;
        case "coverage" when args.Length == 3:
            CoverageGate.Validate(args[1], args[2]);
            return 0;
        case "verify-discovery" when args.Length == 2:
            TestResults.VerifyDiscovery(args[1]);
            Console.WriteLine($"Test discovery verified: {args[1]}");
            return 0;
        case "verify-browser-failure" when args.Length == 2:
            TestResults.VerifyExpectedBrowserFailure(args[1]);
            Console.WriteLine("The Playwright failure probe was discovered and failed as expected.");
            return 0;
        case "docs" when args.Length == 1:
            DocumentationLinks.VerifyInternalLinks();
            return 0;
        case "playwright" when args.Length > 1:
            return Microsoft.Playwright.Program.Main(args[1..]);
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (ValidationException exception)
{
    Console.Error.WriteLine($"Validation failed: {exception.Message}");
    return 1;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"Validation could not read required input: {exception.Message}");
    return 1;
}
catch (XmlException exception)
{
    Console.Error.WriteLine($"Validation encountered malformed XML: {exception.Message}");
    return 1;
}

internal sealed class ValidationException(string message) : Exception(message)
{
}

internal sealed record CoverageLine(string File, int Number);

internal sealed class CoverageMetrics
{
    internal HashSet<CoverageLine> Lines { get; } = [];
    internal HashSet<CoverageLine> CoveredLines { get; } = [];
    internal HashSet<string> Branches { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> CoveredBranches { get; } = new(StringComparer.Ordinal);

    internal void AddLine(CoverageLine line, bool covered)
    {
        Lines.Add(line);
        if (covered)
        {
            CoveredLines.Add(line);
        }
    }

    internal void AddBranch(string branch, bool covered)
    {
        Branches.Add(branch);
        if (covered)
        {
            CoveredBranches.Add(branch);
        }
    }

    internal void Merge(CoverageMetrics other)
    {
        Lines.UnionWith(other.Lines);
        CoveredLines.UnionWith(other.CoveredLines);
        Branches.UnionWith(other.Branches);
        CoveredBranches.UnionWith(other.CoveredBranches);
    }
}

internal static class CoverageGate
{
    private static readonly string[] RuntimeAssemblies =
    [
        "PersonalAgent.Domain",
        "PersonalAgent.Application",
        "PersonalAgent.Infrastructure",
        "PersonalAgent.Web",
        "PersonalAgent.ServiceDefaults"
    ];

    private static readonly (string Name, string RelativePath, double Lines, double Branches)[] CriticalModules =
    [
        ("Copilot turn terminal and cancellation state machine", "src/PersonalAgent.Infrastructure/AgentEngine/Copilot/CopilotTurnStateMachine.cs", 95, 90),
        ("Copilot active-turn cancellation and runtime lifecycle", "src/PersonalAgent.Infrastructure/AgentEngine/Copilot/CopilotActiveTurn.cs", 95, 90),
        ("Approval persistence invariants", "src/PersonalAgent.Infrastructure/Persistence/SqliteApprovalStore.cs", 95, 90),
        ("Action journal idempotency", "src/PersonalAgent.Infrastructure/Persistence/SqliteActionJournalStore.cs", 95, 90)
    ];

    internal static void RunSelfTests()
    {
        var healthy = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            healthy.AddLine(new CoverageLine("fixture.cs", line), covered: true);
        }

        for (var branch = 1; branch <= 20; branch++)
        {
            healthy.AddBranch($"fixture.cs:1:{branch}", covered: true);
        }

        EnforceThreshold("passing fixture", healthy, 90, 85);

        var lowCoverage = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            lowCoverage.AddLine(new CoverageLine("fixture.cs", line), line <= 84);
        }

        ExpectFailure(() => EnforceThreshold("low coverage fixture", lowCoverage, 85, 75));
        ExpectFailure(() => EnforceThreshold(
            "empty coverage fixture",
            new CoverageMetrics(),
            80,
            70,
            allowNoLines: false));

        var lowBranchCoverage = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            lowBranchCoverage.AddLine(new CoverageLine("branch-fixture.cs", line), covered: true);
        }

        for (var branch = 1; branch <= 20; branch++)
        {
            lowBranchCoverage.AddBranch($"branch-fixture.cs:1:{branch}", branch <= 16);
        }

        ExpectFailure(() => EnforceThreshold("low branch coverage fixture", lowBranchCoverage, 90, 85));
        var mergedBranchReports = new CoverageReportSet();
        var repositoryRoot = Directory.GetCurrentDirectory();
        MergeOpenCoverBranches(
            repositoryRoot,
            CreateOpenCoverBranchFixture("1", sourceLine: 10, pathZeroVisits: 1, pathOneVisits: 0),
            mergedBranchReports,
            "first OpenCover branch fixture");
        MergeOpenCoverBranches(
            repositoryRoot,
            CreateOpenCoverBranchFixture("7", sourceLine: 11, pathZeroVisits: 0, pathOneVisits: 1),
            mergedBranchReports,
            "second OpenCover branch fixture");
        var mergedBranches = mergedBranchReports.AllAssemblies;
        if (mergedBranches.Branches.Count != 2 || mergedBranches.CoveredBranches.Count != 2)
        {
            throw new ValidationException("Complementary branch outcomes from separate reports were not merged.");
        }

        var emptyOpenCover = new CoverageReportSet();
        MergeOpenCoverBranches(
            repositoryRoot,
            XDocument.Parse("<CoverageSession><Modules /></CoverageSession>"),
            emptyOpenCover,
            "empty OpenCover fixture");
        ExpectFailure(() => RequireOpenCoverModules(
            ["PersonalAgent.Infrastructure"],
            emptyOpenCover,
            "empty OpenCover fixture"));
        var incompleteOpenCover = new CoverageReportSet();
        incompleteOpenCover.OpenCoverAssemblies.Add("PersonalAgent.Infrastructure");
        ExpectFailure(() => RequireOpenCoverModules(
            ["PersonalAgent.Infrastructure", "PersonalAgent.Application"],
            incompleteOpenCover,
            "missing-module OpenCover fixture"));

        var lowCriticalModule = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            lowCriticalModule.AddLine(new CoverageLine("approval-fixture.cs", line), line <= 94);
        }

        for (var branch = 1; branch <= 20; branch++)
        {
            lowCriticalModule.AddBranch($"approval-fixture.cs:1:{branch}", branch <= 17);
        }

        ExpectFailure(() => EnforceThreshold("critical module fixture", lowCriticalModule, 95, 90));
        var criticalModuleWithoutBranches = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            criticalModuleWithoutBranches.AddLine(new CoverageLine("critical-fixture.cs", line), covered: true);
        }

        ExpectFailure(() => EnforceThreshold(
            "critical module without branch data",
            criticalModuleWithoutBranches,
            95,
            90,
            allowNoLines: false,
            allowNoBranches: false));
        ExpectFailure(() => RequireReports([], "missing report fixture"));
        ExpectFailure(() => TestResults.VerifyDiscoveryXml(
            XDocument.Parse("<TestRun><ResultSummary><Counters total=\"0\" executed=\"0\" passed=\"0\" failed=\"0\" /></ResultSummary></TestRun>"),
            "missing discovery fixture"));
        VerifyChangedLineDiscoveryIncludesTrackedWorktreeEdits();
    }

    internal static void Validate(string unitDirectory, string integrationDirectory)
    {
        var unitResults = TestResults.ReadTestResults(unitDirectory);
        var integrationResults = TestResults.ReadTestResults(integrationDirectory);
        var unitReports = RequireReports(
            Directory.Exists(unitDirectory)
                ? Directory.EnumerateFiles(unitDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
                : [],
            "unit");
        var integrationReports = RequireReports(
            Directory.Exists(integrationDirectory)
                ? Directory.EnumerateFiles(integrationDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
                : [],
            "integration");
        var unitBranchReports = RequireReports(
            Directory.Exists(unitDirectory)
                ? Directory.EnumerateFiles(unitDirectory, "coverage.opencover.xml", SearchOption.AllDirectories)
                : [],
            "unit OpenCover");
        var integrationBranchReports = RequireReports(
            Directory.Exists(integrationDirectory)
                ? Directory.EnumerateFiles(integrationDirectory, "coverage.opencover.xml", SearchOption.AllDirectories)
                : [],
            "integration OpenCover");
        var root = FindRepositoryRoot();
        var unit = MergeReports(root, unitReports, unitBranchReports);
        var combined = MergeReports(
            root,
            unitReports.Concat(integrationReports),
            unitBranchReports.Concat(integrationBranchReports));
        var runtimeAssemblies = RuntimeAssemblies
            .Where(name => name != "PersonalAgent.Infrastructure" || HasHandwrittenSources(root, name))
            .ToArray();

        if (!unit.Assemblies.ContainsKey("PersonalAgent.Domain")
            || !unit.Assemblies.ContainsKey("PersonalAgent.Application"))
        {
            throw new ValidationException("Unit coverage is missing an expected Domain or Application assembly report.");
        }

        RequireOpenCoverModules(
            ["PersonalAgent.Domain", "PersonalAgent.Application"],
            unit,
            "unit");

        var missingAssemblies = runtimeAssemblies
            .Where(name => !combined.Assemblies.ContainsKey(name))
            .ToArray();
        if (missingAssemblies.Length > 0)
        {
            throw new ValidationException(
                $"Coverage reports are missing expected runtime assemblies: {string.Join(", ", missingAssemblies)}.");
        }

        RequireOpenCoverModules(runtimeAssemblies, combined, "combined unit + integration");

        EnforceThreshold("Domain unit", unit.Assemblies["PersonalAgent.Domain"], 90, 85, allowNoLines: false);
        EnforceThreshold(
            "Application unit",
            unit.Assemblies["PersonalAgent.Application"],
            90,
            85,
            allowNoLines: false);

        var runtime = new CoverageMetrics();
        foreach (var assemblyName in runtimeAssemblies)
        {
            var metrics = combined.Assemblies[assemblyName];
            runtime.Merge(metrics);
            EnforceThreshold(assemblyName, metrics, 80, 70, allowNoLines: false);
        }

        EnforceThreshold("combined unit + integration runtime", runtime, 85, 75, allowNoLines: false);
        EnforceChangedLineThreshold(root, combined.AllAssemblies);
        foreach (var module in CriticalModules)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(root, module.RelativePath));
            if (!File.Exists(sourcePath) || IsGeneratedSource(sourcePath))
            {
                throw new ValidationException(
                    $"Critical-module ownership source is missing or generated: {module.RelativePath}.");
            }

            var metrics = SelectSourceFile(combined.AllAssemblies, sourcePath);
            EnforceThreshold(
                module.Name,
                metrics,
                module.Lines,
                module.Branches,
                allowNoLines: false,
                allowNoBranches: false);
        }

        Console.WriteLine("Coverage thresholds passed. Totals use unique source lines and branch conditions.");
        foreach (var (name, metrics) in combined.Assemblies.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{name}: {Describe(metrics)}");
        }

        Console.WriteLine($"Unit test results: {unitResults.Total} discovered, {unitResults.Passed} passed.");
        Console.WriteLine(
            $"Integration test results: {integrationResults.Total} discovered, {integrationResults.Passed} passed.");
    }

    private static bool HasHandwrittenSources(string root, string assemblyName)
    {
        var sourceDirectory = Path.Combine(root, "src", assemblyName);
        return Directory.Exists(sourceDirectory)
            && Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Any(path => !IsGeneratedSource(path));
    }

    private static CoverageMetrics SelectSourceFile(CoverageMetrics source, string sourcePath)
    {
        var metrics = new CoverageMetrics();
        foreach (var line in source.Lines.Where(line =>
                     string.Equals(Path.GetFullPath(line.File), sourcePath, StringComparison.Ordinal)))
        {
            metrics.AddLine(line, source.CoveredLines.Contains(line));
        }

        var branchPrefix = sourcePath + ":";
        foreach (var branch in source.Branches.Where(branch => branch.StartsWith(branchPrefix, StringComparison.Ordinal)))
        {
            metrics.AddBranch(branch, source.CoveredBranches.Contains(branch));
        }

        return metrics;
    }

    private static IReadOnlyList<string> RequireReports(IEnumerable<string> reportPaths, string layer)
    {
        var reports = reportPaths
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reports.Length == 0)
        {
            throw new ValidationException($"The {layer} run produced no expected coverage report.");
        }

        return reports;
    }

    private static void RequireOpenCoverModules(
        IEnumerable<string> expectedAssemblies,
        CoverageReportSet reports,
        string layer)
    {
        var missingModules = expectedAssemblies
            .Where(name => !reports.OpenCoverAssemblies.Contains(name))
            .ToArray();
        if (missingModules.Length > 0)
        {
            throw new ValidationException(
                $"The {layer} OpenCover reports are missing expected runtime modules: {string.Join(", ", missingModules)}.");
        }
    }

    private static CoverageReportSet MergeReports(
        string root,
        IEnumerable<string> reportPaths,
        IEnumerable<string> branchReportPaths)
    {
        var result = new CoverageReportSet();
        foreach (var reportPath in reportPaths)
        {
            var document = LoadXml(reportPath);
            var coverageRoot = document.Root
                ?? throw new ValidationException($"Coverage report has no root element: {reportPath}.");
            var sources = coverageRoot.Element("sources")?.Elements("source")
                .Select(element => element.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray() ?? [];

            foreach (var classElement in coverageRoot.Descendants("class"))
            {
                var packageName = (string?)classElement.Ancestors("package").FirstOrDefault()?.Attribute("name")
                    ?? string.Empty;
                var assemblyName = RuntimeAssemblies.FirstOrDefault(
                    name => packageName.Equals(name, StringComparison.Ordinal));
                if (assemblyName is null)
                {
                    continue;
                }

                var fileName = (string?)classElement.Attribute("filename");
                if (string.IsNullOrWhiteSpace(fileName) || IsGeneratedSource(fileName))
                {
                    continue;
                }

                var fullPath = ResolveSourcePath(root, fileName, sources);
                var metrics = result.Assemblies.GetValueOrDefault(assemblyName);
                if (metrics is null)
                {
                    metrics = new CoverageMetrics();
                    result.Assemblies.Add(assemblyName, metrics);
                }

                foreach (var lineElement in classElement.Descendants("line"))
                {
                    if (!int.TryParse((string?)lineElement.Attribute("number"), NumberStyles.None, CultureInfo.InvariantCulture, out var lineNumber))
                    {
                        throw new ValidationException($"Coverage report contains an invalid line number: {reportPath}.");
                    }

                    var hits = ParseInteger(lineElement.Attribute("hits")?.Value, reportPath);
                    var line = new CoverageLine(fullPath, lineNumber);
                    metrics.AddLine(line, hits > 0);
                    result.AllAssemblies.AddLine(line, hits > 0);
                }
            }
        }

        foreach (var reportPath in branchReportPaths)
        {
            MergeOpenCoverBranches(root, LoadXml(reportPath), result, reportPath);
        }

        return result;
    }

    private static void MergeOpenCoverBranches(
        string root,
        XDocument document,
        CoverageReportSet reports,
        string reportPath)
    {
        var coverageRoot = document.Root
            ?? throw new ValidationException($"OpenCover report has no root element: {reportPath}.");
        foreach (var module in coverageRoot.Descendants("Module"))
        {
            var moduleName = module.Element("ModuleName")?.Value ?? string.Empty;
            var assemblyName = RuntimeAssemblies.FirstOrDefault(name =>
                moduleName.Equals(name, StringComparison.Ordinal));
            if (assemblyName is null)
            {
                continue;
            }

            reports.OpenCoverAssemblies.Add(assemblyName);
            var files = module.Descendants("File")
                .Where(file => file.Attribute("uid") is not null && file.Attribute("fullPath") is not null)
                .ToDictionary(
                    file => (string)file.Attribute("uid")!,
                    file => ResolveSourcePath(root, (string)file.Attribute("fullPath")!, []),
                    StringComparer.Ordinal);
            var metrics = reports.Assemblies.GetValueOrDefault(assemblyName);
            if (metrics is null)
            {
                metrics = new CoverageMetrics();
                reports.Assemblies.Add(assemblyName, metrics);
            }

            foreach (var method in module.Descendants("Method"))
            {
                var methodName = method.Element("Name")?.Value ?? string.Empty;
                foreach (var branch in method.Element("BranchPoints")?.Elements("BranchPoint") ?? [])
                {
                    var fileId = (string?)branch.Attribute("fileid")
                        ?? (string?)method.Element("FileRef")?.Attribute("uid");
                    if (fileId is null || !files.TryGetValue(fileId, out var filePath) || IsGeneratedSource(filePath))
                    {
                        continue;
                    }

                    if (!int.TryParse(
                            (string?)branch.Attribute("sl"),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var lineNumber)
                        || lineNumber < 1
                        || !int.TryParse(
                            (string?)branch.Attribute("ordinal"),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var ordinal)
                        || !int.TryParse(
                            (string?)branch.Attribute("vc"),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var visits))
                    {
                        throw new ValidationException($"OpenCover report contains an invalid branch point: {reportPath}.");
                    }

                    AddBranches(
                        metrics,
                        new CoverageLine(filePath, lineNumber),
                        methodName,
                        ordinal,
                        visits > 0);
                    AddBranches(
                        reports.AllAssemblies,
                        new CoverageLine(filePath, lineNumber),
                        methodName,
                        ordinal,
                        visits > 0);
                }
            }
        }
    }

    private static XDocument CreateOpenCoverBranchFixture(
        string fileId,
        int sourceLine,
        int pathZeroVisits,
        int pathOneVisits) =>
        XDocument.Parse(
            $"""
             <CoverageSession>
               <Modules>
                 <Module>
                   <ModuleName>PersonalAgent.Infrastructure</ModuleName>
                   <Files><File uid="{fileId}" fullPath="src/fixture.cs" /></Files>
                   <Classes>
                     <Class>
                       <Methods>
                         <Method name="Fixture.Method">
                           <Name>Fixture.Method</Name>
                           <BranchPoints>
                             <BranchPoint vc="{pathZeroVisits}" path="0" ordinal="0" offset="{sourceLine}" sl="{sourceLine}" fileid="{fileId}" />
                             <BranchPoint vc="{pathOneVisits}" path="1" ordinal="1" offset="{sourceLine}" sl="{sourceLine}" fileid="{fileId}" />
                           </BranchPoints>
                         </Method>
                       </Methods>
                     </Class>
                   </Classes>
                 </Module>
               </Modules>
             </CoverageSession>
             """);

    private static void AddBranches(
        CoverageMetrics metrics,
        CoverageLine line,
        string method,
        int ordinal,
        bool covered)
    {
        var key = $"{line.File}:{method}:{ordinal}";
        metrics.AddBranch(key, covered);
    }

    private static void EnforceChangedLineThreshold(string root, CoverageMetrics coverage)
    {
        var changed = ReadChangedSourceLines(root);
        var executable = changed.Where(coverage.Lines.Contains).ToHashSet();
        if (executable.Count == 0)
        {
            Console.WriteLine("Changed executable line coverage: N/A (no changed coverable lines).");
            return;
        }

        var covered = executable.Count(coverage.CoveredLines.Contains);
        var rate = (double)covered / executable.Count;
        if (rate < 0.90)
        {
            throw new ValidationException(
                $"Changed executable lines require 90% coverage; measured {Percent(covered, executable.Count)}.");
        }

        Console.WriteLine($"Changed executable lines: {Percent(covered, executable.Count)}.");
    }

    private static HashSet<CoverageLine> ReadChangedSourceLines(string root)
    {
        var changed = new HashSet<CoverageLine>();
        var baseRef = Environment.GetEnvironmentVariable("GITHUB_BASE_REF");
        var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
        var comparison = !string.IsNullOrWhiteSpace(baseRef)
            ? $"origin/{baseRef}...HEAD"
            : eventName == "push" ? "HEAD^...HEAD" : "HEAD";
        var diff = RunGit(root, ["diff", "--no-ext-diff", "--unified=0", comparison, "--", "src"]);
        ParseAddedLines(root, diff, changed);
        var stagedDiff = RunGit(root, ["diff", "--cached", "--no-ext-diff", "--unified=0", "HEAD", "--", "src"]);
        ParseAddedLines(root, stagedDiff, changed);
        var worktreeDiff = RunGit(root, ["diff", "--no-ext-diff", "--unified=0", "HEAD", "--", "src"]);
        ParseAddedLines(root, worktreeDiff, changed);
        var untracked = RunGit(root, ["ls-files", "--others", "--exclude-standard", "--", "src"]);
        foreach (var relativePath in untracked.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var absolutePath = Path.Combine(root, relativePath);
            if (!File.Exists(absolutePath) || IsGeneratedSource(relativePath))
            {
                continue;
            }

            var normalized = Path.GetFullPath(absolutePath);
            var lines = File.ReadAllLines(absolutePath);
            for (var index = 0; index < lines.Length; index++)
            {
                changed.Add(new CoverageLine(normalized, index + 1));
            }
        }

        return changed;
    }

    private static void VerifyChangedLineDiscoveryIncludesTrackedWorktreeEdits()
    {
        var directory = Directory.CreateTempSubdirectory("jarvis-coverage-diff-");
        var root = directory.FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            var sourcePath = Path.Combine(root, "src", "Fixture.cs");
            File.WriteAllText(
                sourcePath,
                """
                namespace Fixture;
                public sealed class CoverageFixture
                {
                    public void CommittedChange() { }
                    public void TrackedWorktreeChange() { }
                }
                """);
            RunGit(root, ["init", "--quiet", "-b", "main"]);
            RunGit(root, ["config", "user.name", "Coverage Gate"]);
            RunGit(root, ["config", "user.email", "coverage-gate@example.invalid"]);
            RunGit(root, ["add", "src/Fixture.cs"]);
            RunGit(root, ["commit", "--quiet", "-m", "base fixture"]);
            RunGit(root, ["update-ref", "refs/remotes/origin/main", "HEAD"]);
            File.WriteAllText(
                sourcePath,
                """
                namespace Fixture;
                public sealed class CoverageFixture
                {
                    public void CommittedChange(int value) { }
                    public void TrackedWorktreeChange() { }
                }
                """);
            RunGit(root, ["add", "src/Fixture.cs"]);
            RunGit(root, ["commit", "--quiet", "-m", "committed fixture change"]);
            File.WriteAllText(
                sourcePath,
                """
                namespace Fixture;
                public sealed class CoverageFixture
                {
                    public void CommittedChange(int value) { }
                    public void TrackedWorktreeChange(int value) { }
                }
                """);

            var previousBase = Environment.GetEnvironmentVariable("GITHUB_BASE_REF");
            Environment.SetEnvironmentVariable("GITHUB_BASE_REF", "main");
            try
            {
                var changes = ReadChangedSourceLines(root);
                var absolutePath = Path.GetFullPath(sourcePath);
                if (!changes.Contains(new CoverageLine(absolutePath, 4))
                    || !changes.Contains(new CoverageLine(absolutePath, 5)))
                {
                    throw new ValidationException(
                        "Changed-line discovery omitted committed or tracked worktree source edits.");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITHUB_BASE_REF", previousBase);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void ParseAddedLines(string root, string diff, ISet<CoverageLine> changed)
    {
        string? currentPath = null;
        var nextLine = 0;
        foreach (var line in diff.Split(Environment.NewLine, StringSplitOptions.None))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                var relativePath = line[6..];
                currentPath = relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !IsGeneratedSource(relativePath)
                    ? Path.GetFullPath(Path.Combine(root, relativePath))
                    : null;
                continue;
            }

            var hunk = Regex.Match(line, @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);
            if (hunk.Success)
            {
                nextLine = int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (currentPath is not null && line.Length > 0 && line[0] == '+' && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                changed.Add(new CoverageLine(currentPath, nextLine));
                nextLine++;
            }
            else if (currentPath is not null
                && (line.Length == 0 || line[0] != '-')
                && (line.Length == 0 || line[0] != '\\'))
            {
                nextLine++;
            }
        }
    }

    private static string RunGit(string root, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new ValidationException($"git diff could not identify changed files: {error.Trim()}");
        }

        return output;
    }

    private static string ResolveSourcePath(string root, string fileName, IReadOnlyList<string> sources)
    {
        if (Path.IsPathRooted(fileName))
        {
            return Path.GetFullPath(fileName);
        }

        foreach (var source in sources)
        {
            var candidate = Path.GetFullPath(Path.Combine(source, fileName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(root, fileName));
    }

    private static bool IsGeneratedSource(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseInteger(string? value, string reportPath)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new ValidationException($"Coverage report contains an invalid hit count: {reportPath}.");
    }

    private static void EnforceThreshold(
        string name,
        CoverageMetrics metrics,
        double lineThreshold,
        double branchThreshold,
        bool allowNoLines = true,
        bool allowNoBranches = true)
    {
        if (metrics.Lines.Count == 0)
        {
            if (allowNoLines)
            {
                Console.WriteLine($"{name}: N/A (no coverable lines).");
                return;
            }

            throw new ValidationException($"{name} has no coverable lines; a report cannot be treated as passing.");
        }

        var lineRate = (double)metrics.CoveredLines.Count / metrics.Lines.Count;
        if (lineRate < lineThreshold / 100)
        {
            throw new ValidationException(
                $"{name} line coverage requires {lineThreshold:0}% but measured {Percent(metrics.CoveredLines.Count, metrics.Lines.Count)}.");
        }

        if (metrics.Branches.Count == 0)
        {
            if (!allowNoBranches)
            {
                throw new ValidationException(
                    $"{name} has no branch data; the OpenCover report cannot establish its branch coverage.");
            }

            Console.WriteLine($"{name}: branches N/A (no coverable branches); lines {Percent(metrics.CoveredLines.Count, metrics.Lines.Count)}.");
            return;
        }

        var branchRate = (double)metrics.CoveredBranches.Count / metrics.Branches.Count;
        if (branchRate < branchThreshold / 100)
        {
            throw new ValidationException(
                $"{name} branch coverage requires {branchThreshold:0}% but measured {Percent(metrics.CoveredBranches.Count, metrics.Branches.Count)}.");
        }
    }

    private static string Describe(CoverageMetrics metrics) =>
        metrics.Lines.Count == 0
            ? "N/A (no coverable lines)"
            : $"{Percent(metrics.CoveredLines.Count, metrics.Lines.Count)} lines, " +
              (metrics.Branches.Count == 0
                  ? "branches N/A"
                  : $"{Percent(metrics.CoveredBranches.Count, metrics.Branches.Count)} branches");

    private static string Percent(int covered, int total) =>
        $"{(double)covered / total:P1} ({covered}/{total})";

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    private static void ExpectFailure(Action action)
    {
        try
        {
            action();
        }
        catch (ValidationException)
        {
            return;
        }

        throw new ValidationException("A negative validation fixture unexpectedly passed.");
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

    private sealed class CoverageReportSet
    {
        internal Dictionary<string, CoverageMetrics> Assemblies { get; } = new(StringComparer.Ordinal);
        internal CoverageMetrics AllAssemblies { get; } = new();
        internal HashSet<string> OpenCoverAssemblies { get; } = new(StringComparer.Ordinal);
    }
}

internal static class TestResults
{
    internal static TestCounts ReadTestResults(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new ValidationException($"Test results directory does not exist: {directory}.");
        }

        var reports = Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories).ToArray();
        if (reports.Length == 0)
        {
            throw new ValidationException($"No test discovery report was produced beneath {directory}.");
        }

        var counts = new TestCounts();
        foreach (var report in reports)
        {
            counts += VerifyDiscoveryXml(LoadXml(report), report);
        }

        if (counts.Total == 0)
        {
            throw new ValidationException($"No tests were discovered beneath {directory}.");
        }

        if (counts.Failed > 0)
        {
            throw new ValidationException($"{counts.Failed} test(s) failed beneath {directory}.");
        }

        return counts;
    }

    internal static void VerifyDiscovery(string report)
    {
        var counts = VerifyDiscoveryXml(LoadXml(report), report);
        if (counts.Total == 0)
        {
            throw new ValidationException($"No tests were discovered in report {report}.");
        }
    }

    internal static void VerifyExpectedBrowserFailure(string report)
    {
        var document = LoadXml(report);
        var failedProbe = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult")
            .Any(element =>
                ((string?)element.Attribute("testName"))?.Contains(
                    "DeliberatelyIncorrectBrowserAssertionFails",
                    StringComparison.Ordinal) == true
                && ((string?)element.Attribute("outcome")) == "Failed"
                && element.Descendants().Any(message =>
                    message.Name.LocalName == "Message"
                    && message.Value.Contains("This title must never match.", StringComparison.Ordinal)));

        if (!failedProbe)
        {
            throw new ValidationException(
                "The browser gate probe did not report its deliberate Playwright assertion as failed.");
        }
    }

    internal static TestCounts VerifyDiscoveryXml(XDocument document, string source)
    {
        var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
        var total = ParseCount(counters?.Attribute("total")?.Value, source, "total");
        var passed = ParseCount(counters?.Attribute("passed")?.Value, source, "passed");
        var failed = ParseCount(counters?.Attribute("failed")?.Value, source, "failed");
        if (counters is null)
        {
            throw new ValidationException($"Test report has no discovery counters: {source}.");
        }

        if (total == 0)
        {
            throw new ValidationException($"Test report contains zero discovered tests: {source}.");
        }

        return new TestCounts(total, passed, failed);
    }

    private static int ParseCount(string? value, string source, string name)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        if (value is null)
        {
            return 0;
        }

        throw new ValidationException($"Test report has an invalid {name} count: {source}.");
    }

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }
}

internal readonly record struct TestCounts(int Total, int Passed, int Failed)
{
    public static TestCounts operator +(TestCounts left, TestCounts right) =>
        new(left.Total + right.Total, left.Passed + right.Passed, left.Failed + right.Failed);
}

internal static class DocumentationLinks
{
    private static readonly Regex MarkdownLink = new(@"\[[^\]]*\]\((?<target>[^)]*)\)", RegexOptions.CultureInvariant);

    internal static void VerifyInternalLinks()
    {
        var root = FindRepositoryRoot();
        var missing = new List<string>();
        foreach (var markdownFile in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                     .Where(path =>
                         !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var content = File.ReadAllText(markdownFile);
            foreach (Match match in MarkdownLink.Matches(content))
            {
                var target = match.Groups["target"].Value.Trim();
                if (target.Length == 0 || target.StartsWith('#'))
                {
                    continue;
                }

                var destination = target.Split(' ', 2)[0].Trim('<', '>');
                if (Uri.TryCreate(destination, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https" or "mailto")
                {
                    continue;
                }

                var localPath = Uri.UnescapeDataString(destination.Split('#', 2)[0].Split('?', 2)[0]);
                if (localPath.Length == 0)
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(markdownFile)!, localPath));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    missing.Add($"{Path.GetRelativePath(root, markdownFile)} -> {destination}");
                }
            }
        }

        if (missing.Count > 0)
        {
            throw new ValidationException($"Broken internal Markdown links:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
        }

        Console.WriteLine("All internal Markdown file links resolve.");
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
}
