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
        if (_viewModel.Screen == "welcome")
        {
            Dispatcher.BeginInvoke(() =>
            {
                InstallPathBox.Focus();
                Keyboard.Focus(InstallPathBox);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsOperationRunning)
        {
            _viewModel.CancelCurrentOperation();
            return;
        }
        Close();
    }

    private void SetupPreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsOperationRunning) return;
        e.Cancel = true;
        _viewModel.CancelCurrentOperation();
    }

    private async void ShowProgress_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmNonEmptyInstallTarget()) return;
        await _viewModel.StartSetupAsync();
    }

    private async void RetryInstall_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmNonEmptyInstallTarget()) return;
        await _viewModel.StartSetupAsync();
    }

    private bool ConfirmNonEmptyInstallTarget()
    {
        try
        {
            if (!_viewModel.InstallTargetHasContent(out var target)) return true;
            return MessageBox.Show(
                       this,
                       $"The selected folder is not empty:\n{target}\n\nExisting files with the same names may be replaced. Continue only if this folder is intended for ipi.",
                       "Confirm install location",
                       MessageBoxButton.YesNo,
                       MessageBoxImage.Warning,
                       MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid install location", MessageBoxButton.OK, MessageBoxImage.Warning);
            InstallPathBox.Focus();
            InstallPathBox.SelectAll();
            return false;
        }
    }

    private async void InstallRuntime_Click(object sender, RoutedEventArgs e) => await _viewModel.InstallRuntimeAsync();

    private void ToggleDetails_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDetails = !_viewModel.ShowDetails;

    private void ShowFailed_Click(object sender, RoutedEventArgs e) => _viewModel.Screen = "failed";

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRuntimeReady)
        {
            _viewModel.ShowProviderSetup();
            FocusProviderContent();
        }
    }

    private void StartIpi_Click(object sender, RoutedEventArgs e) => OpenMainWindow();

    private void SkipProvider_Click(object sender, RoutedEventArgs e) => OpenMainWindow();

    private void AddAnotherProvider_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowProviderGallery();
        FocusProviderContent();
    }

    private void ProviderBack_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowProviderGallery();
        FocusProviderContent();
    }

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

    private async void ProviderOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SetupProviderOptionItem item })
        {
            await _viewModel.SelectProviderOptionAsync(item);
            FocusProviderContent();
        }
    }

    private void FocusProviderContent()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var target = _viewModel.ProviderConfigVisibility == Visibility.Visible
                ? (IInputElement)SetupProviderBaseUrlBox
                : SetupProviderSearchBox;
            target.Focus();
            Keyboard.Focus(target);
        }, System.Windows.Threading.DispatcherPriority.Input);
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
    private RuntimeBootstrapService _bootstrap = new();
    private readonly PathSettingsService _pathSettings = new();
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
    private CancellationTokenSource? _operationCancellation;
    private bool _isOperationRunning;
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
            ConfigureFirstInstallStorageForSelectedLocation();
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
    public bool IsOperationRunning
    {
        get => _isOperationRunning;
        private set
        {
            if (_isOperationRunning == value) return;
            _isOperationRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ApplicationInstallRetryVisibility));
            OnPropertyChanged(nameof(CloseButtonText));
        }
    }
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
    public Visibility ApplicationInstallRetryVisibility => !IsOperationRunning && !string.IsNullOrWhiteSpace(_installError) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuntimeInstallConfirmationVisibility => IsAwaitingRuntimeInstall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuntimeChecklistVisibility => IsAwaitingRuntimeInstall ? Visibility.Collapsed : Visibility.Visible;
    public string RuntimeInstallSource => RuntimeBootstrapService.PiPackageSpec;
    public string RuntimeInstallTarget => ShortPath(_bootstrapInspection.PiCodingAgentRoot);
    public string RuntimeInstallAgentDir => ShortPath(_bootstrapInspection.AgentDir);
    public string CloseButtonText => IsOperationRunning ? "Cancel" : IsRuntimeReady ? "Close" : "Cancel";
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

    public void CancelCurrentOperation()
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested) return;
        _setupLogLines.Add("Cancellation requested. Finishing the current file operation safely…");
        _operationCancellation.Cancel();
        RefreshSetupPlan();
    }

    public async Task StartSetupAsync()
    {
        if (IsOperationRunning) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsOperationRunning = true;
        Screen = "progress";
        ShowDetails = false;
        _installError = "";
        _bootstrapError = "";
        _continueSetupInInstalledCopy = false;
        RefreshSetupPlan();
        try
        {
            await Task.Yield();
            await InstallApplicationFilesAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _installError = "Application file copy was canceled. You can retry when ready.";
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation)) _operationCancellation = null;
            IsOperationRunning = false;
            RefreshSetupPlan();
        }
    }

    public async Task InstallRuntimeAsync()
    {
        if (IsOperationRunning) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsOperationRunning = true;
        Screen = "progress";
        _bootstrapError = "";
        _setupLogLines.Clear();
        _isInstallingRuntime = true;
        RefreshSetupPlan();
        try
        {
            await BootstrapRuntimeAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _bootstrapError = "Runtime setup was canceled. No secret or provider data was changed.";
            _setupLogLines.Add("CANCELED: runtime setup was canceled by the user.");
        }
        finally
        {
            _isInstallingRuntime = false;
            if (ReferenceEquals(_operationCancellation, cancellation)) _operationCancellation = null;
            IsOperationRunning = false;
            RefreshSetupPlan();
        }
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

    public async Task SelectProviderOptionAsync(SetupProviderOptionItem item)
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

        if (item.RequiresOAuthLogin)
        {
            _isProviderConfigVisible = false;
            ProviderStatus = $"Opening {item.Title} sign-in in your browser...";
            NotifyProviderStateChanged();
            try
            {
                await _bridge.LoginOAuthAsync(item.ProviderId, Environment.CurrentDirectory, _pi.AgentDir, status => ProviderStatus = status, url => OpenOAuthBrowser(item.ProviderId, url));
                _providerCatalog.Clear();
                _providerCatalog.AddRange(await _bridge.ListProviderCatalogAsync(Environment.CurrentDirectory, _pi.AgentDir));
                _registryModels.Clear();
                _registryModels.AddRange(await _bridge.ListModelsAsync(Environment.CurrentDirectory, _pi.AgentDir));
                RefreshProviderState();
                ProviderStatus = $"{item.Title} is connected. Choose a model and start ipi.";
            }
            catch (Exception ex)
            {
                ProviderStatus = DescribeOAuthFailure(item, ex);
            }
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
        var apiKeyReference = string.IsNullOrWhiteSpace(NewProviderApiKeyRef) ? DefaultApiKeyRef(providerId) : NewProviderApiKeyRef.Trim();
        var modelIds = NewProviderModelIds
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!Regex.IsMatch(providerId, "^[A-Za-z0-9_.-]+$"))
        {
            ProviderStatus = "Provider id can only use letters, numbers, dot, dash, and underscore.";
            return;
        }
        if (!TryValidateProviderEndpoint(NewProviderBaseUrl, out _))
        {
            ProviderStatus = "Remote Base URLs must use HTTPS. HTTP is loopback-only, and credentials must not be embedded in the URL.";
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
        if (!Regex.IsMatch(apiKeyReference, "^\\$[A-Za-z_][A-Za-z0-9_]*$"))
        {
            NewProviderApiKeyRef = "";
            ProviderStatus = "Use an environment variable reference such as $OPENAI_API_KEY. Secret values are not stored in models.json.";
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
                ["apiKey"] = apiKeyReference,
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
        OnPropertyChanged(nameof(ApplicationInstallRetryVisibility));
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

    private async Task BootstrapRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _setupLogLines.Clear();
            var progress = new Progress<string>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message)) _setupLogLines.Add(message);
                RefreshSetupPlan();
            });
            await _bootstrap.BootstrapAsync(progress, cancellationToken);
            _pi = new PiDataService();
            _runtimeInfo = _pi.RuntimeInfo;
            _bootstrapInspection = _bootstrap.Inspect();
            _bootstrapError = "";
            _ = LoadProviderCatalogAsync();
            _ = LoadRegistryModelsAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _bootstrapError = ex.Message;
            _setupLogLines.Add($"ERROR: {ex.Message}");
        }
    }

    private async Task InstallApplicationFilesAsync(CancellationToken cancellationToken)
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

            await Task.Run(() => CopyDirectory(sourceRoot, targetRoot, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            WriteInstallMarker(targetRoot, sourceRoot);
            _continueSetupInInstalledCopy = File.Exists(_installedExePath) && !IsSameDirectory(sourceRoot, targetRoot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _installError = ex.Message;
        }
    }

    public bool InstallTargetHasContent(out string targetPath)
    {
        targetPath = NormalizeDirectoryPath(InstallPath);
        var sourceRoot = NormalizeDirectoryPath(AppContext.BaseDirectory);
        if (IsSameDirectory(sourceRoot, targetPath) || !Directory.Exists(targetPath)) return false;
        return Directory.EnumerateFileSystemEntries(targetPath).Any();
    }

    private void ConfigureFirstInstallStorageForSelectedLocation()
    {
        // NSIS has already installed the app into the directory the person selected.  On a
        // genuinely fresh install, keep the accompanying runtime and agent data there too
        // instead of silently falling back to the system drive's AppData folders.
        var selectedRoot = NormalizeDirectoryPath(InstallPath);
        var defaultInstallRoot = NormalizeDirectoryPath(IpiPathService.DefaultLocalAppDataDir);
        var existingPaths = _pathSettings.Load();
        if (IsSameDirectory(selectedRoot, defaultInstallRoot)
            || !string.IsNullOrWhiteSpace(existingPaths.AppDataDir)
            || !string.IsNullOrWhiteSpace(existingPaths.LocalAppDataDir)
            || HasExistingDefaultStorage())
        {
            return;
        }

        var settings = new IpiPathSettings(
            Path.Combine(selectedRoot, "data"),
            Path.Combine(selectedRoot, "runtime"));
        _pathSettings.Save(settings);

        // Services capture their roots at construction, so recreate them after persisting
        // the first-install choice before any inspection or runtime download begins.
        _bootstrap = new RuntimeBootstrapService();
        _pi = new PiDataService();
        _runtimeInfo = _pi.RuntimeInfo;
    }

    private static bool HasExistingDefaultStorage()
    {
        var defaultAppData = IpiPathService.DefaultAppDataDir;
        var defaultLocalAppData = IpiPathService.DefaultLocalAppDataDir;
        return File.Exists(Path.Combine(defaultAppData, "runtime.json"))
               || Directory.Exists(Path.Combine(defaultAppData, "agent"))
               || Directory.Exists(Path.Combine(defaultLocalAppData, "runtime"));
    }

    public bool TryGetInstalledExecutable(out string executablePath)
    {
        executablePath = InstalledExePath;
        return File.Exists(executablePath);
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, directory);
            if (ShouldSkipInstallRelativePath(relative)) continue;
            Directory.CreateDirectory(Path.Combine(targetRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        AddSubscriptionProviderOption("anthropic", "Claude Pro/Max (OAuth)", "Sign in with Claude to connect your subscription");
        AddSubscriptionProviderOption("openai-codex", "ChatGPT Plus/Pro (OAuth)", "Sign in with ChatGPT to connect your subscription");
        AddSubscriptionProviderOption("github-copilot", "GitHub Copilot (OAuth)", "Sign in with GitHub to connect your subscription");
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
            .Where(item => !string.IsNullOrWhiteSpace(item.Provider)
                           && !IsSubscriptionOAuthProvider(item.Provider)
                           && !configuredProviderIds.Contains(item.Provider))
            .OrderBy(item => CleanProviderName(item.DisplayName), StringComparer.OrdinalIgnoreCase))
        {
            ProviderOptions.Add(new SetupProviderOptionItem(
                $"catalog:{provider.Provider}",
                ProviderCategory(provider),
                ProviderDisplayTitle(provider.Provider, provider.DisplayName),
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

    private void AddSubscriptionProviderOption(string providerId, string title, string signInDetail)
    {
        var provider = _providerCatalog.FirstOrDefault(item => item.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        ProviderOptions.Add(new SetupProviderOptionItem(
            providerId, "SUBSCRIPTION", title,
            provider?.IsConfigured == true ? $"connected via {title}" : signInDetail,
            providerId, "", "", "", "", provider?.IsConfigured == true, true, true));
    }

    private static bool IsSubscriptionOAuthProvider(string providerId)
        => providerId.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
           || providerId.Equals("openai-codex", StringComparison.OrdinalIgnoreCase)
           || providerId.Equals("github-copilot", StringComparison.OrdinalIgnoreCase);

    private static string DescribeOAuthFailure(SetupProviderOptionItem item, Exception exception)
    {
        var detail = exception.ToString();
        if (item.ProviderId.Equals("openai-codex", StringComparison.OrdinalIgnoreCase)
            && detail.Contains("unsupported_country_region_territory", StringComparison.OrdinalIgnoreCase))
        {
            return "ChatGPT Codex sign-in is not available from your current country, region, or territory. Choose another provider, or select Skip for now and configure one later in Settings.";
        }
        return $"{item.Title} could not be connected. Choose another provider, or select Skip for now and configure one later in Settings.";
    }

    private static void OpenOAuthBrowser(string providerId, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !IsExpectedOAuthHost(providerId, uri.Host))
        {
            throw new InvalidOperationException("Subscription sign-in returned an unexpected authorization URL.");
        }
        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
    }

    private static bool IsExpectedOAuthHost(string providerId, string host)
    {
        var roots = providerId switch
        {
            "anthropic" => new[] { "anthropic.com", "claude.ai" },
            "openai-codex" => new[] { "openai.com", "chatgpt.com" },
            "github-copilot" => new[] { "github.com" },
            _ => Array.Empty<string>(),
        };
        return roots.Any(root => host.Equals(root, StringComparison.OrdinalIgnoreCase) || host.EndsWith($".{root}", StringComparison.OrdinalIgnoreCase));
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
        if (provider.IsConfigured) return provider.ModelCount > 0 ? $"{provider.ModelCount} models · configured" : "configured";
        var models = provider.ModelCount > 0 ? $"{provider.ModelCount} models · " : "";
        return models + "Connect to use this provider";
    }

    private static string CleanProviderName(string name)
        => name.Replace(" (Codex Subscription)", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" (Claude Pro/Max)", "", StringComparison.OrdinalIgnoreCase);

    private static string ProviderDisplayTitle(string provider, string displayName)
    {
        var name = provider.ToLowerInvariant() switch
        {
            "amazon-bedrock" => "Amazon Bedrock",
            "azure-openai-responses" => "Azure OpenAI",
            "cloudflare-ai-gateway" => "Cloudflare AI Gateway",
            "cloudflare-workers-ai" => "Cloudflare Workers AI",
            "google-vertex" => "Google Vertex AI",
            "huggingface" => "Hugging Face",
            "xai" => "xAI",
            _ => CleanProviderName(displayName),
        };
        return $"{name} ({ProviderAuthenticationLabel(provider)})";
    }

    private static string ProviderAuthenticationLabel(string provider) => provider.ToLowerInvariant() switch
    {
        "amazon-bedrock" => "AWS credentials",
        "google-vertex" => "Google sign-in",
        _ => "API key",
    };

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
        var known = provider.ToLowerInvariant() switch
        {
            "anthropic" => "$ANTHROPIC_API_KEY",
            "ant-ling" => "$ANT_LING_API_KEY",
            "azure-openai-responses" => "$AZURE_OPENAI_API_KEY",
            "openai" => "$OPENAI_API_KEY",
            "deepseek" => "$DEEPSEEK_API_KEY",
            "nvidia" => "$NVIDIA_API_KEY",
            "google" => "$GEMINI_API_KEY",
            "google-vertex" => "$GOOGLE_APPLICATION_CREDENTIALS",
            "amazon-bedrock" => "$AWS_BEARER_TOKEN_BEDROCK",
            "mistral" => "$MISTRAL_API_KEY",
            "groq" => "$GROQ_API_KEY",
            "cerebras" => "$CEREBRAS_API_KEY",
            "cloudflare-ai-gateway" or "cloudflare-workers-ai" => "$CLOUDFLARE_API_KEY",
            "xai" => "$XAI_API_KEY",
            "openrouter" => "$OPENROUTER_API_KEY",
            "vercel-ai-gateway" => "$AI_GATEWAY_API_KEY",
            "zai" => "$ZAI_API_KEY",
            "zai-coding-cn" => "$ZAI_CODING_CN_API_KEY",
            "opencode" or "opencode-go" => "$OPENCODE_API_KEY",
            "radius" => "$RADIUS_API_KEY",
            "huggingface" => "$HF_TOKEN",
            "fireworks" => "$FIREWORKS_API_KEY",
            "together" => "$TOGETHER_API_KEY",
            "kimi-coding" => "$KIMI_API_KEY",
            "minimax" => "$MINIMAX_API_KEY",
            "minimax-cn" => "$MINIMAX_CN_API_KEY",
            "xiaomi" => "$XIAOMI_API_KEY",
            "xiaomi-token-plan-cn" => "$XIAOMI_TOKEN_PLAN_CN_API_KEY",
            "xiaomi-token-plan-ams" => "$XIAOMI_TOKEN_PLAN_AMS_API_KEY",
            "xiaomi-token-plan-sgp" => "$XIAOMI_TOKEN_PLAN_SGP_API_KEY",
            _ => "",
        };
        if (!string.IsNullOrWhiteSpace(known)) return known;
        var normalized = Regex.Replace(provider.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "$API_KEY" : $"${normalized}_API_KEY";
    }

    private static string ProviderConfigurationHint(string provider) => provider.ToLowerInvariant() switch
    {
        "amazon-bedrock" => "AWS credentials or AWS_BEARER_TOKEN_BEDROCK",
        "azure-openai-responses" => "AZURE_OPENAI_API_KEY + base URL/resource",
        "cloudflare-ai-gateway" => "CLOUDFLARE_API_KEY + account ID + gateway ID",
        "cloudflare-workers-ai" => "CLOUDFLARE_API_KEY + account ID",
        "google-vertex" => "Google Application Default Credentials",
        _ => DefaultApiKeyRef(provider).TrimStart('$'),
    };

    private static bool TryValidateProviderEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (string.IsNullOrEmpty(parsed.UserInfo) &&
            (parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && parsed.IsLoopback)
           )
        {
            endpoint = parsed;
            return true;
        }
        return false;
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
        bool isCatalogProvider,
        bool requiresOAuthLogin = false)
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
        RequiresOAuthLogin = requiresOAuthLogin;
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
    public bool RequiresOAuthLogin { get; }
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
