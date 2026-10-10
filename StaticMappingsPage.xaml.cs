using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class StaticMappingsPage : Page
{
    private string? _lastStatic;
    private string? _programName;
    private ulong? _imageBase;
    private ulong? _moduleBase;

    public StaticMappingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = RefreshAsync();

    private static async Task<string> Safe(Func<Task<string>> f)
    {
        try { return await f(); } catch (Exception ex) { return ex.Message; }
    }

    private static ulong ParseHex(string s) =>
        ulong.Parse(s.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Split(':').Last(), NumberStyles.HexNumber);

    private async Task RefreshAsync()
    {
        var c = MainWindow.Client;
        var modules = await Safe(() => c.GetAsync("debugger/modules"));
        ModulesText.SetCode(GhidraClient.Pretty(modules));
        _imageBase = null; _moduleBase = null;
        try
        {
            using var info = JsonDocument.Parse(await c.GetAsync("get_current_program_info"));
            _programName = info.RootElement.GetProperty("name").GetString();
            _imageBase = ParseHex(info.RootElement.GetProperty("image_base").GetString()!);
            using var mods = JsonDocument.Parse(modules);
            if (mods.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var m in mods.RootElement.EnumerateArray())
                    if (string.Equals(Path.GetFileName(m.GetProperty("name").GetString()), _programName, StringComparison.OrdinalIgnoreCase))
                        _moduleBase = ParseHex(m.GetProperty("base").GetString()!);
        }
        catch (Exception) { }
        ResultText.SetCode(_moduleBase is { } mb && _imageBase is { } ib
            ? $"{_programName}: static base {ib:x8} -> loaded at {mb:x8} (delta {(long)(mb - ib):+#;-#;0})"
            : "No live module matches the open program. Launch it on the Time page first.");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async void ToDynamic_Click(object sender, RoutedEventArgs e)
    {
        if (StaticAddr.Text.Trim() is not { Length: > 0 } a) return;
        var plugin = await Safe(() => MainWindow.Client.GetAsync($"debugger/static_to_dynamic?address={GhidraClient.Hex(a)}"));
        // The plugin returns the static address unchanged for relocated (ASLR) images, so compute it from the module base.
        var computed = _moduleBase is { } mb && _imageBase is { } ib ? $"{mb + (ParseHex(a) - ib):x8}" : "unknown (no live module)";
        ResultText.SetCode($"dynamic address: {computed}\n\nplugin reply:\n{GhidraClient.Pretty(plugin)}");
    }

    private async void ToStatic_Click(object sender, RoutedEventArgs e)
    {
        if (DynamicAddr.Text.Trim() is not { Length: > 0 } a) return;
        var json = await Safe(() => MainWindow.Client.GetAsync($"debugger/dynamic_to_static?address={GhidraClient.Hex(a)}"));
        _lastStatic = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("static_address", out var sa)) _lastStatic = sa.GetString();
            else if (doc.RootElement.TryGetProperty("offset", out var off) && _imageBase is { } ib
                     && doc.RootElement.TryGetProperty("module", out var mod)
                     && string.Equals(mod.GetString(), _programName, StringComparison.OrdinalIgnoreCase))
                _lastStatic = $"{ib + ParseHex(off.GetString()!):x8}";
        }
        catch (Exception) { }
        if (_lastStatic is null && _moduleBase is { } mb && _imageBase is { } ib2 && ParseHex(a) >= mb)
            _lastStatic = $"{ib2 + (ParseHex(a) - mb):x8}";
        ResultText.SetCode($"static address: {_lastStatic ?? "not in the open program"}\n\nplugin reply:\n{GhidraClient.Pretty(json)}");
    }

    private void OpenStatic_Click(object sender, RoutedEventArgs e)
    {
        if (_lastStatic is not null) MainWindow.Instance?.ShowCodeBrowser(_lastStatic);
    }
}
