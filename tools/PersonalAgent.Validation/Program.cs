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

    internal CoverageMetrics SelectFile(string path)
    {
        var result = new CoverageMetrics();
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var line in Lines.Where(line => string.Equals(line.File, fullPath, comparison)))
        {
            result.AddLine(line, CoveredLines.Contains(line));
        }

        var branchPrefix = fullPath + ":";
        foreach (var branch in Branches.Where(branch => branch.StartsWith(branchPrefix, comparison)))
        {
            result.AddBranch(branch, CoveredBranches.Contains(branch));
        }

        return result;
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
    private const string CriticalJournalSource = "src/PersonalAgent.Infrastructure/Persistence/SqliteJournalStore.cs";

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

        var criticalJournal = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            criticalJournal.AddLine(new CoverageLine("critical-journal.cs", line), covered: true);
        }

        for (var branch = 1; branch <= 20; branch++)
        {
            criticalJournal.AddBranch($"critical-journal.cs:1:{branch}", covered: true);
        }

        EnforceThreshold("critical journal passing fixture", criticalJournal, 95, 90, allowNoLines: false);
        var lowCriticalLine = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            lowCriticalLine.AddLine(new CoverageLine("critical-journal.cs", line), line <= 94);
        }

        for (var branch = 1; branch <= 20; branch++)
        {
            lowCriticalLine.AddBranch($"critical-journal.cs:1:{branch}", covered: true);
        }

        ExpectFailure(() => EnforceThreshold(
            "critical journal low-line fixture", lowCriticalLine, 95, 90, allowNoLines: false));
        var lowCriticalBranch = new CoverageMetrics();
        for (var line = 1; line <= 100; line++)
        {
            lowCriticalBranch.AddLine(new CoverageLine("critical-journal.cs", line), covered: true);
        }

        for (var branch = 1; branch <= 20; branch++)
        {
            lowCriticalBranch.AddBranch($"critical-journal.cs:1:{branch}", branch <= 17);
        }

        ExpectFailure(() => EnforceThreshold(
            "critical journal low-branch fixture", lowCriticalBranch, 95, 90, allowNoLines: false));
        ExpectFailure(() => RequireReports([], "missing report fixture"));
        ExpectFailure(() => TestResults.VerifyDiscoveryXml(
            XDocument.Parse("<TestRun><ResultSummary><Counters total=\"0\" executed=\"0\" passed=\"0\" failed=\"0\" /></ResultSummary></TestRun>"),
            "missing discovery fixture"));
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
        var root = FindRepositoryRoot();
        var unit = MergeReports(root, unitReports);
        var combined = MergeReports(root, unitReports.Concat(integrationReports));
        var runtimeAssemblies = RuntimeAssemblies
            .Where(name => name != "PersonalAgent.Infrastructure" || HasHandwrittenSources(root, name))
            .ToArray();

        if (!unit.Assemblies.ContainsKey("PersonalAgent.Domain")
            || !unit.Assemblies.ContainsKey("PersonalAgent.Application"))
        {
            throw new ValidationException("Unit coverage is missing an expected Domain or Application assembly report.");
        }

        var missingAssemblies = runtimeAssemblies
            .Where(name => !combined.Assemblies.ContainsKey(name))
            .ToArray();
        if (missingAssemblies.Length > 0)
        {
            throw new ValidationException(
                $"Coverage reports are missing expected runtime assemblies: {string.Join(", ", missingAssemblies)}.");
        }

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
        var criticalJournal = combined.Assemblies["PersonalAgent.Infrastructure"]
            .SelectFile(Path.Combine(root, CriticalJournalSource));
        EnforceThreshold("critical approval/action/audit journal", criticalJournal, 95, 90, allowNoLines: false);
        EnforceChangedLineThreshold(root, combined.AllAssemblies);

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

    private static IReadOnlyList<string> RequireReports(IEnumerable<string> reportPaths, string layer)
    {
        var reports = reportPaths
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reports.Length == 0)
        {
            throw new ValidationException($"The {layer} run produced no expected Cobertura coverage report.");
        }

        return reports;
    }

    private static CoverageReportSet MergeReports(string root, IEnumerable<string> reportPaths)
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
                    name => packageName.Equals(name, StringComparison.Ordinal)
                        || packageName.StartsWith($"{name} ", StringComparison.Ordinal));
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
                    AddBranches(metrics, result.AllAssemblies, line, lineElement, reportPath);
                }
            }
        }

        return result;
    }

    private static void AddBranches(
        CoverageMetrics assembly,
        CoverageMetrics allAssemblies,
        CoverageLine line,
        XElement lineElement,
        string reportPath)
    {
        var conditions = lineElement.Element("conditions")?.Elements("condition").ToArray() ?? [];
        if (conditions.Length > 0)
        {
            foreach (var condition in conditions)
            {
                var index = (string?)condition.Attribute("number") ?? "unknown";
                var coverage = (string?)condition.Attribute("coverage") ?? "0%";
                var covered = double.TryParse(
                    coverage.TrimEnd('%'),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var percent) && percent >= 100;
                var key = $"{line.File}:{line.Number}:{index}";
                assembly.AddBranch(key, covered);
                allAssemblies.AddBranch(key, covered);
            }

            return;
        }

        var coverageText = (string?)lineElement.Attribute("condition-coverage");
        if (coverageText is null)
        {
            return;
        }

        var match = Regex.Match(coverageText, @"\((\d+)/(\d+)\)", RegexOptions.CultureInvariant);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var coveredCount)
            || !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var totalCount))
        {
            throw new ValidationException($"Coverage report contains invalid branch counts: {reportPath}.");
        }

        for (var branch = 0; branch < totalCount; branch++)
        {
            var key = $"{line.File}:{line.Number}:{branch}";
            var covered = branch < coveredCount;
            assembly.AddBranch(key, covered);
            allAssemblies.AddBranch(key, covered);
        }
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
        bool allowNoLines = true)
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
