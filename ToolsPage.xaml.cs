using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class ToolsPage : Page
{
    private List<ToolDef> _tools = [];
    private ToolDef? _tool;
    private readonly Dictionary<string, Func<string>> _inputs = [];

    public ObservableCollection<ToolDef> Visible { get; } = [];

    public ToolsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var category = (string)e.Parameter;
        _tools = MainWindow.Client.Tools.Where(t => t.Category == category).OrderBy(t => t.Name).ToList();
        CategoryTitle.Text = _tools.FirstOrDefault()?.CategoryDescription ?? category;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        Visible.Clear();
        foreach (var t in _tools.Where(t => f.Length == 0 || t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            Visible.Add(t);
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolDef tool) return;
        _tool = tool;
        ToolTitle.Text = tool.Name;
        ToolDescription.Text = $"{tool.Method} {tool.Path}\n\n{tool.Description}";
        FormPanel.Children.Clear();
        _inputs.Clear();

        foreach (var p in tool.Params.OrderByDescending(p => p.Required))
        {
            var header = $"{p.Name}{(p.Required ? " *" : "")}  ({p.Type})";
            FrameworkElement control;
            if (p.Type == "boolean")
            {
                var cb = new CheckBox { Content = header, IsChecked = p.Default == "true" ? true : p.Default == "false" ? false : null, IsThreeState = !p.Required };
                _inputs[p.Name] = () => cb.IsChecked switch { true => "true", false => "false", _ => "" };
                control = cb;
            }
            else
            {
                var multiline = p.Type is "array" or "object";
                var tb = new TextBox
                {
                    Header = header,
                    PlaceholderText = p.Default ?? "",
                    AcceptsReturn = multiline,
                    TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MinHeight = multiline ? 80 : 0,
                    FontFamily = multiline ? new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") : null,
                };
                _inputs[p.Name] = () => tb.Text;
                control = tb;
            }
            if (!string.IsNullOrWhiteSpace(p.Description))
                ToolTipService.SetToolTip(control, p.Description);

            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(control);
            if (!string.IsNullOrWhiteSpace(p.Description))
                panel.Children.Add(new TextBlock { Text = p.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 });
            FormPanel.Children.Add(panel);
        }
        RunButton.IsEnabled = true;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_tool is null) return;
        var values = _inputs.ToDictionary(kv => kv.Key, kv => kv.Value());
        var missing = _tool.Params.Where(p => p.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Name))).Select(p => p.Name).ToList();
        if (missing.Count > 0) { ResultText.Text = $"Missing required: {string.Join(", ", missing)}"; return; }

        Busy.IsActive = true;
        RunButton.IsEnabled = false;
        try { ResultText.Text = GhidraClient.Pretty(await MainWindow.Client.CallAsync(_tool, values)); }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { Busy.IsActive = false; RunButton.IsEnabled = true; }
    }
}
