using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class ProgramPage : Page
{
    public ObservableCollection<Row> Files { get; } = [];

    public ProgramPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var c = MainWindow.Client;
        Files.Clear();
        try
        {
            var json = await c.GetAsync("list_project_files?folder=/");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("project_name", out var pn)) ProjectTitle.Text = $"Project: {pn}";
            if (doc.RootElement.TryGetProperty("files", out var files))
                foreach (var f in files.EnumerateArray())
                    Files.Add(new Row(f.GetProperty("path").GetString() ?? "", null));

            var info = await SafeAsync(() => c.GetAsync("get_current_program_info"));
            var open = await SafeAsync(() => c.GetAsync("list_open_programs"));
            var meta = await SafeAsync(() => c.GetAsync("get_metadata"));
            InfoText.Text = $"Current program\n{GhidraClient.Pretty(info)}\n\nOpen programs\n{GhidraClient.Pretty(open)}\n\nMetadata\n{GhidraClient.Pretty(meta)}";
        }
        catch (Exception ex) { InfoText.Text = $"Could not read project: {ex.Message}"; }
    }

    private static async Task<string> SafeAsync(Func<Task<string>> f)
    {
        try { return await f(); } catch (Exception ex) { return ex.Message; }
    }

    private async Task OpenSelectedAsync()
    {
        if (FilesList.SelectedItem is not Row row) return;
        InfoText.Text = $"Opening {row.Text}...";
        var auto = AutoAnalyze.IsChecked == true ? "true" : "false";
        var result = await SafeAsync(() => MainWindow.Client.GetAsync($"open_program?path={Uri.EscapeDataString(row.Text)}&auto_analyze={auto}"));
        InfoText.Text = GhidraClient.Pretty(result);
        if (MainWindow.Current is { } w) await w.ConnectAsync();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _ = OpenSelectedAsync();
    private void FilesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => _ = OpenSelectedAsync();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async void Save_Click(object sender, RoutedEventArgs e) =>
        InfoText.Text = GhidraClient.Pretty(await SafeAsync(() => MainWindow.Client.GetAsync("save_program")));
}
