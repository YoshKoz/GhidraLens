using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class StaticMappingsPage : Page
{
    private string? _lastStatic;

    public StaticMappingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = RefreshAsync();

    private static async Task<string> Safe(Func<Task<string>> f)
    {
        try { return await f(); } catch (Exception ex) { return ex.Message; }
    }

    private async Task RefreshAsync() =>
        ModulesText.Text = GhidraClient.Pretty(await Safe(() => MainWindow.Client.GetAsync("debugger/modules")));

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async void ToDynamic_Click(object sender, RoutedEventArgs e)
    {
        if (StaticAddr.Text.Trim() is { Length: > 0 } a)
            ResultText.Text = GhidraClient.Pretty(await Safe(() => MainWindow.Client.GetAsync($"debugger/static_to_dynamic?address={GhidraClient.Hex(a)}")));
    }

    private async void ToStatic_Click(object sender, RoutedEventArgs e)
    {
        if (DynamicAddr.Text.Trim() is not { Length: > 0 } a) return;
        var json = await Safe(() => MainWindow.Client.GetAsync($"debugger/dynamic_to_static?address={GhidraClient.Hex(a)}"));
        ResultText.Text = GhidraClient.Pretty(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var key in new[] { "static_address", "address", "static" })
                if (doc.RootElement.TryGetProperty(key, out var v)) { _lastStatic = v.ToString(); break; }
        }
        catch (JsonException) { }
    }

    private void OpenStatic_Click(object sender, RoutedEventArgs e)
    {
        if (_lastStatic is not null) MainWindow.Instance?.ShowCodeBrowser(_lastStatic);
    }
}
