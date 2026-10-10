using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace GhidraLens;

public sealed partial class MainWindow : Window
{
    public static GhidraClient Client { get; } = new();
    public static MainWindow? Current { get; private set; }

    private string? _programName;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "Ghidra Lens";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "GhidraLens.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1700, 1000));
        _ = ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        Client.Port = (int)PortBox.Value;
        var (ok, message) = await Client.PingAsync();
        var hasProgram = ok && message.Contains("with program", StringComparison.OrdinalIgnoreCase);
        _programName = hasProgram ? message.Replace("Connected: GhidraMCP plugin running with program ", "").Trim('\'', ' ') : null;
        StatusDot.Fill = new SolidColorBrush(hasProgram ? Microsoft.UI.Colors.LimeGreen : ok ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.OrangeRed);
        StatusText.Text = _programName ?? (ok ? "Ghidra connected, no program loaded" : $"No Ghidra on :{Client.Port}");
        Title = _programName is null ? "Ghidra Lens" : $"{_programName} - Ghidra Lens";

        if (ok && Client.Tools.Count == 0)
        {
            try { await Client.LoadSchemaAsync(); BuildToolsMenus(); }
            catch (Exception ex) { StatusText.Text += $" (schema failed: {ex.Message})"; }
        }

        if (Nav.SelectedItem is null)
            Nav.SelectedItem = hasProgram ? FunctionsItem : ProgramItem;
        else
            Navigate(Nav.SelectedItem as NavigationViewItem);
    }

    private void BuildToolsMenus()
    {
        ToolsRoot.MenuItems.Clear();
        while (ToolsMenu.Items.Count > 2) ToolsMenu.Items.RemoveAt(2);
        foreach (var g in Client.Tools.GroupBy(t => t.Category).OrderBy(g => g.Key))
        {
            var label = $"{char.ToUpper(g.Key[0])}{g.Key[1..]} ({g.Count()})";
            ToolsRoot.MenuItems.Add(new NavigationViewItem { Content = label, Tag = "tools:" + g.Key, Icon = new FontIcon { Glyph = "" } });
            var item = new MenuFlyoutItem { Text = label, Tag = g.Key };
            item.Click += (_, _) => ShowTools((string)item.Tag);
            ToolsMenu.Items.Add(item);
        }
        ToolsRoot.Content = $"Tools ({Client.Tools.Count})";
    }

    private void ShowTools(string category)
    {
        var nav = ToolsRoot.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == "tools:" + category);
        if (nav is not null) { ToolsRoot.IsExpanded = true; Nav.SelectedItem = nav; }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) =>
        Navigate(args.SelectedItem as NavigationViewItem);

    public void ShowFunctions(string? address = null, string? filter = null)
    {
        FunctionsPage.PendingAddress = address;
        FunctionsPage.PendingFilter = filter;
        if (Nav.SelectedItem == FunctionsItem) ContentFrame.Navigate(typeof(FunctionsPage));
        else Nav.SelectedItem = FunctionsItem;
    }

    public void ShowCodeBrowser(string address)
    {
        CodeBrowserPage.PendingAddress = address;
        if (Nav.SelectedItem == CodeBrowserItem) ContentFrame.Navigate(typeof(CodeBrowserPage));
        else Nav.SelectedItem = CodeBrowserItem;
    }

    private void Window_Click(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = ((MenuFlyoutItem)sender).Tag switch
        {
            "codebrowser" => CodeBrowserItem,
            "functions" => FunctionsItem,
            "time" => TimeItem,
            "mappings" => MappingsItem,
            "pcode" => PcodeItem,
            _ => Nav.SelectedItem,
        };
    }

    private void Navigate(NavigationViewItem? item)
    {
        if (item?.Tag is not string tag || !tag.Contains(':')) return;
        var i = tag.IndexOf(':');
        var (kind, arg) = (tag[..i], tag[(i + 1)..]);
        switch (kind)
        {
            case "page" when arg == "program": ContentFrame.Navigate(typeof(ProgramPage)); break;
            case "page" when arg == "functions": ContentFrame.Navigate(typeof(FunctionsPage)); break;
            case "page" when arg == "codebrowser": ContentFrame.Navigate(typeof(CodeBrowserPage)); break;
            case "page" when arg == "time": ContentFrame.Navigate(typeof(TimePage)); break;
            case "page" when arg == "mappings": ContentFrame.Navigate(typeof(StaticMappingsPage)); break;
            case "page" when arg == "pcode": ContentFrame.Navigate(typeof(PcodeStepperPage)); break;
            case "list": ContentFrame.Navigate(typeof(ExplorerPage), arg); break;
            case "tools": ContentFrame.Navigate(typeof(ToolsPage), arg); break;
        }
    }

    public async Task<string?> AskAsync(string title, string label, string initial = "", bool multiline = false)
    {
        var box = new TextBox { Header = label, Text = initial, FontSize = 18, AcceptsReturn = multiline, MinWidth = 480, MinHeight = multiline ? 120 : 0, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        var dlg = new ContentDialog
        {
            Title = title, Content = box, PrimaryButtonText = "OK", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public async Task ShowResultAsync(string title, string text)
    {
        var dlg = new ContentDialog
        {
            Title = title, CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
            Content = new ScrollViewer
            {
                MaxHeight = 640, MinWidth = 720, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = GhidraClient.Pretty(text), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 15, IsTextSelectionEnabled = true },
            },
        };
        dlg.Resources["ContentDialogMaxWidth"] = 1400d;
        await dlg.ShowAsync();
    }

    private async Task RunAsync(string title, Func<Task<string>> call, bool reconnect = false)
    {
        string result;
        try { result = await call(); }
        catch (Exception ex) { result = ex.Message; }
        if (reconnect) await ConnectAsync();
        await ShowResultAsync(title, result);
    }

    private async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private void OpenProgram_Click(object sender, RoutedEventArgs e) => Nav.SelectedItem = ProgramItem;
    private void ProjectFiles_Click(object sender, RoutedEventArgs e) => Nav.SelectedItem = ProgramItem;

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("*") is not { } path) return;
        await RunAsync("Import file", () => Client.PostAsync("import_file", new { file_path = path, auto_analyze = true }), true);
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync(".gpr") is not { } path) return;
        await RunAsync("Open project", () => Client.PostAsync("open_project", new { path }), true);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync("Save", () => Client.GetAsync("save_program"));
    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await RunAsync("Save all", () => Client.GetAsync("save_all_programs"));

    private async void CloseProgram_Click(object sender, RoutedEventArgs e)
    {
        if (_programName is null) return;
        await RunAsync("Close program", () => Client.PostAsync("close_program", new { name = _programName, save = true }), true);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Rename_Click(object sender, RoutedEventArgs e) { if (await EnsureFunctionsAsync() is { } p) await p.RenameAsync(); }
    private async void Comment_Click(object sender, RoutedEventArgs e) { if (await EnsureFunctionsAsync() is { } p) await p.CommentAsync(); }
    private async void Prototype_Click(object sender, RoutedEventArgs e) { if (await EnsureFunctionsAsync() is { } p) await p.PrototypeAsync(); }
    private async void Tag_Click(object sender, RoutedEventArgs e) { if (await EnsureFunctionsAsync() is { } p) await p.TagAsync(); }
    private async void Bookmark_Click(object sender, RoutedEventArgs e) { if (await EnsureFunctionsAsync() is { } p) await p.BookmarkAsync(); }

    private async void Goto_Click(object sender, RoutedEventArgs e)
    {
        if (await AskAsync("Go to address", "Address (hex)") is { Length: > 0 } addr) ShowFunctions(addr);
    }

    private async Task<FunctionsPage?> EnsureFunctionsAsync()
    {
        if (ContentFrame.Content is FunctionsPage p && p.HasSelection) return p;
        await ShowResultAsync("No function selected", "Select a function on the Functions page first.");
        return null;
    }

    private async void SearchFunctions_Click(object sender, RoutedEventArgs e)
    {
        if (await AskAsync("Find function", "Name contains") is { Length: > 0 } q) ShowFunctions(filter: q);
    }

    private async void SearchStrings_Click(object sender, RoutedEventArgs e)
    {
        if (await AskAsync("Search strings", "Text") is { Length: > 0 } q)
            await RunAsync($"Strings matching \"{q}\"", () => Client.GetAsync($"search_strings?search_term={Uri.EscapeDataString(q)}&limit=500"));
    }

    private async void SearchInstructions_Click(object sender, RoutedEventArgs e)
    {
        if (await AskAsync("Search instructions", "Mnemonic (e.g. CALL, XOR)") is { Length: > 0 } q)
            await RunAsync($"Instructions: {q}", () => Client.GetAsync($"search_instructions?mnemonic={Uri.EscapeDataString(q)}&limit=500"));
    }

    private async void SearchBytes_Click(object sender, RoutedEventArgs e)
    {
        if (await AskAsync("Search byte pattern", "Hex bytes (e.g. 55 8B EC)") is { Length: > 0 } q)
            await RunAsync($"Bytes: {q}", () => Client.GetAsync($"search_byte_patterns?pattern={Uri.EscapeDataString(q)}"));
    }

    private async void OpenPrograms_Click(object sender, RoutedEventArgs e) => await RunAsync("Open programs", () => Client.GetAsync("list_open_programs"));
    private async void Bookmarks_Click(object sender, RoutedEventArgs e) => await RunAsync("Bookmarks", () => Client.GetAsync("list_bookmarks"));
    private async void Metadata_Click(object sender, RoutedEventArgs e) => await RunAsync("Program metadata", () => Client.GetAsync("get_metadata"));

    private async void RunAnalysis_Click(object sender, RoutedEventArgs e) => await RunAsync("Auto analyze", () => Client.PostAsync("run_analysis", new { }));
    private async void Reanalyze_Click(object sender, RoutedEventArgs e) => await RunAsync("Re-analyze", () => Client.PostAsync("reanalyze", new { }));
    private async void AnalysisStatus_Click(object sender, RoutedEventArgs e) => await RunAsync("Analysis status", () => Client.GetAsync("analysis_status"));
    private async void Analyzers_Click(object sender, RoutedEventArgs e) => await RunAsync("Analyzers", () => Client.GetAsync("list_analyzers"));

    private async void GhidraDocs_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://ghidra-sre.org/"));

    private async void PluginDocs_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/bethington/ghidra-mcp"));

    private async void Version_Click(object sender, RoutedEventArgs e) => await RunAsync("Version", () => Client.GetAsync("get_version"));

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var tools = Client.Tools.Count;
        await ShowResultAsync("About Ghidra Lens",
            JsonSerializer.Serialize(new { app = "Ghidra Lens", ui = "WinUI 3 / Windows App SDK 1.8", backend = $"GhidraMCP on 127.0.0.1:{Client.Port}", endpoints = tools, program = _programName }));
    }
}
