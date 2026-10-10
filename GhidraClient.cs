using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhidraLens;

public sealed class Row
{
    public Row(string text, string? address) { Text = text; Address = address; }
    public string Text { get; set; }
    public string? Address { get; set; }
}

public sealed class ToolParam
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "string";
    [JsonPropertyName("source")] public string Source { get; set; } = "query";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("default")] public string? Default { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class ToolDef
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "other";
    [JsonPropertyName("category_description")] public string? CategoryDescription { get; set; }
    [JsonPropertyName("params")] public List<ToolParam> Params { get; set; } = [];

    public string Name => Path.TrimStart('/');
}

public sealed class SchemaRoot
{
    [JsonPropertyName("tools")] public List<ToolDef> Tools { get; set; } = [];
}

public sealed class GhidraClient
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public int Port { get; set; } = 8089;
    public List<ToolDef> Tools { get; private set; } = [];

    private string Url(string path) => $"http://127.0.0.1:{Port}/{path.TrimStart('/')}";

    public async Task<string> GetAsync(string path, CancellationToken ct = default) =>
        await _http.GetStringAsync(Url(path), ct);

    public async Task<string> PostAsync(string path, object body, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(Url(path), content, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<(bool Ok, string Message)> PingAsync()
    {
        try { return (true, await GetAsync("check_connection")); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task LoadSchemaAsync()
    {
        var json = await GetAsync("mcp/schema");
        Tools = JsonSerializer.Deserialize<SchemaRoot>(json)?.Tools ?? [];
    }

    public async Task<string> CallAsync(ToolDef tool, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        var query = new List<string>();
        var body = new Dictionary<string, object?>();
        foreach (var p in tool.Params)
        {
            if (!values.TryGetValue(p.Name, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            if (p.Source == "body") body[p.Name] = ConvertValue(p.Type, raw);
            else query.Add($"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(raw)}");
        }
        var path = tool.Path + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        return tool.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            ? await PostAsync(path, body, ct)
            : await GetAsync(path, ct);
    }

    private static object? ConvertValue(string type, string raw) => type switch
    {
        "integer" when long.TryParse(raw, out var l) => l,
        "number" when double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        "boolean" when bool.TryParse(raw, out var b) => b,
        "array" or "object" => JsonSerializer.Deserialize<JsonElement>(raw),
        _ => raw,
    };

    public static string Pretty(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "decompiled", "disassembly", "code" })
                    if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString()!.Replace("\r\n", "\n");
            }
            return JsonSerializer.Serialize(root, Indented);
        }
        catch (JsonException) { return text; }
    }

    public async Task<List<Row>> ListAsync(string endpoint, CancellationToken ct = default)
    {
        var sep = endpoint.Contains('?') ? '&' : '?';
        var json = await GetAsync($"{endpoint}{sep}offset=0&limit=50000", ct);
        return ParseRows(json);
    }

    public static List<Row> ParseRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            throw new InvalidOperationException(err.ToString());

        var rows = new List<Row>();
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
        if (array.ValueKind != JsonValueKind.Array) return rows;
        foreach (var item in array.EnumerateArray())
        {
            string? Str(string k) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(k, out var v) ? v.ToString() : null;
            var addr = Str("address") ?? Str("from_address") ?? Str("start");
            var text = Str("name") ?? Str("value") ?? Str("path") ?? Str("function") ?? item.ToString();
            rows.Add(new Row(addr is null ? text : $"{text}   {addr}", addr));
        }
        return rows;
    }

    public static string Hex(string address) =>
        address.Contains(':') || address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address : "0x" + address;
}
