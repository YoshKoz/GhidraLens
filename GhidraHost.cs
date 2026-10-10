using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GhidraLens;

public sealed class LensSettings
{
    public string GhidraInstallDir { get; set; } = @"C:\Tools\RE\ghidra\ghidra_12.1.4_PUBLIC";
    public bool AutoStartGhidra { get; set; } = true;
    public bool HideGhidraWindows { get; set; } = true;
    public bool StopGhidraOnExit { get; set; } = true;
    public string DebuggerPython { get; set; } = @"C:\Tools\RE\ghidra\dbgeng-venv\Scripts\python.exe";
    public string DebuggerOffer { get; set; } = "dbgeng";
    public string? LastProjectPath { get; set; }
    public string? LastProgramPath { get; set; }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhidraLens", "settings.json");

    public static LensSettings Load()
    {
        try { return JsonSerializer.Deserialize<LensSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

// Runs Ghidra as a background engine so the user only ever launches Ghidra Lens.
public sealed class GhidraHost
{
    private const int SwHide = 0, SwShow = 5;
    private readonly GhidraClient _client;
    private CancellationTokenSource? _hideLoop;

    public LensSettings Settings { get; } = LensSettings.Load();
    public bool StartedByUs { get; private set; }
    public bool WindowsHidden { get; private set; }

    public GhidraHost(GhidraClient client) => _client = client;

    public static Process? FindGhidraProcess() =>
        Process.GetProcessesByName("javaw").Concat(Process.GetProcessesByName("java"))
            .FirstOrDefault(p => { try { return CommandLineOf(p).Contains("ghidra", StringComparison.OrdinalIgnoreCase); } catch { return false; } });

    private static string CommandLineOf(Process p)
    {
        using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={p.Id}");
        return searcher.Get().Cast<System.Management.ManagementObject>().Select(o => o["CommandLine"] as string ?? "").FirstOrDefault() ?? "";
    }

    public async Task<bool> EnsureRunningAsync(IProgress<string> status, CancellationToken ct = default)
    {
        if ((await _client.PingAsync()).Ok) return true;
        if (!Settings.AutoStartGhidra) return false;

        var launcher = Path.Combine(Settings.GhidraInstallDir, "ghidraRun.bat");
        if (!File.Exists(launcher)) { status.Report($"Ghidra not found at {Settings.GhidraInstallDir}"); return false; }

        if (FindGhidraProcess() is null)
        {
            status.Report("Starting Ghidra engine...");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{launcher}\"\"")
            {
                WorkingDirectory = Settings.GhidraInstallDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            StartedByUs = true;
        }
        if (Settings.HideGhidraWindows) StartHideLoop();

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if ((await _client.PingAsync()).Ok) return true;
            await Task.Delay(1500, ct);
        }
        status.Report("Ghidra did not start within 3 minutes");
        return false;
    }

    public async Task RestoreSessionAsync(IProgress<string> status)
    {


        if (Settings.LastProjectPath is { } project && File.Exists(project))
        {
            var files = await SafeGet("list_project_files?folder=/");
            var expectedName = Path.GetFileNameWithoutExtension(project);
            if (!files.Contains($"\"project_name\":\"{expectedName}\""))
            {
                status.Report($"Opening project {expectedName}...");
                await _client.PostAsync("open_project", new { path = project });
            }
        }
        if (Settings.LastProgramPath is { } program)
        {
            status.Report($"Opening {program}...");
            try { await _client.OpenAndActivateAsync(program, autoAnalyze: false); }
            catch (Exception ex) { status.Report($"Could not open {program}: {ex.Message}"); }
        }
    }

    // Ghidra records the open project in its preferences as an escaped path without the .gpr extension.
    private string? LastOpenedProjectFromGhidra()
    {
        var version = Path.GetFileName(Settings.GhidraInstallDir.TrimEnd('\\'));
        var prefs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ghidra", version, "preferences");
        if (!File.Exists(prefs)) return null;
        var line = File.ReadLines(prefs).FirstOrDefault(l => l.StartsWith("LastOpenedProject="));
        if (line is null) return null;
        var path = line["LastOpenedProject=".Length..].Replace("\\:", ":").Replace("\\\\", "\\") + ".gpr";
        return File.Exists(path) ? path : null;
    }

    private async Task<string> SafeGet(string path)
    {
        try { return await _client.GetAsync(path); } catch (Exception ex) { return ex.Message; }
    }

    public void RememberProgram(string programPath)
    {
        if (LastOpenedProjectFromGhidra() is { } project) Settings.LastProjectPath = project;
        Settings.LastProgramPath = programPath;
        Settings.Save();
    }

    public async Task ShutdownAsync()
    {
        _hideLoop?.Cancel();
        if (StartedByUs && Settings.StopGhidraOnExit)
        {
            try { await _client.PostAsync("save_all_programs", new { }); } catch { }
            try { await _client.PostAsync("exit_ghidra", new { }); } catch { }
        }
    }

    public void SetWindowsVisible(bool visible)
    {
        WindowsHidden = !visible;
        if (visible) _hideLoop?.Cancel(); else StartHideLoop();
        ApplyVisibility(visible);
    }

    // Ghidra keeps opening new top-level windows (splash, project manager, CodeBrowser), so keep hiding for a while.
    private void StartHideLoop()
    {
        _hideLoop?.Cancel();
        _hideLoop = new CancellationTokenSource();
        var token = _hideLoop.Token;
        WindowsHidden = true;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                ApplyVisibility(false);
                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
            }
        }, token);
    }

    private static void ApplyVisibility(bool visible)
    {
        if (FindGhidraProcess() is not { } p) return;
        foreach (var hwnd in TopLevelWindows((uint)p.Id))
            ShowWindow(hwnd, visible ? SwShow : SwHide);
    }

    private static List<IntPtr> TopLevelWindows(uint pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid && GetWindowTextLength(h) > 0) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
