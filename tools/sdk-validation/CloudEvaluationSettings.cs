internal sealed record CloudEvaluationSettings(string BaseUrl, string Model, string ApiKey, string? WireApi)
{
    public override string ToString() => "Cloud evaluation settings (credential redacted)";
    public static CloudEvaluationSettings Load(string path)
    {
        var fileValues = File.Exists(path)
            ? Parse(File.ReadAllLines(path))
            : new Dictionary<string, string>();
        string? Get(string name) =>
            Environment.GetEnvironmentVariable(name) ??
            (fileValues.TryGetValue(name, out var value) ? value : null);

        var baseUrl = Get("JARVIS_CLOUD_BASE_URL");
        var model = Get("JARVIS_CLOUD_MODEL");
        var apiKey = Get("JARVIS_CLOUD_API_KEY");
        var wireApi = Get("JARVIS_CLOUD_WIRE_API");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Configure JARVIS_CLOUD_BASE_URL, JARVIS_CLOUD_MODEL, and JARVIS_CLOUD_API_KEY in the environment or .env.cloud-evaluation.");
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new InvalidOperationException("The cloud API base URL must be HTTPS without embedded credentials, query, or fragment.");
        }
        if (wireApi is not null && wireApi is not ("responses" or "chat-completions"))
        {
            throw new InvalidOperationException("JARVIS_CLOUD_WIRE_API must be 'responses' or 'chat-completions'.");
        }
        return new(baseUrl, model, apiKey, wireApi);
    }

    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator < 1)
            {
                throw new InvalidOperationException($"Invalid evaluation configuration at line {lineNumber}.");
            }
            var name = line[..separator].Trim();
            if (name is not ("JARVIS_CLOUD_BASE_URL" or "JARVIS_CLOUD_MODEL" or
                "JARVIS_CLOUD_API_KEY" or "JARVIS_CLOUD_WIRE_API"))
            {
                throw new InvalidOperationException($"Unsupported evaluation setting at line {lineNumber}.");
            }
            var value = line[(separator + 1)..].Trim();
            if (value.Length > 0 && value[0] is '\'' or '"')
            {
                if (value.Length < 2 || value[^1] != value[0])
                {
                    throw new InvalidOperationException($"Unterminated evaluation value at line {lineNumber}.");
                }
                value = value[1..^1];
            }
            if (!values.TryAdd(name, value))
            {
                throw new InvalidOperationException($"Duplicate evaluation setting at line {lineNumber}.");
            }
        }
        return values;
    }

    internal static void ValidateParser()
    {
        var values = Parse(["# comment", "", "JARVIS_CLOUD_API_KEY='test=key'",
            "JARVIS_CLOUD_MODEL=\"test-model\"", "JARVIS_CLOUD_WIRE_API=responses"]);
        if (values["JARVIS_CLOUD_API_KEY"] != "test=key" ||
            values["JARVIS_CLOUD_MODEL"] != "test-model")
        {
            throw new InvalidOperationException("Evaluation configuration quoting contract failed.");
        }
        foreach (var invalid in new[]
        {
            new[] { "JARVIS_CLOUD_API_KEY='unterminated" },
            new[] { "export JARVIS_CLOUD_API_KEY=test" },
            new[] { "JARVIS_CLOUD_MODEL=a", "JARVIS_CLOUD_MODEL=b" },
        })
        {
            try
            {
                Parse(invalid);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            throw new InvalidOperationException("Invalid evaluation configuration was accepted.");
        }
        var literal = Parse(["JARVIS_CLOUD_API_KEY=$(must-not-execute)"]);
        if (literal["JARVIS_CLOUD_API_KEY"] != "$(must-not-execute)")
        {
            throw new InvalidOperationException("Evaluation configuration must never expand shell expressions.");
        }
        Console.WriteLine("PASS evaluation settings parse quoted/literal values and reject malformed or duplicate settings without shell execution.");
    }
}
