using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GhidraLens;

public sealed partial class PcodeStepperPage : Page
{
    public static string? PendingAddress { get; set; }

    private readonly List<string> _details = [];
    private readonly Dictionary<string, string> _instructions = [];

    public ObservableCollection<Row> Ops { get; } = [];

    public PcodeStepperPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (PendingAddress is { } a) { PendingAddress = null; FunctionBox.Text = a; await LoadAsync(); }
    }

    private static string Varnode(JsonElement v)
    {
        var space = v.GetProperty("space").GetString();
        var off = v.GetProperty("offset").GetString();
        var size = v.GetProperty("size").GetInt32();
        return space switch
        {
            "const" => $"0x{off}:{size}",
            "register" => $"reg[{off}]:{size}",
            _ => $"{space}[{off}]:{size}",
        };
    }

    private async Task LoadAsync()
    {
        var addr = FunctionBox.Text.Trim();
        if (addr.Length == 0) return;
        Ops.Clear(); _details.Clear(); _instructions.Clear();
        OpDetail.SetCode("Loading...");
        var c = MainWindow.Client;
        try
        {
            var gran = (string)Granularity.SelectedItem;
            using var doc = JsonDocument.Parse(await c.GetAsync($"get_function_pcode?function_address={GhidraClient.Hex(addr)}&granularity={gran}"));
            if (doc.RootElement.TryGetProperty("error", out var err)) { OpDetail.SetCode(err.ToString()); return; }

            try
            {
                using var dis = JsonDocument.Parse(await c.GetAsync($"disassemble_function?address={GhidraClient.Hex(addr)}"));
                foreach (var i in dis.RootElement.GetProperty("instructions").EnumerateArray())
                    _instructions[i.GetProperty("address").ToString()] = i.GetProperty("instruction").ToString();
            }
            catch { }

            foreach (var block in doc.RootElement.GetProperty("basic_blocks").EnumerateArray())
            {
                var bstart = block.GetProperty("start").GetProperty("address").ToString();
                foreach (var op in block.GetProperty("pcodes").EnumerateArray())
                {
                    var seq = op.GetProperty("seq").GetProperty("address").ToString();
                    var mnem = op.GetProperty("mnemonic").GetString();
                    var inputs = op.TryGetProperty("inputs", out var ins) ? string.Join(", ", ins.EnumerateArray().Select(Varnode)) : "";
                    var output = op.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Object ? Varnode(o) + " = " : "";
                    Ops.Add(new Row($"{seq}  [{bstart}]  {output}{mnem} {inputs}", seq));
                    _details.Add(JsonSerializer.Serialize(op, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            if (Ops.Count > 0) OpsView.SelectedIndex = 0;
            else OpDetail.SetCode("No P-code returned.");
        }
        catch (Exception ex) { OpDetail.SetCode(ex.Message); }
    }

    private void Ops_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = OpsView.SelectedIndex;
        if (i < 0 || i >= _details.Count) return;
        var row = Ops[i];
        var instr = row.Address is { } a && _instructions.TryGetValue(a, out var s) ? s : "?";
        PositionText.Text = $"op {i + 1} / {Ops.Count}";
        OpDetail.SetCode($"Instruction @ {row.Address}:  {instr}\n\n{_details[i]}");
        OpsView.ScrollIntoView(row);
    }

    private void Move(int delta)
    {
        if (Ops.Count == 0) return;
        OpsView.SelectedIndex = Math.Clamp(OpsView.SelectedIndex + delta, 0, Ops.Count - 1);
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Reset_Click(object sender, RoutedEventArgs e) => Move(-Ops.Count);
    private void Step_Click(object sender, RoutedEventArgs e) => Move(1);
    private void StepBack_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void StepInstruction_Click(object sender, RoutedEventArgs e)
    {
        var i = OpsView.SelectedIndex;
        if (i < 0) return;
        var current = Ops[i].Address;
        var next = Enumerable.Range(i + 1, Ops.Count - i - 1).FirstOrDefault(j => Ops[j].Address != current, -1);
        if (next >= 0) OpsView.SelectedIndex = next;
    }

    private async void Emulate_Click(object sender, RoutedEventArgs e)
    {
        var tool = MainWindow.Client.Tools.FirstOrDefault(t => t.Path == "/emulate_function");
        if (tool is null) { EmuResult.SetCode("emulate_function endpoint not available."); return; }
        var values = new Dictionary<string, string>
        {
            ["address"] = GhidraClient.Hex(FunctionBox.Text.Trim()),
            ["registers"] = RegistersBox.Text,
            ["memory"] = MemoryBox.Text,
            ["max_steps"] = ((int)MaxSteps.Value).ToString(),
            ["return_registers"] = ReturnRegs.Text,
        };
        EmuResult.SetCode("Emulating...");
        try { EmuResult.SetCode(GhidraClient.Pretty(await MainWindow.Client.CallAsync(tool, values))); }
        catch (Exception ex) { EmuResult.SetCode(ex.Message); }
    }
}
