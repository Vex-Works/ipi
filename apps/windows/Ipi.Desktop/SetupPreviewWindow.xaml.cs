using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ipi.Desktop.Models;
using Ipi.Desktop.Services;

namespace Ipi.Desktop;

public partial class SetupPreviewWindow
{
    private readonly SetupPreviewViewModel _viewModel;

    public SetupPreviewWindow(bool postInstallMode = false)
    {
        _viewModel = new SetupPreviewViewModel(postInstallMode);
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void SetupPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (WindowState != WindowState.Normal) return;
        var area = SystemParameters.WorkArea;
        Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
        Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
        Activate();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void ShowProgress_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartSetupAsync();
    }

    private async void InstallRuntime_Click(object sender, RoutedEventArgs e) => await _viewModel.InstallRuntimeAsync();

    private void ToggleDetails_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDetails = !_viewModel.ShowDetails;

    private void ShowFailed_Click(object sender, RoutedEventArgs e) => _viewModel.Screen = "failed";

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRuntimeReady) _viewModel.ShowProviderSetup();
    }

    private void StartIpi_Click(object sender, RoutedEventArgs e) => OpenMainWindow();

    private void SkipProvider_Click(object sender, RoutedEventArgs e) => OpenMainWindow();

    private void AddAnotherProvider_Click(object sender, RoutedEventArgs e) => _viewModel.ShowProviderGallery();

    private void ProviderBack_Click(object sender, RoutedEventArgs e) => _viewModel.ShowProviderGallery();

    private void OpenSetupLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _viewModel.SetupLogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(path)) File.WriteAllText(path, "ipi setup log has not been created yet.\n");
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open setup log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ProviderOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SetupProviderOptionItem item }) _viewModel.SelectProviderOption(item);
    }

    private void SaveSetupProvider_Click(object sender, RoutedEventArgs e) => _viewModel.SaveSelectedProvider();

    private void OpenMainWindow()
    {
        if (_viewModel.TryGetInstalledExecutable(out var installedExe) && !IsCurrentExecutable(installedExe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExe,
                    WorkingDirectory = Path.GetDirectoryName(installedExe) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                });
                Close();
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Unable to launch installed ipi", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var mainWindow = new MainWindow();
        Application.Current.MainWindow = mainWindow;
        mainWindow.Show();
        Close();
    }

    private static bool IsCurrentExecutable(string executablePath)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(current), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void BrowseInstallPath_Click(object sender, RoutedEventArgs e)
    {
        var fallback = IpiPathService.LocalAppDataDir;
        var initial = Directory.Exists(_viewModel.InstallPath) ? _viewModel.InstallPath : fallback;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose ipi install location",
            InitialDirectory = initial,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true) _viewModel.InstallPath = dialog.FolderName;
    }
}

public sealed class SetupPreviewViewModel : INotifyPropertyChanged
{
    private PiDataService _pi = new();
    private readonly PiAgentBridgeService _bridge = new();
    private readonly RuntimeBootstrapService _bootstrap = new();
    private readonly List<PiProviderCatalogRecord> _providerCatalog = new();
    private readonly List<PiModelOptionRecord> _registryModels = new();
    private string _screen = "welcome";
    private bool _showDetails;
    private bool _isProviderConfigVisible;
    private string _installPath = IpiPathService.LocalAppDataDir;
    private string _providerSearchText = "";
    private string _providerStatus = "";
    private string _installedExePath = "";
    private string _installError = "";
    private string _bootstrapError = "";
    private readonly List<string> _setupLogLines = new();
    private bool _isInstallingRuntime;
    private bool _continueSetupInInstalledCopy;
    private string _connectedProviderId = "";
    private string _connectedProviderTitle = "No provider connected";
    private string _connectedProviderDetail = "You can start without one, but chat requires a configured model provider.";
    private SetupProviderOptionItem? _selectedProviderOption;
    private string _newProviderId = "";
    private string _newProviderBaseUrl = "";
    private string _newProviderApi = "openai-completions";
    private string _newProviderApiKeyRef = "";
    private string _newProviderModelIds = "";
    private PiRuntimeInfo _runtimeInfo = new PiRuntimeService().Resolve();
    private RuntimeBootstrapInspection _bootstrapInspection = new RuntimeBootstrapService().Inspect();

    public SetupPreviewViewModel(bool postInstallMode = false)
    {
        Steps = new ObservableCollection<SetupStepItem>();
        ProviderOptions = new ObservableCollection<SetupProviderOptionItem>();
        FilteredProviderOptions = new ObservableCollection<SetupProviderOptionItem>();
        if (postInstallMode)
        {
            _installPath = AppContext.BaseDirectory;
            _screen = "progress";
        }
        RefreshSetupPlan();
        RefreshProviderState();
        _ = LoadProviderCatalogAsync();
        _ = LoadRegistryModelsAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Screen
    {
        get => _screen;
        set
        {
            if (_screen == value) return;
            _screen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInstallScreen));
            OnPropertyChanged(nameof(ProviderReadySummaryVisibility));
            OnPropertyChanged(nameof(ProviderGalleryVisibility));
            OnPropertyChanged(nameof(ProviderConfigVisibility));
        }
    }

    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (_showDetails == value) return;
            _showDetails = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailsVisibility));
            OnPropertyChanged(nameof(DetailColumnWidth));
            OnPropertyChanged(nameof(DetailsButtonText));
        }
    }

    public bool IsInstallScreen => Screen == "progress";
    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (_installPath == value) return;
            _installPath = value;
            OnPropertyChanged();
            RefreshSetupPlan();
        }
    }
    public ObservableCollection<SetupStepItem> Steps { get; }
    private bool IsAwaitingRuntimeInstall => IsInstallScreen && !_isInstallingRuntime && !IsRuntimeReady && string.IsNullOrWhiteSpace(_installError) && string.IsNullOrWhiteSpace(_bootstrapError) && _setupLogLines.Count == 0;
    public string CurrentStep => IsAwaitingRuntimeInstall ? "Ready to install runtime" : Steps.FirstOrDefault(step => step.State == "active")?.Label ?? "Runtime ready";
    public string StepCounter
    {
        get
        {
            if (Steps.Count == 0) return "0 checks";
            if (IsAwaitingRuntimeInstall) return "Confirmation required";
            var activeIndex = Steps.ToList().FindIndex(step => step.State == "active");
            var index = activeIndex >= 0 ? activeIndex + 1 : Steps.Count;
            return $"{index} of {Steps.Count} checks";
        }
    }
    public double ProgressPercent => Steps.Count == 0 ? 0 : Steps.Count(step => step.State == "done") * 100d / Steps.Count;
    public Visibility SetupProgressVisibility => IsAwaitingRuntimeInstall ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailsVisibility => ShowDetails ? Visibility.Visible : Visibility.Collapsed;
    public GridLength DetailColumnWidth => ShowDetails ? new GridLength(360) : new GridLength(0);
    public string DetailsButtonText => ShowDetails ? "Hide details" : "Show details";
    public string RuntimeModeLabel => _runtimeInfo.RuntimeMode;
    public string SetupLogPath => _bootstrap.LogPath;
    public bool IsRuntimeReady => Steps.Count > 0 && Steps.All(step => step.State == "done");
    public Visibility ContinueButtonVisibility => IsRuntimeReady ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstallRuntimeButtonVisibility => Visibility.Collapsed;
    public Visibility RuntimeInstallConfirmationVisibility => IsAwaitingRuntimeInstall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuntimeChecklistVisibility => IsAwaitingRuntimeInstall ? Visibility.Collapsed : Visibility.Visible;
    public string RuntimeInstallSource => RuntimeBootstrapService.PiPackageSpec;
    public string RuntimeInstallTarget => ShortPath(_bootstrapInspection.PiCodingAgentRoot);
    public string RuntimeInstallAgentDir => ShortPath(_bootstrapInspection.AgentDir);
    public string CloseButtonText => IsRuntimeReady ? "Close" : "Cancel";
    public string InstalledExePath => string.IsNullOrWhiteSpace(_installedExePath) ? Path.Combine(NormalizeDirectoryPath(InstallPath), "ipi.exe") : _installedExePath;
    public bool ContinueSetupInInstalledCopy => _continueSetupInInstalledCopy;

    public ObservableCollection<SetupProviderOptionItem> ProviderOptions { get; }
    public ObservableCollection<SetupProviderOptionItem> FilteredProviderOptions { get; }
    public string ProviderSearchText
    {
        get => _providerSearchText;
        set
        {
            if (_providerSearchText == value) return;
            _providerSearchText = value;
            OnPropertyChanged();
            RebuildFilteredProviderOptions();
        }
    }
    public string ProviderStatus { get => _providerStatus; private set { if (_providerStatus == value) return; _providerStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProviderStatusVisibility)); } }
    public Visibility ProviderStatusVisibility => string.IsNullOrWhiteSpace(ProviderStatus) ? Visibility.Collapsed : Visibility.Visible;
    public string ConnectedProviderId => _connectedProviderId;
    public string ConnectedProviderTitle => _connectedProviderTitle;
    public string ConnectedProviderDetail => _connectedProviderDetail;
    public bool HasConnectedProvider => !string.IsNullOrWhiteSpace(_connectedProviderId);
    public Visibility ProviderReadySummaryVisibility => Screen == "provider" && HasConnectedProvider && !_isProviderConfigVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProviderGalleryVisibility => Screen == "provider" && !_isProviderConfigVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProviderConfigVisibility => Screen == "provider" && _isProviderConfigVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StartIpiVisibility => HasConnectedProvider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddAnotherProviderVisibility => HasConnectedProvider && !_isProviderConfigVisible ? Visibility.Visible : Visibility.Collapsed;
    public string ProviderConfigTitle => _selectedProviderOption is null ? "Provider config" : $"Configure {_selectedProviderOption.Title}";
    public string NewProviderId { get => _newProviderId; set { if (_newProviderId == value) return; _newProviderId = value; OnPropertyChanged(); } }
    public string NewProviderBaseUrl { get => _newProviderBaseUrl; set { if (_newProviderBaseUrl == value) return; _newProviderBaseUrl = value; OnPropertyChanged(); } }
    public string NewProviderApi { get => _newProviderApi; set { if (_newProviderApi == value) return; _newProviderApi = value; OnPropertyChanged(); } }
    public string NewProviderApiKeyRef { get => _newProviderApiKeyRef; set { if (_newProviderApiKeyRef == value) return; _newProviderApiKeyRef = value; OnPropertyChanged(); } }
    public string NewProviderModelIds { get => _newProviderModelIds; set { if (_newProviderModelIds == value) return; _newProviderModelIds = value; OnPropertyChanged(); } }
    public Visibility NewProviderModelsVisibility => _selectedProviderOption is { IsCatalogProvider: true } ? Visibility.Collapsed : Visibility.Visible;

    public string LiveOutput => string.Join('\n', BuildLiveOutputLines());

    public async Task StartSetupAsync()
    {
        Screen = "progress";
        ShowDetails = false;
        _installError = "";
        _bootstrapError = "";
        _continueSetupInInstalledCopy = false;
        RefreshSetupPlan();
        await Task.Yield();
        await InstallApplicationFilesAsync();
        RefreshSetupPlan();
    }

    public async Task InstallRuntimeAsync()
    {
        Screen = "progress";
        _bootstrapError = "";
        _setupLogLines.Clear();
        _isInstallingRuntime = true;
        RefreshSetupPlan();
        await BootstrapRuntimeAsync();
        _isInstallingRuntime = false;
        RefreshSetupPlan();
        if (!IsRuntimeReady && !string.IsNullOrWhiteSpace(_bootstrapError)) Screen = "failed";
    }

    public void ShowProviderSetup()
    {
        RefreshProviderState();
        _isProviderConfigVisible = false;
        ProviderStatus = "";
        Screen = "provider";
        NotifyProviderStateChanged();
    }

    public void ShowProviderGallery()
    {
        _isProviderConfigVisible = false;
        ProviderStatus = "";
        foreach (var option in ProviderOptions) option.IsSelected = false;
        NotifyProviderStateChanged();
    }

    public void SelectProviderOption(SetupProviderOptionItem item)
    {
        _selectedProviderOption = item;
        foreach (var option in ProviderOptions) option.IsSelected = option.Key == item.Key;
        if (item.IsConfigured)
        {
            _connectedProviderId = item.ProviderId;
            _connectedProviderTitle = item.Title;
            _connectedProviderDetail = item.Detail;
            ProviderStatus = "Provider is already configured.";
            _isProviderConfigVisible = false;
            NotifyProviderStateChanged();
            return;
        }

        NewProviderId = item.ProviderId;
        NewProviderBaseUrl = item.BaseUrl;
        NewProviderApi = string.IsNullOrWhiteSpace(item.Api) ? "openai-completions" : item.Api;
        NewProviderApiKeyRef = string.IsNullOrWhiteSpace(item.ApiKeyRef) ? DefaultApiKeyRef(item.ProviderId) : item.ApiKeyRef;
        NewProviderModelIds = item.ModelIds;
        ProviderStatus = item.IsCatalogProvider
            ? "Save this provider reference, then set the environment variable before chatting."
            : "Use an OpenAI-compatible endpoint. API keys should be environment variables, not pasted secrets.";
        _isProviderConfigVisible = true;
        NotifyProviderStateChanged();
    }

    public void SaveSelectedProvider()
    {
        var providerId = NewProviderId.Trim();
        var isCatalogProvider = _selectedProviderOption is { IsCatalogProvider: true };
        var modelIds = NewProviderModelIds
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!Regex.IsMatch(providerId, "^[A-Za-z0-9_.-]+$"))
        {
            ProviderStatus = "Provider id can only use letters, numbers, dot, dash, and underscore.";
            return;
        }
        if (string.IsNullOrWhiteSpace(NewProviderBaseUrl) || !Uri.TryCreate(NewProviderBaseUrl.Trim(), UriKind.Absolute, out _))
        {
            ProviderStatus = "Base URL must be a valid absolute URL.";
            return;
        }
        if (string.IsNullOrWhiteSpace(NewProviderApi))
        {
            ProviderStatus = "API type is required.";
            return;
        }
        if (!isCatalogProvider && modelIds.Count == 0)
        {
            ProviderStatus = "Add at least one model id.";
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pi.ModelsPath)!);
            var root = ReadJsonObject(_pi.ModelsPath);
            var providers = root["providers"] as JsonObject;
            if (providers is null)
            {
                providers = new JsonObject();
                root["providers"] = providers;
            }
            if (providers.ContainsKey(providerId))
            {
                ProviderStatus = "That provider already exists in models.json.";
                return;
            }

            var provider = new JsonObject
            {
                ["baseUrl"] = NewProviderBaseUrl.Trim(),
                ["apiKey"] = string.IsNullOrWhiteSpace(NewProviderApiKeyRef) ? DefaultApiKeyRef(providerId) : NewProviderApiKeyRef.Trim(),
            };
            if (!isCatalogProvider)
            {
                provider["api"] = NewProviderApi.Trim();
                var models = new JsonArray();
                foreach (var id in modelIds) models.Add(new JsonObject { ["id"] = id });
                provider["models"] = models;
            }
            providers[providerId] = provider;
            File.WriteAllText(_pi.ModelsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var defaultModel = modelIds.FirstOrDefault() ?? _registryModels.FirstOrDefault(item => item.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.Model;
            if (!string.IsNullOrWhiteSpace(defaultModel)) TrySetDefaultModel(providerId, defaultModel);

            _connectedProviderId = providerId;
            _connectedProviderTitle = _selectedProviderOption?.Title ?? FriendlyProviderTitle(providerId, null);
            _connectedProviderDetail = string.IsNullOrWhiteSpace(defaultModel) ? "Saved to models.json" : $"Default model: {defaultModel}";
            ProviderStatus = "Saved. You can add another provider or start ipi.";
            _isProviderConfigVisible = false;
            RefreshProviderState();
            NotifyProviderStateChanged();
        }
        catch (Exception ex)
        {
            ProviderStatus = ex.Message;
        }
    }

    private void RefreshSetupPlan()
    {
        _runtimeInfo = new PiRuntimeService().Resolve();
        _bootstrapInspection = _bootstrap.Inspect();
        var checks = BuildRuntimeChecks();
        var firstMissingIndex = checks.FindIndex(check => !check.IsReady);
        var reviewingPlan = Screen == "progress"
                            && string.IsNullOrWhiteSpace(_installError)
                            && string.IsNullOrWhiteSpace(_bootstrapError)
                            && _setupLogLines.Count == 0
                            && firstMissingIndex >= 0;

        Steps.Clear();
        for (var i = 0; i < checks.Count; i++)
        {
            var check = checks[i];
            var state = check.IsReady ? "done" : reviewingPlan ? "planned" : i == firstMissingIndex ? "active" : "pending";
            Steps.Add(new SetupStepItem(check.Label, state, check.Detail));
        }

        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(SetupProgressVisibility));
        OnPropertyChanged(nameof(RuntimeModeLabel));
        OnPropertyChanged(nameof(IsRuntimeReady));
        OnPropertyChanged(nameof(ContinueButtonVisibility));
        OnPropertyChanged(nameof(InstallRuntimeButtonVisibility));
        OnPropertyChanged(nameof(RuntimeInstallConfirmationVisibility));
        OnPropertyChanged(nameof(RuntimeChecklistVisibility));
        OnPropertyChanged(nameof(RuntimeInstallSource));
        OnPropertyChanged(nameof(RuntimeInstallTarget));
        OnPropertyChanged(nameof(RuntimeInstallAgentDir));
        OnPropertyChanged(nameof(CloseButtonText));
        OnPropertyChanged(nameof(LiveOutput));
    }

    private List<SetupRuntimeCheck> BuildRuntimeChecks()
    {
        var targetRoot = NormalizeDirectoryPath(InstallPath);
        var targetExe = Path.Combine(targetRoot, "ipi.exe");
        var appFilesReady = IsSameDirectory(targetRoot, AppContext.BaseDirectory) || File.Exists(targetExe);
        if (appFilesReady) _installedExePath = targetExe;
        var bridgePath = Path.Combine(AppContext.BaseDirectory, "agent-bridge.mjs");
        var bridgeExists = File.Exists(bridgePath);
        var runtimeConfigReady = !_bootstrapInspection.RequiredActions.Any(action => action.Label.Contains("runtime config", StringComparison.OrdinalIgnoreCase));

        var checks = new List<SetupRuntimeCheck>
        {
            new("Install location", !string.IsNullOrWhiteSpace(InstallPath), ShortPath(targetRoot)),
            new("Application files", appFilesReady && string.IsNullOrWhiteSpace(_installError), string.IsNullOrWhiteSpace(_installError) ? (appFilesReady ? "installed" : "not installed") : _installError),
        };

        if (!string.IsNullOrWhiteSpace(_bootstrapError)) checks.Add(new SetupRuntimeCheck("Runtime bootstrap", false, _bootstrapError));
        checks.AddRange(new[]
        {
            new SetupRuntimeCheck("Node.js runtime", _bootstrapInspection.HasCompatibleNode, _bootstrapInspection.NodeDetail),
            new SetupRuntimeCheck("Pi upstream package", _bootstrapInspection.HasPiCodingAgent, _bootstrapInspection.HasPiCodingAgent ? ShortPath(_bootstrapInspection.PiCodingAgentRoot) : RuntimeBootstrapService.PiPackageSpec),
            new SetupRuntimeCheck("Pi agent directory", _bootstrapInspection.HasAgentDirectory, ShortPath(_bootstrapInspection.AgentDir)),
            new SetupRuntimeCheck("Settings file", _bootstrapInspection.HasSettings, _bootstrapInspection.HasSettings ? "ready" : "will create"),
            new SetupRuntimeCheck("Model registry", _bootstrapInspection.HasModels, _bootstrapInspection.HasModels ? "ready" : "will create"),
            new SetupRuntimeCheck("Session store", _bootstrapInspection.HasSessions, _bootstrapInspection.HasSessions ? "ready" : "will create"),
            new SetupRuntimeCheck("Plugin package directory", _bootstrapInspection.HasPackageDirectory, _bootstrapInspection.HasPackageDirectory ? "ready" : "will create"),
            new SetupRuntimeCheck("Runtime config", runtimeConfigReady, runtimeConfigReady ? "ready" : ShortPath(_bootstrap.RuntimeConfigPath)),
            new SetupRuntimeCheck("Local bridge", bridgeExists, bridgeExists ? "ready" : "missing"),
        });

        var ready = appFilesReady && string.IsNullOrWhiteSpace(_installError) && string.IsNullOrWhiteSpace(_bootstrapError) && _bootstrapInspection.IsReady && runtimeConfigReady && bridgeExists;
        checks.Add(new SetupRuntimeCheck("Launch readiness", ready, ready ? "ready" : "needs setup"));
        return checks;
    }

    private async Task BootstrapRuntimeAsync()
    {
        try
        {
            _setupLogLines.Clear();
            var progress = new Progress<string>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message)) _setupLogLines.Add(message);
                RefreshSetupPlan();
            });
            await _bootstrap.BootstrapAsync(progress);
            _pi = new PiDataService();
            _runtimeInfo = _pi.RuntimeInfo;
            _bootstrapInspection = _bootstrap.Inspect();
            _bootstrapError = "";
            _ = LoadProviderCatalogAsync();
            _ = LoadRegistryModelsAsync();
        }
        catch (Exception ex)
        {
            _bootstrapError = ex.Message;
            _setupLogLines.Add($"ERROR: {ex.Message}");
        }
    }

    private async Task InstallApplicationFilesAsync()
    {
        var sourceRoot = NormalizeDirectoryPath(AppContext.BaseDirectory);
        var targetRoot = NormalizeDirectoryPath(InstallPath);
        _installedExePath = Path.Combine(targetRoot, "ipi.exe");

        try
        {
            if (string.IsNullOrWhiteSpace(targetRoot)) throw new InvalidOperationException("Install location is empty.");
            if (IsSameDirectory(sourceRoot, targetRoot)) return;
            if (IsSubDirectoryOf(targetRoot, sourceRoot)) throw new InvalidOperationException("Choose a folder outside the current publish directory.");
            if (IsSubDirectoryOf(sourceRoot, targetRoot)) throw new InvalidOperationException("Choose a folder that does not contain the current publish directory.");

            await Task.Run(() => CopyDirectory(sourceRoot, targetRoot));
            WriteInstallMarker(targetRoot, sourceRoot);
            _continueSetupInInstalledCopy = File.Exists(_installedExePath) && !IsSameDirectory(sourceRoot, targetRoot);
        }
        catch (Exception ex)
        {
            _installError = ex.Message;
        }
    }

    public bool TryGetInstalledExecutable(out string executablePath)
    {
        executablePath = InstalledExePath;
        return File.Exists(executablePath);
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            if (ShouldSkipInstallRelativePath(relative)) continue;
            Directory.CreateDirectory(Path.Combine(targetRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (ShouldSkipInstallRelativePath(relative)) continue;
            var destination = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static bool ShouldSkipInstallRelativePath(string relative)
    {
        var normalized = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Equals("ipi.install.json", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("runtime", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith($"runtime{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("session-images", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith($"session-images{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(".WebView2", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains($".WebView2{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("win-x64", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith($"win-x64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("win-x86", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith($"win-x86{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("win-arm64", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith($"win-arm64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteInstallMarker(string targetRoot, string sourceRoot)
    {
        var marker = new JsonObject
        {
            ["app"] = "ipi",
            ["installedAt"] = DateTimeOffset.Now.ToString("O"),
            ["source"] = sourceRoot,
            ["target"] = targetRoot,
        };
        File.WriteAllText(Path.Combine(targetRoot, "ipi.install.json"), marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fallback = IpiPathService.LocalAppDataDir;
        var value = string.IsNullOrWhiteSpace(path) ? fallback : Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSubDirectoryOf(string candidate, string parent)
    {
        try
        {
            var candidatePath = NormalizeDirectoryPath(candidate) + Path.DirectorySeparatorChar;
            var parentPath = NormalizeDirectoryPath(parent) + Path.DirectorySeparatorChar;
            return candidatePath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ShortPath(string path)
    {
        if (path.Length <= 42) return path;
        var root = Path.GetPathRoot(path) ?? "";
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return $"{root}…{Path.DirectorySeparatorChar}{name}";
    }

    private IEnumerable<string> BuildLiveOutputLines()
    {
        yield return $"installLocation={InstallPath}";
        yield return $"installedExe={InstalledExePath}";
        yield return $"runtimeMode={_runtimeInfo.RuntimeMode}";
        yield return $"agentDir={_bootstrapInspection.AgentDir}";
        yield return $"node={_bootstrapInspection.NodeCommand}";
        yield return $"piCodingAgentRoot={_bootstrapInspection.PiCodingAgentRoot}";
        yield return $"runtimeConfig={_bootstrap.RuntimeConfigPath}";
        yield return $"setupLog={_bootstrap.LogPath}";
        yield return $"nodeSource={RuntimeBootstrapService.NodeDownloadUrl}";
        yield return $"piSource={RuntimeBootstrapService.PiPackageUrl}";
        yield return "";
        yield return "requiredActions:";
        if (_bootstrapInspection.RequiredActions.Count == 0) yield return "  none";
        foreach (var action in _bootstrapInspection.RequiredActions) yield return $"  - {action.Label}: {action.Detail}";
        yield return "";
        foreach (var step in Steps) yield return $"[{step.State}] {step.Label}: {step.Detail}";
        if (_setupLogLines.Count == 0) yield break;
        yield return "";
        yield return "log:";
        foreach (var line in _setupLogLines.TakeLast(80)) yield return $"  {line}";
    }

    private async Task LoadProviderCatalogAsync()
    {
        try
        {
            var providers = await _bridge.ListProviderCatalogAsync(Environment.CurrentDirectory, _pi.AgentDir);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _providerCatalog.Clear();
                _providerCatalog.AddRange(providers);
                RefreshProviderState();
            });
        }
        catch
        {
            await Application.Current.Dispatcher.InvokeAsync(RefreshProviderState);
        }
    }

    private async Task LoadRegistryModelsAsync()
    {
        try
        {
            var models = await _bridge.ListModelsAsync(Environment.CurrentDirectory, _pi.AgentDir);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _registryModels.Clear();
                _registryModels.AddRange(models);
                RefreshProviderState();
            });
        }
        catch
        {
            await Application.Current.Dispatcher.InvokeAsync(RefreshProviderState);
        }
    }

    private void RefreshProviderState()
    {
        var settings = _pi.ReadSettingsSummary();
        var configuredModels = new List<PiModelOptionRecord>();
        void AddModel(PiModelOptionRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.Provider) || string.IsNullOrWhiteSpace(record.Model)) return;
            if (record.Provider == "unknown" || record.Model == "unknown") return;
            if (configuredModels.Any(item => item.Provider.Equals(record.Provider, StringComparison.OrdinalIgnoreCase) && item.Model.Equals(record.Model, StringComparison.OrdinalIgnoreCase))) return;
            configuredModels.Add(record);
        }

        foreach (var model in _registryModels.Where(item => item.IsConfigured)) AddModel(model);
        foreach (var model in ReadModelsJsonProviderOptions()) AddModel(model);

        var configuredGroups = configuredModels
            .GroupBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Key.Equals(settings.DefaultProvider, StringComparison.OrdinalIgnoreCase))
            .ThenBy(group => FriendlyProviderTitle(group.Key, group.FirstOrDefault()?.ProviderDisplayName))
            .ToList();

        if (configuredGroups.Count > 0)
        {
            var group = configuredGroups[0];
            var defaultModel = group.FirstOrDefault(item => item.Model.Equals(settings.DefaultModel, StringComparison.OrdinalIgnoreCase)) ?? group.First();
            _connectedProviderId = group.Key;
            _connectedProviderTitle = FriendlyProviderTitle(group.Key, defaultModel.ProviderDisplayName);
            _connectedProviderDetail = $"{group.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count()} models · default: {defaultModel.Model}";
        }
        else
        {
            _connectedProviderId = "";
            _connectedProviderTitle = "No provider connected";
            _connectedProviderDetail = "Choose a provider now, or skip and configure it later in Settings.";
        }

        ProviderOptions.Clear();
        foreach (var group in configuredGroups)
        {
            var first = group.First();
            var count = group.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            ProviderOptions.Add(new SetupProviderOptionItem(
                $"configured:{group.Key}",
                "CONNECTED",
                FriendlyProviderTitle(group.Key, first.ProviderDisplayName),
                $"{count} models · configured",
                group.Key,
                "",
                "",
                DefaultApiKeyRef(group.Key),
                "",
                true,
                false));
        }

        ProviderOptions.Add(new SetupProviderOptionItem(
            "custom-openai",
            "CUSTOM",
            "OpenAI-compatible endpoint",
            "Use any compatible local or hosted endpoint",
            "custom-openai",
            "https://api.example.com/v1",
            "openai-completions",
            "$CUSTOM_OPENAI_API_KEY",
            "model-id",
            false,
            false));

        var configuredProviderIds = configuredGroups.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _providerCatalog
            .Where(item => !string.IsNullOrWhiteSpace(item.Provider) && !configuredProviderIds.Contains(item.Provider))
            .OrderBy(item => CleanProviderName(item.DisplayName), StringComparer.OrdinalIgnoreCase))
        {
            ProviderOptions.Add(new SetupProviderOptionItem(
                $"catalog:{provider.Provider}",
                ProviderCategory(provider),
                CleanProviderName(provider.DisplayName),
                ProviderCatalogDetail(provider),
                provider.Provider,
                provider.BaseUrl,
                provider.Api,
                DefaultApiKeyRef(provider.Provider),
                "",
                provider.IsConfigured,
                true));
        }

        RebuildFilteredProviderOptions();
        NotifyProviderStateChanged();
    }

    private void RebuildFilteredProviderOptions()
    {
        FilteredProviderOptions.Clear();
        var query = ProviderSearchText.Trim();
        foreach (var option in ProviderOptions)
        {
            if (string.IsNullOrWhiteSpace(query)
                || option.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.ProviderId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProviderOptions.Add(option);
            }
        }
    }

    private IReadOnlyList<PiModelOptionRecord> ReadModelsJsonProviderOptions()
    {
        var result = new List<PiModelOptionRecord>();
        try
        {
            if (!File.Exists(_pi.ModelsPath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(_pi.ModelsPath));
            if (!doc.RootElement.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Object) return result;
            foreach (var providerProperty in providers.EnumerateObject())
            {
                var provider = providerProperty.Name;
                var providerElement = providerProperty.Value;
                if (providerElement.ValueKind != JsonValueKind.Object) continue;
                if (!providerElement.TryGetProperty("models", out var models)) continue;
                if (models.ValueKind == JsonValueKind.Array)
                {
                    foreach (var model in models.EnumerateArray()) AddModelOption(provider, model, result);
                }
                else if (models.ValueKind == JsonValueKind.Object)
                {
                    foreach (var modelProperty in models.EnumerateObject())
                    {
                        var id = modelProperty.Name;
                        var name = id;
                        if (modelProperty.Value.ValueKind == JsonValueKind.Object && modelProperty.Value.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String) name = nameElement.GetString() ?? id;
                        result.Add(new PiModelOptionRecord(provider, id, name, "models.json", true));
                    }
                }
            }
        }
        catch
        {
            return result;
        }
        return result;
    }

    private static void AddModelOption(string provider, JsonElement model, List<PiModelOptionRecord> result)
    {
        if (model.ValueKind == JsonValueKind.String)
        {
            var id = model.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(id)) result.Add(new PiModelOptionRecord(provider, id, id, "models.json", true));
            return;
        }
        if (model.ValueKind != JsonValueKind.Object) return;
        var modelId = model.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? ""
            : model.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString() ?? ""
                : "";
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var name = model.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() ?? modelId : modelId;
        result.Add(new PiModelOptionRecord(provider, modelId, name, "models.json", true));
    }

    private void TrySetDefaultModel(string providerId, string modelId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pi.SettingsPath)!);
            var root = ReadJsonObject(_pi.SettingsPath);
            root["defaultProvider"] = providerId;
            root["defaultModel"] = modelId;
            File.WriteAllText(_pi.SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Provider is still saved even when default model update fails.
        }
    }

    private static JsonObject ReadJsonObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private void NotifyProviderStateChanged()
    {
        OnPropertyChanged(nameof(ProviderReadySummaryVisibility));
        OnPropertyChanged(nameof(ProviderGalleryVisibility));
        OnPropertyChanged(nameof(ProviderConfigVisibility));
        OnPropertyChanged(nameof(StartIpiVisibility));
        OnPropertyChanged(nameof(AddAnotherProviderVisibility));
        OnPropertyChanged(nameof(ConnectedProviderId));
        OnPropertyChanged(nameof(ConnectedProviderTitle));
        OnPropertyChanged(nameof(ConnectedProviderDetail));
        OnPropertyChanged(nameof(HasConnectedProvider));
        OnPropertyChanged(nameof(ProviderConfigTitle));
        OnPropertyChanged(nameof(NewProviderModelsVisibility));
    }

    private static string ProviderCategory(PiProviderCatalogRecord provider)
    {
        var id = provider.Provider.ToLowerInvariant();
        if (id == "github-copilot" || provider.Api.Contains("oauth", StringComparison.OrdinalIgnoreCase)) return "SUBSCRIPTION";
        return "API KEY";
    }

    private static string ProviderCatalogDetail(PiProviderCatalogRecord provider)
    {
        var auth = provider.IsConfigured ? "configured" : ProviderCategory(provider).ToLowerInvariant();
        return provider.ModelCount > 0 ? $"{provider.ModelCount} models · {auth}" : auth;
    }

    private static string CleanProviderName(string name)
        => name.Replace(" (Codex Subscription)", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" (Claude Pro/Max)", "", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyProviderTitle(string provider, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) return CleanProviderName(displayName!);
        return provider switch
        {
            "openai-codex" => "ChatGPT Plus/Pro",
            _ => provider,
        };
    }

    private static string DefaultApiKeyRef(string provider)
    {
        var normalized = Regex.Replace(provider.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "$API_KEY" : $"${normalized}_API_KEY";
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var directory = entry.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    private static string NodeDetail(string nodePath)
    {
        var configured = Environment.GetEnvironmentVariable("IPI_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && string.Equals(Path.GetFullPath(configured), Path.GetFullPath(nodePath), StringComparison.OrdinalIgnoreCase)) return "env";
        return nodePath.Contains(Path.Combine("runtime", "node"), StringComparison.OrdinalIgnoreCase) ? "bundled" : "PATH";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record SetupStepItem(string Label, string State, string Detail)
{
    public string Glyph => State switch
    {
        "done" => "✓",
        "active" => "○",
        _ => "•"
    };

    public Brush GlyphBrush => State switch
    {
        "done" => new SolidColorBrush(Color.FromRgb(64, 200, 120)),
        "active" => new SolidColorBrush(Color.FromRgb(67, 87, 244)),
        "planned" => new SolidColorBrush(Color.FromRgb(142, 149, 166)),
        _ => new SolidColorBrush(Color.FromRgb(195, 199, 208)),
    };

    public Brush TextBrush => State switch
    {
        "pending" => new SolidColorBrush(Color.FromRgb(178, 182, 192)),
        _ => new SolidColorBrush(Color.FromRgb(86, 90, 100)),
    };
}

public sealed record SetupRuntimeCheck(string Label, bool IsReady, string Detail);

public sealed class SetupProviderOptionItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public SetupProviderOptionItem(
        string key,
        string category,
        string title,
        string detail,
        string providerId,
        string baseUrl,
        string api,
        string apiKeyRef,
        string modelIds,
        bool isConfigured,
        bool isCatalogProvider)
    {
        Key = key;
        Category = category;
        Title = title;
        Detail = detail;
        ProviderId = providerId;
        BaseUrl = baseUrl;
        Api = api;
        ApiKeyRef = apiKeyRef;
        ModelIds = modelIds;
        IsConfigured = isConfigured;
        IsCatalogProvider = isCatalogProvider;
    }

    public string Key { get; }
    public string Category { get; }
    public string Title { get; }
    public string Detail { get; }
    public string ProviderId { get; }
    public string BaseUrl { get; }
    public string Api { get; }
    public string ApiKeyRef { get; }
    public string ModelIds { get; }
    public bool IsConfigured { get; }
    public bool IsCatalogProvider { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
