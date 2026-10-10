using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class CodeBrowserPage : Page
{
    private static readonly Regex OperandAddr = new(@"0x([0-9a-fA-F]{6,16})", RegexOptions.Compiled);
    private static readonly List<string> History = [];
    private static int _historyIndex = -1;
    public static string? PendingAddress { get; set; }

    private string? _functionEntry;

    public ObservableCollection<Row> Listing { get; } = [];

    public CodeBrowserPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var start = PendingAddress ?? (_historyIndex >= 0 ? History[_historyIndex] : null);
        PendingAddress = null;
        if (start is null)
        {
            try
            {
                using var doc = JsonDocument.Parse(await MainWindow.Client.GetAsync("get_entry_points"));
                start = FindFirstAddress(doc.RootElement);
            }
            catch { }
        }
        if (start is not null) await GoAsync(start, record: _historyIndex < 0 || History[_historyIndex] != start);
    }

    private static string? FindFirstAddress(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object when e.TryGetProperty("address", out var a) => a.ToString(),
        JsonValueKind.Object => e.EnumerateObject().Select(p => FindFirstAddress(p.Value)).FirstOrDefault(x => x is not null),
        JsonValueKind.Array => e.EnumerateArray().Select(FindFirstAddress).FirstOrDefault(x => x is not null),
        _ => null,
    };

    public async Task GoAsync(string address, bool record = true)
    {
        address = address.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (record)
        {
            if (_historyIndex < History.Count - 1) History.RemoveRange(_historyIndex + 1, History.Count - _historyIndex - 1);
            History.Add(address);
            _historyIndex = History.Count - 1;
        }
        AddressBox.Text = address;
        Busy.IsActive = true;
        var c = MainWindow.Client;
        var hex = GhidraClient.Hex(address);
        try
        {
            using var fn = JsonDocument.Parse(await c.GetAsync($"get_function_by_address?address={hex}"));
            var entry = fn.RootElement.TryGetProperty("address", out var ea) ? ea.ToString() : null;
            var name = fn.RootElement.TryGetProperty("name", out var n) ? n.ToString() : null;
            FunctionTitle.Text = name is null ? "(no function)" : $"{name} @ {entry}";

            if (entry is not null && entry != _functionEntry)
            {
                _functionEntry = entry;
                Listing.Clear();
                using var dis = JsonDocument.Parse(await c.GetAsync($"disassemble_function?address={GhidraClient.Hex(entry)}"));
                if (dis.RootElement.TryGetProperty("instructions", out var ins))
                    foreach (var i in ins.EnumerateArray())
                    {
                        var a = i.GetProperty("address").ToString();
                        Listing.Add(new Row($"{a}   {i.GetProperty("instruction")}", a));
                    }
                DecompileText.SetCode(GhidraClient.Pretty(await c.GetAsync($"decompile_function?address={GhidraClient.Hex(entry)}")));
            }
            else if (entry is null)
            {
                _functionEntry = null;
                Listing.Clear();
                DecompileText.SetCode(GhidraClient.Pretty(fn.RootElement.GetRawText()));
            }

            var hit = Listing.FirstOrDefault(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) { ListingView.SelectedItem = hit; ListingView.ScrollIntoView(hit, ScrollIntoViewAlignment.Leading); }
            await ShowBytesAsync(address);
        }
        catch (Exception ex) { DecompileText.SetCode(ex.Message); }
        finally { Busy.IsActive = false; }
    }

    private async Task ShowBytesAsync(string address)
    {
        try
        {
            using var doc = JsonDocument.Parse(await MainWindow.Client.GetAsync($"read_memory?address={GhidraClient.Hex(address)}&length=256"));
            if (!doc.RootElement.TryGetProperty("data", out var data)) { HexText.Text = doc.RootElement.GetRawText(); return; }
            var bytes = data.EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
            var baseAddr = Convert.ToUInt64(address.Split(':').Last(), 16);
            var sb = new StringBuilder();
            for (var off = 0; off < bytes.Length; off += 16)
            {
                var line = bytes.Skip(off).Take(16).ToArray();
                sb.Append($"{baseAddr + (ulong)off:x8}  ");
                sb.Append(string.Join(' ', line.Select(b => b.ToString("x2"))).PadRight(48));
                sb.Append("  ");
                sb.AppendLine(new string(line.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray()));
            }
            BytesTitle.Text = $"Bytes @ {address}";
            HexText.Text = sb.ToString();
        }
        catch (Exception ex) { HexText.Text = ex.Message; }
    }

    private async void Listing_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListingView.SelectedItem is Row { Address: { } a }) { AddressBox.Text = a; await ShowBytesAsync(a); }
    }

    private async void Listing_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ListingView.SelectedItem is Row row && OperandAddr.Match(row.Text.Split("   ", 2).Last()) is { Success: true } m)
            await GoAsync(m.Groups[1].Value);
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await GoAsync(AddressBox.Text);

    private async void AddressBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await GoAsync(AddressBox.Text);
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex > 0) await GoAsync(History[--_historyIndex], record: false);
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex < History.Count - 1) await GoAsync(History[++_historyIndex], record: false);
    }
}
