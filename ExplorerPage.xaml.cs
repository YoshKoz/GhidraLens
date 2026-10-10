using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class ExplorerPage : Page
{
    private List<Row> _all = [];
    private string _endpoint = "";
    private CancellationTokenSource? _detailCts;

    public ObservableCollection<Row> Visible { get; } = [];

    public ExplorerPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _endpoint = (string)e.Parameter;
        Busy.IsActive = true;
        try
        {
            _all = await MainWindow.Client.ListAsync(_endpoint);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            DetailText.SetCode($"Could not load {_endpoint}: {ex.Message}\n\nOpen a program on the Program page.");
        }
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

    private async void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not Row row) return;
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        if (row.Address is null || row.Address.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase))
        {
            DetailText.SetCode(row.Text);
            return;
        }
        DetailText.SetCode("Loading references...");
        try
        {
            var json = await MainWindow.Client.GetAsync($"get_xrefs_to?address={Uri.EscapeDataString(GhidraClient.Hex(row.Address))}&limit=500", _detailCts.Token);
            DetailText.SetCode($"{row.Text}\n\nReferences to {row.Address}:\n{GhidraClient.Pretty(json)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DetailText.SetCode($"{row.Text}\n\n{ex.Message}"); }
    }

    private void ItemsList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is Row { Address: { } a } && !a.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase))
            MainWindow.Instance?.ShowFunctions(a);
    }
}
