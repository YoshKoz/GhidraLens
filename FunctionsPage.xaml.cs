using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class FunctionsPage : Page
{
    public static string? PendingAddress { get; set; }
    public static string? PendingFilter { get; set; }

    public bool HasSelection => _current?.Address is not null;

    private List<Row> _all = [];
    private Row? _current;
    private CancellationTokenSource? _cts;

    public ObservableCollection<Row> Visible { get; } = [];

    public FunctionsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        Busy.IsActive = true;
        try
        {
            _all = await MainWindow.Client.ListAsync("list_functions");
            if (PendingFilter is { } pf) { PendingFilter = null; FilterBox.Text = pf; }
            ApplyFilter();
            if (PendingAddress is { } a)
            {
                PendingAddress = null;
                await SelectAddressAsync(a);
            }
        }
        catch (Exception ex) { DetailText.SetCode($"Could not load functions: {ex.Message}\n\nOpen a program on the Program page."); }
        finally { Busy.IsActive = false; }
    }

    private void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        var rows = f.Length == 0 ? _all : _all.Where(r => r.Text.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Visible.Clear();
        foreach (var r in rows.Take(3000)) Visible.Add(r);
        CountText.Text = $"{rows.Count:N0} of {_all.Count:N0}";
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is Row row) { _current = row; _ = ShowAsync(); }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = ShowAsync();

    private string CurrentName => _current?.Text.Split("   ")[0] ?? "";

    private async Task SelectAddressAsync(string address)
    {
        var hit = _all.FirstOrDefault(r => string.Equals(r.Address, address.Replace("0x", ""), StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            FilterBox.Text = "";
            ApplyFilter();
            ItemsList.SelectedItem = hit;
            ItemsList.ScrollIntoView(hit);
            return;
        }
        var json = await MainWindow.Client.GetAsync($"get_function_by_address?address={GhidraClient.Hex(address)}");
        _current = new Row(GhidraClient.Pretty(json).Split('\n').FirstOrDefault() ?? address, address.Replace("0x", ""));
        await ShowAsync();
    }

    private async Task ShowAsync()
    {
        if (_current?.Address is not { } addr) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var a = Uri.EscapeDataString(GhidraClient.Hex(addr));
        var tab = (Tabs.SelectedItem as TabViewItem)?.Tag as string ?? "decompile";
        var path = tab switch
        {
            "disassemble" => $"disassemble_function?address={a}",
            "xrefs_to" => $"get_xrefs_to?address={a}&limit=500",
            "xrefs_from" => $"get_xrefs_from?address={a}&limit=500",
            "callers" => $"get_function_callers?address={a}&limit=500",
            "callees" => $"get_function_callees?address={a}&limit=500",
            "variables" => $"get_function_variables?address={a}",
            "info" => $"get_function_by_address?address={a}",
            _ => $"decompile_function?address={a}",
        };
        Busy.IsActive = true;
        DetailText.SetCode("Loading...");
        try
        {
            var json = await MainWindow.Client.GetAsync(path, ct);
            if (tab == "disassemble" && GhidraClient.AsListing(json) is { } listing) DetailText.SetCode(listing, CodeLang.Asm);
            else DetailText.SetCode(GhidraClient.Pretty(json));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DetailText.SetCode($"{path}\n\n{ex.Message}"); }
        finally { if (!ct.IsCancellationRequested) Busy.IsActive = false; }
    }

    private static Task<string?> AskAsync(string title, string label, string initial = "") =>
        MainWindow.Instance!.AskAsync(title, label, initial, title == "Comment");

    private async Task RunAsync(Func<Task<string>> action, bool reload)
    {
        try
        {
            DetailText.SetCode(GhidraClient.Pretty(await action()));
            if (reload)
            {
                var addr = _current?.Address;
                _all = await MainWindow.Client.ListAsync("list_functions");
                ApplyFilter();
                if (addr is not null) await SelectAddressAsync(addr);
            }
        }
        catch (Exception ex) { DetailText.SetCode(ex.Message); }
    }

    public async Task RenameAsync()
    {
        if (_current is null) return;
        if (await AskAsync("Rename function", "New name", CurrentName) is { Length: > 0 } name)
            await RunAsync(() => MainWindow.Client.PostAsync("rename_function", new { old_name = CurrentName, new_name = name }), true);
    }

    public async Task CommentAsync()
    {
        if (_current?.Address is not { } addr) return;
        if (await AskAsync("Comment", "Plate comment at function entry") is { Length: > 0 } text)
            await RunAsync(() => MainWindow.Client.PostAsync("set_comment", new { address = GhidraClient.Hex(addr), comment = text, type = "plate" }), false);
    }

    public async Task TagAsync()
    {
        if (_current?.Address is not { } addr) return;
        if (await AskAsync("Tag function", "Comma-separated tags") is { Length: > 0 } tags)
            await RunAsync(() => MainWindow.Client.PostAsync("add_function_tag", new { function = GhidraClient.Hex(addr), tags }), false);
    }

    public async Task PrototypeAsync()
    {
        if (_current?.Address is not { } addr) return;
        if (await AskAsync("Set prototype", "C prototype, e.g. int __stdcall foo(char *s)") is { Length: > 0 } proto)
            await RunAsync(() => MainWindow.Client.PostAsync("set_function_prototype", new { function_address = GhidraClient.Hex(addr), prototype = proto }), true);
    }

    public async Task BookmarkAsync()
    {
        if (_current?.Address is not { } addr) return;
        if (await AskAsync("Add bookmark", "Bookmark comment") is { } text)
            await RunAsync(() => MainWindow.Client.PostAsync("set_bookmark", new { address = GhidraClient.Hex(addr), category = "Analysis", comment = text }), false);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e) => await RenameAsync();
    private async void Comment_Click(object sender, RoutedEventArgs e) => await CommentAsync();
    private async void Tag_Click(object sender, RoutedEventArgs e) => await TagAsync();
    private async void Prototype_Click(object sender, RoutedEventArgs e) => await PrototypeAsync();

    private async void Goto_Click(object sender, RoutedEventArgs e)
    {
        if (await AskAsync("Go to address", "Address (hex)") is { Length: > 0 } addr)
            await SelectAddressAsync(addr.Trim());
    }

    private async void Save_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => MainWindow.Client.GetAsync("save_program"), false);
}