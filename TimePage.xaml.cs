using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace GhidraLens;

public sealed partial class TimePage : Page
{
    public TimePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = RefreshAsync();

    private static async Task<string> Safe(Func<Task<string>> f)
    {
        try { return GhidraClient.Pretty(await f()); } catch (Exception ex) { return ex.Message; }
    }

    private async Task RefreshAsync()
    {
        var c = MainWindow.Client;
        var status = await Safe(() => c.GetAsync("debugger/status"));
        StatusText.Text = string.Join(" ", status.Split(['\r', '\n', '{', '}'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        TracesText.SetCode(await Safe(() => c.GetAsync("debugger/traces")));
        RegistersText.SetCode(await Safe(() => c.GetAsync("debugger/registers")));
        StackText.SetCode(await Safe(() => c.GetAsync("debugger/stack_trace?depth=64")));
        BreakpointsText.SetCode(await Safe(() => c.GetAsync("debugger/list_breakpoints")));
    }

    private async Task ActAsync(string path, object? body = null)
    {
        ActionText.SetCode(await Safe(() => MainWindow.Client.PostAsync(path, body ?? new { })));
        await RefreshAsync();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!));
        picker.FileTypeFilter.Add(".exe");
        if (await picker.PickSingleFileAsync() is not { } file) return;
        var args = await MainWindow.Instance!.AskAsync("Launch", "Command-line arguments (optional)") ?? "";
        ActionText.SetCode($"Launching {file.Path}...");
        var s = MainWindow.Host.Settings;
        await ActAsync("debugger/launch", new
        {
            executable_path = file.Path, args, cwd = Path.GetDirectoryName(file.Path),
            offer = s.DebuggerOffer, python_executable = s.DebuggerPython, timeout_seconds = 90,
        });
    }

    private async void Resume_Click(object sender, RoutedEventArgs e) => await ActAsync("debugger/resume");
    private async void Interrupt_Click(object sender, RoutedEventArgs e) => await ActAsync("debugger/interrupt");
    private async void StepInto_Click(object sender, RoutedEventArgs e) => await ActAsync("debugger/step_into");
    private async void StepOver_Click(object sender, RoutedEventArgs e) => await ActAsync("debugger/step_over");
    private async void StepOut_Click(object sender, RoutedEventArgs e) => await ActAsync("debugger/step_out");

    private async void SetBp_Click(object sender, RoutedEventArgs e)
    {
        if (BreakAddr.Text.Trim() is { Length: > 0 } a) await ActAsync("debugger/set_breakpoint", new { address = GhidraClient.Hex(a) });
    }

    private async void RemoveBp_Click(object sender, RoutedEventArgs e)
    {
        if (BreakAddr.Text.Trim() is { Length: > 0 } a) await ActAsync("debugger/remove_breakpoint", new { address = GhidraClient.Hex(a) });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();
}
