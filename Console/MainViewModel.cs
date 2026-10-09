using System.Net.Http;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using MCPPOC.Console.Models;
using MCPPOC.Console.Services;

namespace MCPPOC.Console;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _repoRoot;
    private readonly ProfileStore _profileStore = new();
    private readonly ServerProcessManager _server = new();
    private readonly InstallerRunner _installer;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _statusPoll;
    private McpProbe? _probe;

    public MainViewModel()
    {
        _repoRoot = FindRepoRoot();
        _installer = new InstallerRunner(_repoRoot);
        _installer.OutputReceived += line => AddLog(LogLevel.Status, "install", line);

        Profiles = new ObservableCollection<ServerProfile>(_profileStore.Load());
        _selectedProfile = Profiles[0];

        LogEntries = new ObservableCollection<LogEntry>();
        LogView = CollectionViewSource.GetDefaultView(LogEntries);
        LogView.Filter = FilterLog;

        Assemblies = new ObservableCollection<AssemblyRow>();

        _server.LogReceived += OnServerLog;
        _server.StateChanged += () => Application.Current?.Dispatcher.Invoke(OnServerStateChanged);
        _server.Exited += () => Application.Current?.Dispatcher.Invoke(() => AddLog(LogLevel.Status, "console", "Server process exited."));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(Uptime));
        _clock.Start();

        _statusPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusPoll.Tick += async (_, _) => { if (_server.IsRunning) await RefreshStatusAsync(); };
        _statusPoll.Start();

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_server.IsRunning);
        StopCommand = new RelayCommand(() => _server.Stop(), () => _server.IsRunning);
        RestartCommand = new AsyncRelayCommand(async () => { _server.Stop(); await Task.Delay(400); await StartAsync(); }, () => _server.IsRunning);
        SelfTestCommand = new AsyncRelayCommand(SelfTestAsync, () => _server.IsRunning);
        CopyEndpointCommand = new RelayCommand(() => { if (Endpoint != null) Clipboard.SetText(Endpoint); }, () => Endpoint != null);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync, () => _server.IsRunning);

        NewProfileCommand = new RelayCommand(NewProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => Profiles.Count > 1);

        ClearLogCommand = new RelayCommand(() => LogEntries.Clear());
        SaveLogCommand = new RelayCommand(SaveLog);
        ClearProblemsCommand = new RelayCommand(() => { var toRemove = LogEntries.Where(e => e.Level is LogLevel.Error or LogLevel.Warning).ToList(); foreach (var e in toRemove) LogEntries.Remove(e); });
        CopyProblemsCommand = new RelayCommand(() => Clipboard.SetText(string.Join(Environment.NewLine, Problems.Select(p => $"{p.TimeText} {p.Source} {p.Message}"))));

        BuildCommand = new AsyncRelayCommand(() => RunBuildAsync());
        TestCommand = new AsyncRelayCommand(() => RunToolAsync("dotnet", $"test \"{Path.Combine(_repoRoot, "mcp-dotnet.slnx")}\" -c Release --no-build", "test"));
        PackDistCommand = new AsyncRelayCommand(() => RunToolAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_repoRoot, "Scripts", "pack-dist.ps1")}\"", "pack-dist"));
        PackNuGetCommand = new AsyncRelayCommand(() => RunToolAsync("dotnet", $"pack \"{Path.Combine(_repoRoot, "src", "MCPPOC", "MCPPOC.csproj")}\" -c Release -o \"{Path.Combine(_repoRoot, "Distribution")}\"", "pack"));

        RegisterClaudeCodeCommand = new AsyncRelayCommand(() => RegisterAsync("claude-code"));
        RegisterClaudeDesktopCommand = new AsyncRelayCommand(() => RegisterAsync("claude-desktop"));
        RegisterCodexCommand = new AsyncRelayCommand(() => RegisterAsync("codex"));
        RegisterAllCommand = new AsyncRelayCommand(() => RegisterAsync("all"));
        CheckRegistrationCommand = new RelayCommand(RefreshRegistrationStatus);

        BinaryPath = ServerProcessManager.FindServerDll(_repoRoot) ?? "(not built yet - use Maintenance > Build)";
        RefreshRegistrationStatus();
        AddLog(LogLevel.Status, "console", $"mcp-dotNET console - repository: {_repoRoot}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        var d = new DirectoryInfo(dir);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "mcp-dotnet.slnx")))
            d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }

    // ---------------------------------------------------------------- profiles

    public ObservableCollection<ServerProfile> Profiles { get; }

    private ServerProfile _selectedProfile;
    public ServerProfile SelectedProfile
    {
        get => _selectedProfile;
        set { _selectedProfile = value; OnPropertyChanged(); SaveProfiles(); }
    }

    private void SaveProfiles() => _profileStore.Save(Profiles);

    private void NewProfile()
    {
        var name = "Profile " + (Profiles.Count + 1);
        var p = new ServerProfile { Name = name, Port = SelectedProfile.Port + 1 };
        Profiles.Add(p);
        SelectedProfile = p;
        SaveProfiles();
    }

    private void DeleteProfile()
    {
        if (Profiles.Count <= 1) return;
        var toRemove = SelectedProfile;
        var idx = Profiles.IndexOf(toRemove);
        Profiles.Remove(toRemove);
        SelectedProfile = Profiles[Math.Max(0, idx - 1)];
        SaveProfiles();
    }

    // ---------------------------------------------------------------- server state

    public string BinaryPath { get; }
    public string StateText => _server.State.ToString().ToUpperInvariant();
    public Services.ServerState State => _server.State;
    public string? Endpoint => _server.Endpoint;
    public string? Token => _server.Token;
    public int? Pid => _server.Pid;
    public string Uptime => _server.StartedAt is { } t ? (DateTime.Now - t).ToString(@"hh\:mm\:ss") : "-";
    public bool IsRunning => _server.IsRunning;

    public int Requests { get; private set; }
    public int Warnings { get; private set; }
    public int Errors { get; private set; }
    public string LastSelfTest { get; private set; } = "-";
    public string RunningJob { get; private set; } = "-";

    private void OnServerStateChanged()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(Token));
        OnPropertyChanged(nameof(Pid));
        OnPropertyChanged(nameof(IsRunning));
        if (!_server.IsRunning) { Assemblies.Clear(); _probe?.Dispose(); _probe = null; }
    }

    private async Task StartAsync()
    {
        if (string.IsNullOrEmpty(BinaryPath) || !File.Exists(BinaryPath))
        {
            AddLog(LogLevel.Error, "console", "Server binary not found. Run Maintenance > Build first.");
            return;
        }
        Requests = Warnings = Errors = 0;
        _server.Start(BinaryPath, SelectedProfile);
        OnServerStateChanged();
        OnPropertyChanged(nameof(Requests)); OnPropertyChanged(nameof(Warnings)); OnPropertyChanged(nameof(Errors));
        await Task.Delay(1200);
        if (_server.IsRunning && _server.Endpoint != null)
        {
            _probe = new McpProbe(_server.Endpoint, _server.Token);
            await SelfTestAsync();
            await RefreshStatusAsync();
        }
    }

    private async Task SelfTestAsync()
    {
        if (_server.Endpoint == null) return;
        _probe ??= new McpProbe(_server.Endpoint, _server.Token);
        var (ok, message, version) = await _probe.SelfTestAsync();
        LastSelfTest = $"{DateTime.Now:HH:mm:ss} - {(ok ? "OK" : "FAILED")}: {message}" + (version != null ? $" (v{version})" : "");
        OnPropertyChanged(nameof(LastSelfTest));
        AddLog(ok ? LogLevel.Status : LogLevel.Error, "self-test", message);
    }

    private async Task RefreshStatusAsync()
    {
        if (_probe == null) return;
        try
        {
            var rows = await _probe.ListAssembliesAsync();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Assemblies.Clear();
                foreach (var r in rows) Assemblies.Add(r);
            });
        }
        catch (HttpRequestException) { /* server not reachable right now; next poll retries */ }
        catch (TaskCanceledException) { }
    }

    private void OnServerLog(LogEntry entry)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            AddLogEntry(entry);
            if (entry.Level == LogLevel.Warning) { Warnings++; OnPropertyChanged(nameof(Warnings)); }
            else if (entry.Level == LogLevel.Error) { Errors++; OnPropertyChanged(nameof(Errors)); }
            else if (entry.Level == LogLevel.Request) { Requests++; OnPropertyChanged(nameof(Requests)); }
        });
    }

    private void AddLog(LogLevel level, string source, string message) =>
        Application.Current?.Dispatcher.Invoke(() => AddLogEntry(new LogEntry(DateTime.Now, level, source, message)));

    private void AddLogEntry(LogEntry entry)
    {
        LogEntries.Add(entry);
        if (LogEntries.Count > 5000) LogEntries.RemoveAt(0);
        if (AutoScroll) LogScrollRequested?.Invoke();
    }

    public event Action? LogScrollRequested;

    // ---------------------------------------------------------------- log filtering

    public ObservableCollection<LogEntry> LogEntries { get; }
    public ICollectionView LogView { get; }
    public IEnumerable<LogEntry> Problems => LogEntries.Where(e => e.Level is LogLevel.Error or LogLevel.Warning);

    private bool _showError = true, _showWarning = true, _showInfo = true, _showRequest = true, _showDebug;
    public bool ShowError { get => _showError; set { _showError = value; OnPropertyChanged(); LogView.Refresh(); } }
    public bool ShowWarning { get => _showWarning; set { _showWarning = value; OnPropertyChanged(); LogView.Refresh(); } }
    public bool ShowInfo { get => _showInfo; set { _showInfo = value; OnPropertyChanged(); LogView.Refresh(); } }
    public bool ShowRequest { get => _showRequest; set { _showRequest = value; OnPropertyChanged(); LogView.Refresh(); } }
    public bool ShowDebug { get => _showDebug; set { _showDebug = value; OnPropertyChanged(); LogView.Refresh(); } }

    private string _searchText = "";
    public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); LogView.Refresh(); } }

    public bool AutoScroll { get; set; } = true;
    public bool WrapLog { get; set; }

    private bool FilterLog(object obj)
    {
        if (obj is not LogEntry e) return false;
        var levelOk = e.Level switch
        {
            LogLevel.Error => ShowError,
            LogLevel.Warning => ShowWarning,
            LogLevel.Request => ShowRequest,
            LogLevel.Debug => ShowDebug,
            _ => ShowInfo,
        };
        if (!levelOk) return false;
        return string.IsNullOrEmpty(SearchText) || e.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"mcp-dotnet-console-{DateTime.Now:yyyyMMdd-HHmmss}.log", Filter = "Log files (*.log)|*.log|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
            File.WriteAllLines(dlg.FileName, LogEntries.Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Level}] {e.Source}: {e.Message}"));
    }

    // ---------------------------------------------------------------- workspace

    public ObservableCollection<AssemblyRow> Assemblies { get; }

    // ---------------------------------------------------------------- maintenance

    private async Task RunToolAsync(string exe, string args, string jobName)
    {
        RunningJob = jobName; OnPropertyChanged(nameof(RunningJob));
        IsBusy = true; OnPropertyChanged(nameof(IsBusy));
        AddLog(LogLevel.Status, jobName, $"$ {exe} {args}");
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = args,
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) AddLog(LogLevel.Status, jobName, e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) AddLog(LogLevel.Status, jobName, e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        AddLog(p.ExitCode == 0 ? LogLevel.Status : LogLevel.Error, jobName, $"exit code {p.ExitCode}");
        RunningJob = "-"; OnPropertyChanged(nameof(RunningJob));
        IsBusy = false; OnPropertyChanged(nameof(IsBusy));
    }

    // Build, then flatten bin/ into the clean, distribution-shaped layout (one level, DLL/EXE only)
    // that ServerProcessManager.FindServerDll and every launch script expect.
    private async Task RunBuildAsync()
    {
        await RunToolAsync("dotnet", $"build \"{Path.Combine(_repoRoot, "mcp-dotnet.slnx")}\" -c Release", "build");
        await RunToolAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_repoRoot, "Scripts", "flatten-bin.ps1")}\"", "build");
    }

    public bool IsBusy { get; private set; }

    // ---------------------------------------------------------------- integration

    public bool ClaudeCodeRegistered { get; private set; }
    public bool ClaudeDesktopRegistered { get; private set; }
    public bool CodexRegistered { get; private set; }
    public bool ClaudeDesktopRunning { get; private set; }
    public bool IntegrationReadOnly { get; set; }

    private void RefreshRegistrationStatus()
    {
        var (cc, cd, cx) = InstallerRunner.CheckRegistration();
        ClaudeCodeRegistered = cc; ClaudeDesktopRegistered = cd; CodexRegistered = cx;
        ClaudeDesktopRunning = InstallerRunner.IsClaudeDesktopRunning();
        OnPropertyChanged(nameof(ClaudeCodeRegistered));
        OnPropertyChanged(nameof(ClaudeDesktopRegistered));
        OnPropertyChanged(nameof(CodexRegistered));
        OnPropertyChanged(nameof(ClaudeDesktopRunning));
    }

    private async Task RegisterAsync(string target)
    {
        RunningJob = $"register ({target})"; OnPropertyChanged(nameof(RunningJob));
        IsBusy = true; OnPropertyChanged(nameof(IsBusy));
        if (ClaudeDesktopRunning && (target is "claude-desktop" or "all"))
            AddLog(LogLevel.Warning, "install", "Claude Desktop is running: it may overwrite its config with its in-memory copy. Fully quit it first for a guaranteed update.");
        var code = await _installer.RegisterAsync(target, IntegrationReadOnly);
        AddLog(code == 0 ? LogLevel.Status : LogLevel.Error, "install", $"install.ps1 -Target {target} exit code {code}");
        RefreshRegistrationStatus();
        RunningJob = "-"; OnPropertyChanged(nameof(RunningJob));
        IsBusy = false; OnPropertyChanged(nameof(IsBusy));
    }

    // ---------------------------------------------------------------- commands

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand SelfTestCommand { get; }
    public ICommand CopyEndpointCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SaveLogCommand { get; }
    public ICommand ClearProblemsCommand { get; }
    public ICommand CopyProblemsCommand { get; }
    public ICommand BuildCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand PackDistCommand { get; }
    public ICommand PackNuGetCommand { get; }
    public ICommand RegisterClaudeCodeCommand { get; }
    public ICommand RegisterClaudeDesktopCommand { get; }
    public ICommand RegisterCodexCommand { get; }
    public ICommand RegisterAllCommand { get; }
    public ICommand CheckRegistrationCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _clock.Stop();
        _statusPoll.Stop();
        _server.Dispose();
        _probe?.Dispose();
    }
}
