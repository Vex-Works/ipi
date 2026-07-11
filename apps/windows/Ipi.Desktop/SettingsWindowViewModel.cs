using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Ipi.Desktop.Models;
using Ipi.Desktop.Services;

namespace Ipi.Desktop;

public sealed class SettingsWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppearanceSettingsService _service;
    private readonly ArchiveStoreService _archive;
    private readonly PiDataService _pi = new();
    private readonly PiAgentBridgeService _bridge = new();
    private readonly PiPackageBridgeService _packageBridge = new();
    private readonly PathSettingsService _pathSettings = new();
    private readonly object _packageOperationGate = new();
    private CancellationTokenSource? _packageOperationCancellation;
    private bool _isDisposed;
    private readonly List<PiModelOptionRecord> _registryModels = new();
    private readonly List<PiProviderCatalogRecord> _providerCatalog = new();
    private readonly List<PiModelOptionRecord> _modelRecords = new();
    private string _settingsSection = "appearance";
    private string _archiveSearchText = "";
    private string _providerSearchText = "";
    private bool _isAddingProvider;
    private bool _isProviderConfigVisible;
    private ModelProviderViewItem? _selectedModelProvider;
    private ProviderTemplateItem? _selectedProviderTemplate;
    private string _newProviderId = "";
    private string _newProviderBaseUrl = "";
    private string _newProviderApi = "openai-completions";
    private string _newProviderApiKeyRef = "";
    private string _newProviderModelIds = "";
    private string _newProviderStatus = "";
    private string _language;
    private string _mode;
    private string _theme;
    private string _themeSearchText = "";
    private double _windowTransparency;
    private bool _isLanguageMenuOpen;
    private string _appDataDirOverride = string.Empty;
    private string _localAppDataDirOverride = string.Empty;
    private string _storageStatus = string.Empty;

    public SettingsWindowViewModel(AppearanceSettingsService service, ArchiveStoreService? archiveStore = null)
    {
        _service = service;
        _archive = archiveStore ?? new ArchiveStoreService();
        var settings = _service.Load();
        _language = settings.Language;
        _mode = settings.Mode;
        _theme = settings.Theme is "ipi" or "broadsheet" or "candy-block" ? settings.Theme : "ipi";
        _windowTransparency = settings.WindowTransparency;
        NotificationSoundsEnabled = settings.NotificationSoundsEnabled;
        var pathSettings = _pathSettings.Load();
        _appDataDirOverride = pathSettings.AppDataDir ?? string.Empty;
        _localAppDataDirOverride = pathSettings.LocalAppDataDir ?? string.Empty;

        LanguageOptions.Add(new("zh-CN", "简体中文"));
        LanguageOptions.Add(new("en-US", "English"));
        RefreshPackageScopeOptions();

        ModeOptions.Add(new("light", "sun", ModeLabel("light")));
        ModeOptions.Add(new("dark", "moon", ModeLabel("dark")));
        ModeOptions.Add(new("system", "monitor", ModeLabel("system")));

        Themes.Add(new("ipi", "ipi", "当前 ipi 主题", "Current ipi theme", "#EDF2FF", "#EEF3FF", "#C1CCFF", "#696B7A"));
        Themes.Add(new("broadsheet", "Broadsheet", "骨纸与靛蓝的编辑式主题", "Bone paper and indigo editorial theme", "#E8E6E0", "#1925AA", "#0D1355", "#000000"));
        Themes.Add(new("candy-block", "Candy Block", "白墙、暖奶油卡片与泡泡糖粉行动按钮", "White canvas, warm cream cards, and bubblegum actions", "#FFFFFF", "#FFFCF6", "#FF83DA", "#161616"));

        BuildProviderTemplates();
        RebuildFilteredThemes();
        RefreshSelectionState();
        RefreshArchivedSessions();
        RefreshModelProviders();
        RefreshSkills();
        RefreshPluginPackages();
        _ = LoadProviderCatalogAsync();
        _ = LoadRegistryModelsAsync();
        _archive.Changed += RefreshArchivedSessions;
    }

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();
    public ObservableCollection<ModeOption> ModeOptions { get; } = new();
    public ObservableCollection<ThemeCardItem> Themes { get; } = new();
    public ObservableCollection<ThemeCardItem> FilteredThemes { get; } = new();
    public ObservableCollection<ArchivedSessionViewItem> ArchivedSessions { get; } = new();
    public ObservableCollection<ModelProviderViewItem> ModelProviders { get; } = new();
    public ObservableCollection<ModelDetailViewItem> SelectedProviderModels { get; } = new();
    private string _selectedSkillSourcePath = string.Empty;
    public ObservableCollection<SettingsSkillSourceItem> SkillSources { get; } = new();
    public ObservableCollection<SettingsSkillItem> SkillItems { get; } = new();
    public ObservableCollection<PluginPackageViewItem> SettingsPluginPackages { get; } = new();
    public ObservableCollection<PluginResourceGroupViewItem> SettingsPluginResourceGroups { get; } = new();
    private PluginPackageViewItem? _selectedSettingsPluginPackage;
    private bool _isPackageActionRunning;
    private bool _isPackageAddOpen;
    private string _packageNewSource = "npm:";
    private string _packageActionStatus = string.Empty;
    private string _packageInstallScope = "global";
    public ObservableCollection<SettingsRuntimeDiagnosticItem> RuntimeDiagnostics { get; } = new();
    public ObservableCollection<PackageScopeOption> PackageScopeOptions { get; } = new();
    public ObservableCollection<ProviderTemplateItem> ProviderTemplates { get; } = new();
    public ObservableCollection<ProviderTemplateItem> FilteredProviderTemplates { get; } = new();
    public ObservableCollection<ProviderTemplateGroup> FilteredProviderTemplateGroups { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AppearanceSettings>? SettingsChanged;

    public AppearanceSettings CurrentSettings => new(Language, Mode, Theme, WindowTransparency, NotificationSoundsEnabled);

    public bool NotificationSoundsEnabled { get; private set; }

    public void SetNotificationSoundsEnabled(bool enabled)
    {
        if (NotificationSoundsEnabled == enabled) return;
        NotificationSoundsEnabled = enabled;
        OnPropertyChanged();
        SaveAndNotify();
    }

    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LanguageLabel));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(CloseText));
            OnPropertyChanged(nameof(AppearanceTitle));
            OnPropertyChanged(nameof(LanguageTitle));
            OnPropertyChanged(nameof(ThemeTitle));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(WindowTransparencyTitle));
            OnPropertyChanged(nameof(WindowTransparencyDescription));
            OnPropertyChanged(nameof(ArchivedConversationsTitle));
            OnPropertyChanged(nameof(ArchivedConversationsDescription));
            OnPropertyChanged(nameof(ArchiveSearchPlaceholder));
            OnPropertyChanged(nameof(ArchivedCountText));
            OnPropertyChanged(nameof(RestoreAllArchivedText));
            OnPropertyChanged(nameof(DeleteAllArchivedText));
            OnPropertyChanged(nameof(CleanMissingArchivedText));
            OnPropertyChanged(nameof(ArchivedEmptyTitle));
            OnPropertyChanged(nameof(ArchivedEmptyDescription));
            OnPropertyChanged(nameof(ModelsTitle));
            OnPropertyChanged(nameof(ModelsDescription));
            OnPropertyChanged(nameof(ModelsPathText));
            OnPropertyChanged(nameof(SkillsTitle));
            OnPropertyChanged(nameof(SkillsDescription));
            OnPropertyChanged(nameof(SkillsSummary));
            OnPropertyChanged(nameof(AddSkillSourceText));
            OnPropertyChanged(nameof(PluginPackagesTitle));
            OnPropertyChanged(nameof(PluginPackagesDescription));
            OnPropertyChanged(nameof(PluginPackagesSummary));
            OnPropertyChanged(nameof(PluginPackagesSettingsPath));
            OnPropertyChanged(nameof(PluginPackagesEmptyText));
            OnPropertyChanged(nameof(PackageStatusLabel));
            OnPropertyChanged(nameof(PackageVersionLabel));
            OnPropertyChanged(nameof(PackageResourcesLabel));
            OnPropertyChanged(nameof(PackageScopeLabel));
            OnPropertyChanged(nameof(PackageAddButtonText));
            OnPropertyChanged(nameof(PackageUpdateButtonText));
            OnPropertyChanged(nameof(PackageRemoveButtonText));
            OnPropertyChanged(nameof(PackageCancelButtonText));
            OnPropertyChanged(nameof(PackageInstallSourceLabel));
            OnPropertyChanged(nameof(PackageInstallScopeDetail));
            OnPropertyChanged(nameof(PackageGlobalScopeText));
            OnPropertyChanged(nameof(PackageSourcePlaceholder));
            OnPropertyChanged(nameof(RefreshPackagesText));
            OnPropertyChanged(nameof(SelectedPluginPackageTitle));
            OnPropertyChanged(nameof(SelectedPluginPackageStatus));
            OnPropertyChanged(nameof(RuntimeDiagnosticsTitle));
            OnPropertyChanged(nameof(RuntimeDiagnosticsDescription));
            OnPropertyChanged(nameof(RuntimeDiagnosticsSummary));
            OnPropertyChanged(nameof(RefreshDiagnosticsText));
            OnPropertyChanged(nameof(StoragePathsTitle));
            OnPropertyChanged(nameof(StoragePathsDescription));
            OnPropertyChanged(nameof(AppDataDirTitle));
            OnPropertyChanged(nameof(AppDataDirDescription));
            OnPropertyChanged(nameof(LocalAppDataDirTitle));
            OnPropertyChanged(nameof(LocalAppDataDirDescription));
            OnPropertyChanged(nameof(StorageConfigPathText));
            OnPropertyChanged(nameof(StorageRestartNotice));
            OnPropertyChanged(nameof(BrowseText));
            OnPropertyChanged(nameof(RestoreDefaultText));
            OnPropertyChanged(nameof(SaveStoragePathsText));
            OnPropertyChanged(nameof(AddProviderText));
            OnPropertyChanged(nameof(ProviderSearchPlaceholder));
            OnPropertyChanged(nameof(ModelProviderEmptyTitle));
            OnPropertyChanged(nameof(ModelProviderEmptyDescription));
            OnPropertyChanged(nameof(SelectedProviderTitle));
            OnPropertyChanged(nameof(SelectedProviderDetail));
            OnPropertyChanged(nameof(ModelDetailEmptyTitle));
            OnPropertyChanged(nameof(ModelDetailEmptyDescription));
            OnPropertyChanged(nameof(AddProviderTitle));
            OnPropertyChanged(nameof(AddProviderDescription));
            OnPropertyChanged(nameof(CustomProviderFormTitle));
            OnPropertyChanged(nameof(NewProviderIdLabel));
            OnPropertyChanged(nameof(NewProviderBaseUrlLabel));
            OnPropertyChanged(nameof(NewProviderApiLabel));
            OnPropertyChanged(nameof(NewProviderApiKeyRefLabel));
            OnPropertyChanged(nameof(NewProviderModelsLabel));
            OnPropertyChanged(nameof(SaveProviderText));
            OnPropertyChanged(nameof(CancelText));
            RefreshModeLabels();
            RefreshThemeLabels();
            RefreshProviderTemplateLabels();
            RefreshPackageScopeOptions();
            RefreshArchivedSessions();
            RefreshModelProviders();
            RefreshPluginPackages();
            if (IsDiagnosticsSection) RefreshRuntimeDiagnostics();
            SaveAndNotify();
        }
    }

    public string LanguageLabel => LanguageOptions.FirstOrDefault(option => option.Key == Language)?.Label ?? "简体中文";
    public string WindowTitle => Language == "en-US" ? "Settings" : "设置";
    public string CloseText => Language == "en-US" ? "Close" : "关闭";
    public string AppearanceTitle => Language == "en-US" ? "Appearance" : "外观";
    public string LanguageTitle => Language == "en-US" ? "Language" : "语言";
    public string ThemeTitle => Language == "en-US" ? "Theme" : "主题";
    public string SearchPlaceholder => Language == "en-US" ? "Search your themes..." : "搜索主题...";
    public string WindowTransparencyTitle => Language == "en-US" ? "Window transparency" : "窗口透明";
    public string WindowTransparencyDescription => Language == "en-US"
        ? "Let the window show the desktop through Windows system backdrop effects."
        : "让整个窗口透出桌面。仅支持 Windows 的系统背景效果。";

    public string ArchivedConversationsTitle => Language == "en-US" ? "Archived chats" : "已归档对话";
    public string ArchivedConversationsDescription => Language == "en-US"
        ? "Chats archived from the sidebar are hidden from active lists. Restore them here when you need them again."
        : "从侧栏归档的对话会从活跃列表隐藏；需要时可以在这里恢复。";
    public string ArchiveSearchPlaceholder => Language == "en-US" ? "Search archived chats..." : "搜索已归档对话...";
    public string ArchivedCountText => Language == "en-US" ? $"{ArchivedSessions.Count} archived" : $"{ArchivedSessions.Count} 个归档";
    public string RestoreAllArchivedText => Language == "en-US" ? "Restore all" : "全部恢复";
    public string DeleteAllArchivedText => Language == "en-US" ? "Delete all" : "全部删除";
    public string CleanMissingArchivedText => Language == "en-US" ? "Clean missing" : "清理缺失";
    public string ArchivedEmptyTitle => Language == "en-US" ? "No archived chats" : "暂无已归档对话";
    public string ArchivedEmptyDescription => Language == "en-US"
        ? "Use the archive button on a chat row to move it here. The session file stays on disk."
        : "在对话行点击归档后会出现在这里；session 文件仍保留在本机磁盘。";

    public string ModelsTitle => Language == "en-US" ? "Models" : "模型";
    public string ModelsDescription => Language == "en-US"
        ? "Manage Pi model providers from the local models.json file. Available providers come from Pi's model registry."
        : "管理本机 Pi 的模型 provider。可用模型来自 Pi registry，自定义 provider 写入 models.json。";
    public string ModelsPathText => $"~/.pi/agent/models.json · {_pi.ModelsPath}";
    public string AddProviderText => Language == "en-US" ? "+ Add provider" : "+ 添加 provider";
    public string ProviderSearchPlaceholder => Language == "en-US" ? "Search providers..." : "搜索 provider...";
    public string ModelProviderEmptyTitle => Language == "en-US" ? "No providers configured" : "暂无已配置 provider";
    public string ModelProviderEmptyDescription => Language == "en-US"
        ? "Add a local or compatible provider, or sign in through Pi so registry models become available."
        : "添加本地/兼容 provider，或通过 Pi 登录后让 registry 模型出现在这里。";
    public string SelectedProviderTitle => _selectedModelProvider?.Title ?? (Language == "en-US" ? "Select a provider" : "选择一个 provider");
    public string SelectedProviderDetail => _selectedModelProvider?.Detail ?? (Language == "en-US" ? "Select a provider or model" : "选择 provider 或模型");
    public string ModelDetailEmptyTitle => Language == "en-US" ? "Select a provider or model" : "选择 provider 或模型";
    public string ModelDetailEmptyDescription => Language == "en-US" ? "The model list will appear here." : "模型列表会显示在这里。";
    public string AddProviderTitle => Language == "en-US" ? "Add provider" : "添加 provider";
    public string AddProviderDescription => Language == "en-US"
        ? "Choose a real template, then save it to models.json. API keys should usually be environment variables."
        : "选择真实模板后写入 models.json。API key 建议使用环境变量引用。";
    public string CustomProviderFormTitle => Language == "en-US" ? "Provider config" : "Provider 配置";
    public string NewProviderIdLabel => Language == "en-US" ? "Provider id" : "Provider ID";
    public string NewProviderBaseUrlLabel => Language == "en-US" ? "Base URL" : "Base URL";
    public string NewProviderApiLabel => Language == "en-US" ? "API type" : "API 类型";
    public string NewProviderApiKeyRefLabel => Language == "en-US" ? "API key reference" : "API key 引用";
    public string NewProviderModelsLabel => Language == "en-US" ? "Model ids, one per line" : "模型 ID，每行一个";
    public string SaveProviderText => Language == "en-US" ? "Save to models.json" : "保存到 models.json";
    public string CancelText => Language == "en-US" ? "Cancel" : "取消";
    public string ProviderCountText => Language == "en-US" ? $"{ModelProviders.Count} providers" : $"{ModelProviders.Count} 个 provider";

    public string SkillsTitle => Language == "en-US" ? "Skills" : "技能";
    public string SkillsDescription => Language == "en-US"
        ? $"Global skill settings · {_pi.SkillsSettingsPath}. Sources are discovered from the resolved agent, packages, and configured folders."
        : $"全局技能设置 · {_pi.SkillsSettingsPath}。来源会从当前 agent、package 和本机配置目录发现。";
    public string SkillsSummary => Language == "en-US" ? $"{SkillItems.Count(item => item.IsEnabled)} / {SkillItems.Count} enabled" : $"启用 {SkillItems.Count(item => item.IsEnabled)} / {SkillItems.Count}";
    public string SelectedSkillSourceTitle
    {
        get
        {
            var source = SkillSources.FirstOrDefault(item => PathsEqual(item.Path, _selectedSkillSourcePath));
            return source is null
                ? Language == "en-US" ? "Select a source" : "选择来源"
                : Language == "en-US" ? $"{source.Label} skills" : $"{source.Label} 的技能";
        }
    }
    public string AddSkillSourceText => Language == "en-US" ? "Add source" : "添加来源";

    public string PluginPackagesTitle => Language == "en-US" ? "Packages" : "插件包";
    public string PluginPackagesDescription => Language == "en-US"
        ? "Pi packages can provide extensions, skills, prompt templates, and themes. Install, update, remove, enable, disable, and inspect package resources here."
        : "Pi package 可以提供扩展、技能、提示模板和主题。这里可以安装、更新、移除、启用/禁用并查看 package 资源。";
    public string PluginPackagesSummary => Language == "en-US"
        ? $"{SettingsPluginPackages.Count(item => !item.Disabled)} / {SettingsPluginPackages.Count} enabled"
        : $"启用 {SettingsPluginPackages.Count(item => !item.Disabled)} / {SettingsPluginPackages.Count}";
    public string PluginPackagesSettingsPath => _pi.SettingsPath;
    public string SelectedPluginPackageTitle => _selectedSettingsPluginPackage?.Title ?? (Language == "en-US" ? "Select a package" : "选择 package");
    public string SelectedPluginPackageSource => _selectedSettingsPluginPackage?.Source ?? "-";
    public string SelectedPluginPackageStatus => _selectedSettingsPluginPackage?.StatusText ?? "-";
    public string SelectedPluginPackageVersion => _selectedSettingsPluginPackage?.VersionText ?? "-";
    public string SelectedPluginPackageName => _selectedSettingsPluginPackage?.PackageName ?? "-";
    public string SelectedPluginPackageResources => _selectedSettingsPluginPackage?.ResourceSummary ?? "-";
    public string SelectedPluginPackagePath => _selectedSettingsPluginPackage?.InstalledPath ?? "-";
    public string SelectedPluginPackageScope => _selectedSettingsPluginPackage?.ScopeText ?? "-";
    public string SelectedPluginPackageUninstallTarget => BuildSelectedPackageUninstallTarget();
    public bool SelectedPluginPackageHasManagedInstall => _selectedSettingsPluginPackage is not null && IsManagedPackageInstallPath(_selectedSettingsPluginPackage.InstalledPath);
    public bool IsPackageActionRunning { get => _isPackageActionRunning; private set { if (_isPackageActionRunning == value) return; _isPackageActionRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(PackageActionButtonEnabled)); OnPropertyChanged(nameof(SelectedPackageActionButtonEnabled)); } }
    public bool IsPackageAddOpen { get => _isPackageAddOpen; private set { if (_isPackageAddOpen == value) return; _isPackageAddOpen = value; OnPropertyChanged(); OnPropertyChanged(nameof(PackageAddVisibility)); } }
    public string PackageNewSource { get => _packageNewSource; set { if (_packageNewSource == value) return; _packageNewSource = value; OnPropertyChanged(); } }
    public string PackageInstallScope { get => _packageInstallScope; set { const string next = "global"; if (_packageInstallScope == next) return; _packageInstallScope = next; OnPropertyChanged(); OnPropertyChanged(nameof(PackageInstallScopeDetail)); } }
    public string PackageInstallScopeDetail => Language == "en-US" ? $"Global scope · {_pi.SettingsPath}" : $"全局范围 · {_pi.SettingsPath}";
    public string PackageGlobalScopeText => Language == "en-US" ? "Global" : "全局";
    public string WorkspacePath => LoadWorkspacePath();
    public string PackageActionStatus { get => _packageActionStatus; private set { if (_packageActionStatus == value) return; _packageActionStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(PackageActionStatusVisibility)); } }
    public bool PackageActionButtonEnabled => !IsPackageActionRunning;
    public bool SelectedPluginPackageIsReadOnly => _selectedSettingsPluginPackage?.IsReadOnly == true;
    public bool SelectedPackageActionButtonEnabled => !IsPackageActionRunning && _selectedSettingsPluginPackage is not null && !SelectedPluginPackageIsReadOnly;
    public Visibility PackageAddVisibility => IsPackageAddOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PackageActionStatusVisibility => string.IsNullOrWhiteSpace(PackageActionStatus) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelectedPackageNpmActionVisibility => _selectedSettingsPluginPackage?.IsNpmPackage == true ? Visibility.Visible : Visibility.Collapsed;
    public string PluginPackagesEmptyText => Language == "en-US" ? "settings.json has no packages" : "settings.json 中没有 packages";
    public string PackageStatusLabel => Language == "en-US" ? "Status" : "状态";
    public string PackageVersionLabel => Language == "en-US" ? "Version" : "版本";
    public string PackageResourcesLabel => Language == "en-US" ? "Resources" : "资源";
    public string PackageScopeLabel => Language == "en-US" ? "Scope" : "范围";
    public string PackageAddButtonText => Language == "en-US" ? "Add" : "添加";
    public string PackageUpdateButtonText => Language == "en-US" ? "Update" : "更新";
    public string PackageRemoveButtonText => Language == "en-US" ? "Uninstall" : "卸载";
    public string PackageCancelButtonText => Language == "en-US" ? "Cancel" : "取消";
    public string PackageInstallSourceLabel => Language == "en-US" ? "Package source" : "Package source";
    public string PackageSourcePlaceholder => Language == "en-US" ? "npm:package-name, file:path, or local folder" : "npm:包名、file:路径或本地文件夹";
    public string RefreshPackagesText => Language == "en-US" ? "Refresh" : "刷新";
    public Visibility PluginPackagesEmptyVisibility => SettingsPluginPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PluginPackagesDetailVisibility => SettingsPluginPackages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string RuntimeDiagnosticsTitle => Language == "en-US" ? "Runtime diagnostics" : "运行时诊断";
    public string RuntimeDiagnosticsDescription => Language == "en-US"
        ? "Check local runtime paths, Node, bridge files, provider readiness, and project environment without exposing secrets."
        : "检查本机 runtime 路径、Node、bridge 文件、provider 就绪状态和项目环境；不会显示密钥。";
    public string RuntimeDiagnosticsSummary => Language == "en-US" ? $"{RuntimeDiagnostics.Count} checks" : $"{RuntimeDiagnostics.Count} 项检查";
    public string RefreshDiagnosticsText => Language == "en-US" ? "Refresh" : "刷新";

    public string StoragePathsTitle => Language == "en-US" ? "Storage paths" : "存储路径";
    public string StoragePathsDescription => Language == "en-US"
        ? "Choose where ipi stores app data and local runtime/cache data. Environment variables still take precedence. Restart ipi after saving."
        : "设置 ipi 的应用数据与本地 runtime/cache 数据位置。环境变量仍然优先。保存后重启 ipi 生效。";
    public string AppDataDirTitle => Language == "en-US" ? "App data directory" : "应用数据目录";
    public string AppDataDirDescription => Language == "en-US"
        ? "Config, archive index, default chats, exports, and workspace metadata."
        : "配置、归档索引、默认聊天、导出与工作区元数据。";
    public string LocalAppDataDirTitle => Language == "en-US" ? "Local runtime/cache directory" : "本地运行时/缓存目录";
    public string LocalAppDataDirDescription => Language == "en-US"
        ? "Bundled runtime, npm packages, caches, and generated session images."
        : "内置 runtime、npm packages、缓存与生成的会话图片。";
    public string StorageConfigPathText => Language == "en-US" ? "ipi-paths.json · next to ipi.exe" : "ipi-paths.json · 位于 ipi.exe 同目录";
    public string StorageRestartNotice => Language == "en-US" ? "Saved path changes apply after restarting ipi." : "路径修改保存后需要重启 ipi 才会完全生效。";
    public string BrowseText => Language == "en-US" ? "Browse" : "选择";
    public string RestoreDefaultText => Language == "en-US" ? "Default" : "默认";
    public string SaveStoragePathsText => Language == "en-US" ? "Save paths" : "保存路径";
    public string EffectiveAppDataDir => string.IsNullOrWhiteSpace(AppDataDirOverride) ? IpiPathService.DefaultAppDataDir : AppDataDirOverride;
    public string EffectiveLocalAppDataDir => string.IsNullOrWhiteSpace(LocalAppDataDirOverride) ? IpiPathService.DefaultLocalAppDataDir : LocalAppDataDirOverride;
    public string StorageStatus { get => _storageStatus; private set { if (_storageStatus == value) return; _storageStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(StorageStatusVisibility)); } }
    public Visibility StorageStatusVisibility => string.IsNullOrWhiteSpace(StorageStatus) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsAppearanceSection => _settingsSection == "appearance";
    public bool IsArchiveSection => _settingsSection == "archive";
    public bool IsModelsSection => _settingsSection == "models";
    public bool IsSkillsSection => _settingsSection == "skills";
    public bool IsPackagesSection => _settingsSection == "packages";
    public bool IsStorageSection => _settingsSection == "storage";
    public bool IsDiagnosticsSection => _settingsSection == "diagnostics";
    public bool IsAddingProvider => _isAddingProvider;
    public Visibility AppearancePageVisibility => IsAppearanceSection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ArchivePageVisibility => IsArchiveSection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ModelsPageVisibility => IsModelsSection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SkillsPageVisibility => IsSkillsSection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PluginPackagesPageVisibility => IsPackagesSection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StoragePathsPageVisibility => IsStorageSection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuntimeDiagnosticsPageVisibility => IsDiagnosticsSection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ArchivedEmptyVisibility => ArchivedSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ArchivedListVisibility => ArchivedSessions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelProviderEmptyVisibility => ModelProviders.Count == 0 && !IsAddingProvider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ModelProviderListVisibility => ModelProviders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelProviderDetailVisibility => !IsAddingProvider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddProviderVisibility => IsAddingProvider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProviderGalleryVisibility => IsAddingProvider && !_isProviderConfigVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProviderConfigVisibility => IsAddingProvider && _isProviderConfigVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NewProviderModelsVisibility => _selectedProviderTemplate is { IsCatalogProvider: true } ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelectedProviderModelsVisibility => SelectedProviderModels.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelDetailEmptyVisibility => SelectedProviderModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NewProviderStatusVisibility => string.IsNullOrWhiteSpace(NewProviderStatus) ? Visibility.Collapsed : Visibility.Visible;

    public string ArchiveSearchText
    {
        get => _archiveSearchText;
        set
        {
            if (_archiveSearchText == value) return;
            _archiveSearchText = value;
            OnPropertyChanged();
            RefreshArchivedSessions();
        }
    }

    public string ProviderSearchText
    {
        get => _providerSearchText;
        set
        {
            if (_providerSearchText == value) return;
            _providerSearchText = value;
            OnPropertyChanged();
            RebuildFilteredProviderTemplates();
        }
    }

    public string NewProviderId { get => _newProviderId; set { if (_newProviderId == value) return; _newProviderId = value; OnPropertyChanged(); } }
    public string NewProviderBaseUrl { get => _newProviderBaseUrl; set { if (_newProviderBaseUrl == value) return; _newProviderBaseUrl = value; OnPropertyChanged(); } }
    public string NewProviderApi { get => _newProviderApi; set { if (_newProviderApi == value) return; _newProviderApi = value; OnPropertyChanged(); } }
    public string NewProviderApiKeyRef { get => _newProviderApiKeyRef; set { if (_newProviderApiKeyRef == value) return; _newProviderApiKeyRef = value; OnPropertyChanged(); } }
    public string NewProviderModelIds { get => _newProviderModelIds; set { if (_newProviderModelIds == value) return; _newProviderModelIds = value; OnPropertyChanged(); } }
    public string NewProviderStatus
    {
        get => _newProviderStatus;
        private set
        {
            if (_newProviderStatus == value) return;
            _newProviderStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewProviderStatusVisibility));
        }
    }

    public string Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
            RefreshSelectionState();
            SaveAndNotify();
        }
    }

    public string Theme
    {
        get => _theme;
        private set
        {
            if (_theme == value) return;
            _theme = value;
            OnPropertyChanged();
            RefreshSelectionState();
            SaveAndNotify();
        }
    }

    public string ThemeSearchText
    {
        get => _themeSearchText;
        set
        {
            if (_themeSearchText == value) return;
            _themeSearchText = value;
            OnPropertyChanged();
            RebuildFilteredThemes();
        }
    }

    public double WindowTransparency
    {
        get => _windowTransparency;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 0, 70));
            if (Math.Abs(_windowTransparency - normalized) < 0.1) return;
            _windowTransparency = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTransparencyLabel));
            SaveAndNotify();
        }
    }

    public string WindowTransparencyLabel => $"{WindowTransparency:0}%";
    public bool IsLanguageMenuOpen { get => _isLanguageMenuOpen; set { _isLanguageMenuOpen = value; OnPropertyChanged(); } }

    public string AppDataDirOverride
    {
        get => _appDataDirOverride;
        set
        {
            if (_appDataDirOverride == value) return;
            _appDataDirOverride = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveAppDataDir));
        }
    }

    public string LocalAppDataDirOverride
    {
        get => _localAppDataDirOverride;
        set
        {
            if (_localAppDataDirOverride == value) return;
            _localAppDataDirOverride = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveLocalAppDataDir));
        }
    }

    public void ToggleLanguageMenu() => IsLanguageMenuOpen = !IsLanguageMenuOpen;

    public void SelectLanguage(LanguageOption option)
    {
        Language = option.Key;
        IsLanguageMenuOpen = false;
    }

    public void SelectMode(ModeOption option) => Mode = option.Key;

    public void CycleMode()
    {
        Mode = Mode switch
        {
            "light" => "dark",
            "dark" => "system",
            _ => "light",
        };
    }

    public void ToggleLightDarkMode()
    {
        Mode = Mode == "dark" ? "light" : "dark";
    }

    public void SelectTheme(ThemeCardItem theme) => Theme = theme.Key;

    public void ResetAppDataDir() => AppDataDirOverride = string.Empty;

    public void ResetLocalAppDataDir() => LocalAppDataDirOverride = string.Empty;

    public void SaveStoragePaths()
    {
        try
        {
            var settings = new IpiPathSettings(AppDataDirOverride, LocalAppDataDirOverride).Normalize();
            _pathSettings.Save(settings);
            AppDataDirOverride = settings.AppDataDir ?? string.Empty;
            LocalAppDataDirOverride = settings.LocalAppDataDir ?? string.Empty;
            StorageStatus = Language == "en-US" ? "Saved. Restart ipi to use the new paths." : "已保存。重启 ipi 后使用新路径。";
        }
        catch (Exception ex)
        {
            StorageStatus = Language == "en-US" ? $"Unable to save paths: {ex.Message}" : $"无法保存路径：{ex.Message}";
        }
    }

    public void SelectSection(string section)
    {
        var normalized = section is "archive" or "models" or "skills" or "packages" or "storage" or "diagnostics" ? section : "appearance";
        if (_settingsSection == normalized) return;
        _settingsSection = normalized;
        if (normalized == "models") _ = LoadRegistryModelsAsync();
        if (normalized == "skills") RefreshSkills();
        if (normalized == "packages") RefreshPluginPackages();
        if (normalized == "diagnostics") RefreshRuntimeDiagnostics();
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsArchiveSection));
        OnPropertyChanged(nameof(IsModelsSection));
        OnPropertyChanged(nameof(IsSkillsSection));
        OnPropertyChanged(nameof(IsPackagesSection));
        OnPropertyChanged(nameof(IsStorageSection));
        OnPropertyChanged(nameof(IsDiagnosticsSection));
        OnPropertyChanged(nameof(AppearancePageVisibility));
        OnPropertyChanged(nameof(ArchivePageVisibility));
        OnPropertyChanged(nameof(ModelsPageVisibility));
        OnPropertyChanged(nameof(SkillsPageVisibility));
        OnPropertyChanged(nameof(PluginPackagesPageVisibility));
        OnPropertyChanged(nameof(StoragePathsPageVisibility));
        OnPropertyChanged(nameof(RuntimeDiagnosticsPageVisibility));
    }

    public void RefreshSkills()
    {
        var previousSource = _selectedSkillSourcePath;
        SkillSources.Clear();
        SkillItems.Clear();
        var english = Language == "en-US";
        var sources = _pi.ListSkillSources().ToList();
        if (string.IsNullOrWhiteSpace(previousSource) || sources.All(source => !PathsEqual(source.Path, previousSource)))
        {
            previousSource = sources.FirstOrDefault()?.Path ?? string.Empty;
        }
        _selectedSkillSourcePath = previousSource;
        foreach (var source in sources) SkillSources.Add(new SettingsSkillSourceItem(source, english, PathsEqual(source.Path, _selectedSkillSourcePath)));
        foreach (var skill in _pi.ListSkills(800).Where(skill => PathsEqual(skill.SourcePath, _selectedSkillSourcePath))) SkillItems.Add(new SettingsSkillItem(skill, english));
        OnPropertyChanged(nameof(SkillsSummary));
        OnPropertyChanged(nameof(SelectedSkillSourceTitle));
    }

    public void SelectSkillSource(SettingsSkillSourceItem item)
    {
        if (PathsEqual(_selectedSkillSourcePath, item.Path)) return;
        _selectedSkillSourcePath = item.Path;
        RefreshSkills();
    }

    public void ToggleSkillSource(SettingsSkillSourceItem item)
    {
        _pi.SetSkillSourceEnabled(item.Path, !item.IsEnabled);
        _selectedSkillSourcePath = item.Path;
        RefreshSkills();
    }

    public void ToggleSkill(SettingsSkillItem item)
    {
        if (!item.SourceEnabled) return;
        _pi.SetSkillEnabled(item.Path, !item.IsEnabled);
        RefreshSkills();
    }

    public bool AddSkillSource(string path)
    {
        var added = _pi.AddSkillSource(path);
        RefreshSkills();
        return added;
    }

    public async void RefreshPluginPackages()
    {
        await RefreshPluginPackagesAsync();
    }

    private async Task RefreshPluginPackagesAsync(string? preferredSource = null)
    {
        var operation = TryBeginPackageOperation();
        if (operation is null) return;
        var previousSource = preferredSource ?? _selectedSettingsPluginPackage?.Source;
        IsPackageActionRunning = true;
        try
        {
            var packages = await _packageBridge.ListPackagesAsync(WorkspacePath, _pi.AgentDir, operation.Token);
            SettingsPluginPackages.Clear();
            SettingsPluginResourceGroups.Clear();
            foreach (var package in packages) SettingsPluginPackages.Add(new PluginPackageViewItem(package, Language == "en-US"));
            var selected = SettingsPluginPackages.FirstOrDefault(item => item.Source.Equals(previousSource ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? SettingsPluginPackages.FirstOrDefault();
            SelectPluginPackage(selected);
            OnPropertyChanged(nameof(PluginPackagesSummary));
            OnPropertyChanged(nameof(PluginPackagesEmptyVisibility));
            OnPropertyChanged(nameof(PluginPackagesDetailVisibility));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            PackageActionStatus = Language == "en-US" ? "Package refresh cancelled" : "插件包刷新已取消";
        }
        catch (Exception ex)
        {
            PackageActionStatus = ex.Message;
            SettingsPluginPackages.Clear();
            SettingsPluginResourceGroups.Clear();
            SelectPluginPackage(null);
        }
        finally
        {
            CompletePackageOperation(operation);
            IsPackageActionRunning = false;
        }
    }

    public void SelectPluginPackage(PluginPackageViewItem? item)
    {
        _selectedSettingsPluginPackage = item;
        foreach (var package in SettingsPluginPackages) package.IsSelected = ReferenceEquals(package, item);
        SettingsPluginResourceGroups.Clear();
        if (item is not null)
        {
            foreach (var group in item.Resources.GroupBy(resource => resource.Kind).OrderBy(group => PluginKindOrder(group.Key)))
            {
                SettingsPluginResourceGroups.Add(new PluginResourceGroupViewItem(PluginKindTitle(group.Key), group.Select(resource => new PluginResourceViewItem(resource.Name, resource.Path))));
            }
        }
        NotifyPluginPackagePropertiesChanged();
    }

    private void RebuildPackageItems(IReadOnlyList<PluginPackageRecord> packages, string? preferredSource)
    {
        SettingsPluginPackages.Clear();
        SettingsPluginResourceGroups.Clear();
        foreach (var package in packages) SettingsPluginPackages.Add(new PluginPackageViewItem(package, Language == "en-US"));
        var selected = !string.IsNullOrWhiteSpace(preferredSource)
            ? SettingsPluginPackages.FirstOrDefault(item => item.Source.Equals(preferredSource, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectPluginPackage(selected ?? SettingsPluginPackages.FirstOrDefault());
        OnPropertyChanged(nameof(PluginPackagesSummary));
        OnPropertyChanged(nameof(PluginPackagesEmptyVisibility));
        OnPropertyChanged(nameof(PluginPackagesDetailVisibility));
    }

    public async void TogglePluginPackage(PluginPackageViewItem item)
    {
        if (IsPackageActionRunning) return;
        if (item.IsReadOnly)
        {
            PackageActionStatus = Language == "en-US" ? "Project packages are read-only until workspace trust is implemented." : "在实现工作区信任前，项目 package 仅供查看。";
            return;
        }
        var operation = TryBeginPackageOperation();
        if (operation is null) return;
        IsPackageActionRunning = true;
        try
        {
            var nextEnabled = item.Disabled;
            PackageActionStatus = nextEnabled ? Language == "en-US" ? "Enabling package..." : "正在启用插件包..." : Language == "en-US" ? "Disabling package..." : "正在禁用插件包...";
            var packages = await _packageBridge.SetEnabledAsync(WorkspacePath, _pi.AgentDir, item.Source, item.Scope, nextEnabled, progress => PackageActionStatus = progress, operation.Token);
            RebuildPackageItems(packages, item.Source);
            PackageActionStatus = nextEnabled ? Language == "en-US" ? "Package enabled" : "插件包已启用" : Language == "en-US" ? "Package disabled" : "插件包已禁用";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            PackageActionStatus = Language == "en-US" ? "Package operation cancelled" : "插件包操作已取消";
        }
        catch (Exception ex)
        {
            PackageActionStatus = ex.Message;
        }
        finally
        {
            CompletePackageOperation(operation);
            IsPackageActionRunning = false;
        }
    }

    public void OpenAddPackage()
    {
        if (string.IsNullOrWhiteSpace(PackageNewSource)) PackageNewSource = "npm:";
        PackageActionStatus = string.Empty;
        IsPackageAddOpen = true;
    }

    public void CancelAddPackage()
    {
        IsPackageAddOpen = false;
        PackageActionStatus = string.Empty;
    }

    public async Task AddPackageAsync()
    {
        if (IsPackageActionRunning) return;
        var source = PackageNewSource.Trim();
        if (string.IsNullOrWhiteSpace(source) || source.Equals("npm:", StringComparison.OrdinalIgnoreCase))
        {
            PackageActionStatus = Language == "en-US" ? "Enter a package source, for example npm:my-pi-package" : "请输入 package source，例如 npm:my-pi-package";
            return;
        }
        var operation = TryBeginPackageOperation();
        if (operation is null) return;
        IsPackageActionRunning = true;
        try
        {
            PackageActionStatus = Language == "en-US" ? $"Installing {source}" : $"正在安装 {source}";
            var packages = await _packageBridge.InstallAsync(WorkspacePath, _pi.AgentDir, source, "global", progress => PackageActionStatus = progress, operation.Token);
            IsPackageAddOpen = false;
            RebuildPackageItems(packages, source);
            PackageActionStatus = Language == "en-US" ? "Package installed" : "插件包已安装";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            PackageActionStatus = Language == "en-US" ? "Package installation cancelled" : "插件包安装已取消";
        }
        catch (Exception ex)
        {
            PackageActionStatus = ex.Message;
        }
        finally
        {
            CompletePackageOperation(operation);
            IsPackageActionRunning = false;
        }
    }

    public async Task UpdateSelectedPackageAsync()
    {
        var selected = _selectedSettingsPluginPackage;
        if (selected is null || IsPackageActionRunning) return;
        if (selected.IsReadOnly)
        {
            PackageActionStatus = Language == "en-US" ? "Project packages are read-only until workspace trust is implemented." : "在实现工作区信任前，项目 package 仅供查看。";
            return;
        }
        var operation = TryBeginPackageOperation();
        if (operation is null) return;
        IsPackageActionRunning = true;
        try
        {
            PackageActionStatus = Language == "en-US" ? $"Updating {selected.Source}" : $"正在更新 {selected.Source}";
            var packages = await _packageBridge.UpdateAsync(WorkspacePath, _pi.AgentDir, selected.Source, selected.Scope, progress => PackageActionStatus = progress, operation.Token);
            RebuildPackageItems(packages, selected.Source);
            PackageActionStatus = Language == "en-US" ? "Package updated" : "插件包已更新";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            PackageActionStatus = Language == "en-US" ? "Package update cancelled" : "插件包更新已取消";
        }
        catch (Exception ex)
        {
            PackageActionStatus = ex.Message;
        }
        finally
        {
            CompletePackageOperation(operation);
            IsPackageActionRunning = false;
        }
    }

    public async Task RemoveSelectedPackageAsync()
    {
        var selected = _selectedSettingsPluginPackage;
        if (selected is null || IsPackageActionRunning) return;
        if (selected.IsReadOnly)
        {
            PackageActionStatus = Language == "en-US" ? "Project packages are read-only until workspace trust is implemented." : "在实现工作区信任前，项目 package 仅供查看。";
            return;
        }
        var operation = TryBeginPackageOperation();
        if (operation is null) return;
        IsPackageActionRunning = true;
        try
        {
            PackageActionStatus = Language == "en-US" ? $"Uninstalling {selected.Source}" : $"正在卸载 {selected.Source}";
            var packages = await _packageBridge.RemoveAsync(WorkspacePath, _pi.AgentDir, selected.Source, selected.Scope, progress => PackageActionStatus = progress, operation.Token);
            RebuildPackageItems(packages, null);
            PackageActionStatus = selected.Source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Directory.Exists(selected.Source)
                ? Language == "en-US" ? "Local package reference removed. Local source folder was not deleted." : "本地 package 引用已移除；本地源目录未删除。"
                : Language == "en-US" ? "Package uninstalled and removed from settings." : "插件包已卸载，并已从设置移除。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            PackageActionStatus = Language == "en-US" ? "Package removal cancelled" : "插件包卸载已取消";
        }
        catch (Exception ex)
        {
            PackageActionStatus = ex.Message;
        }
        finally
        {
            CompletePackageOperation(operation);
            IsPackageActionRunning = false;
        }
    }

    private CancellationTokenSource? TryBeginPackageOperation()
    {
        lock (_packageOperationGate)
        {
            if (_isDisposed || _packageOperationCancellation is not null) return null;
            _packageOperationCancellation = new CancellationTokenSource();
            return _packageOperationCancellation;
        }
    }

    private void CompletePackageOperation(CancellationTokenSource operation)
    {
        lock (_packageOperationGate)
        {
            if (ReferenceEquals(_packageOperationCancellation, operation)) _packageOperationCancellation = null;
        }
        operation.Dispose();
    }

    public void CancelActiveOperations()
    {
        CancellationTokenSource? operation;
        lock (_packageOperationGate) operation = _packageOperationCancellation;
        try { operation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_packageOperationGate) _isDisposed = true;
        CancelActiveOperations();
    }

    private string BuildSelectedPackageUninstallTarget()
    {
        var selected = _selectedSettingsPluginPackage;
        if (selected is null) return "-";
        if (selected.IsNpmPackage) return string.IsNullOrWhiteSpace(selected.InstalledPath)
            ? $"npm uninstall {selected.PackageName}"
            : $"npm uninstall {selected.PackageName}\n{selected.InstalledPath}";
        if (IsManagedPackageInstallPath(selected.InstalledPath)) return selected.InstalledPath;
        return Language == "en-US"
            ? "No managed install directory will be deleted. This local/file source will be removed from settings.json only."
            : "不会删除受管安装目录之外的本地/file 源目录；只会从 settings.json 移除引用。";
    }

    private bool IsManagedPackageInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var managedRoots = new[]
        {
            _pi.NpmPackagesDir,
            Path.Combine(_pi.AgentDir, "npm", "node_modules"),
            Path.Combine(_pi.AgentDir, "git")
        };
        return managedRoots.Any(root => IsUnderDirectory(path, root));
    }

    private static bool IsUnderDirectory(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }


    private static int PluginKindOrder(string kind) => kind switch
    {
        "extension" => 0,
        "skill" => 1,
        "prompt" => 2,
        "theme" => 3,
        _ => 9
    };

    private string PluginKindTitle(string kind) => kind switch
    {
        "extension" => Language == "en-US" ? "Extensions" : "扩展",
        "skill" => Language == "en-US" ? "Skills" : "技能",
        "prompt" => Language == "en-US" ? "Prompt templates" : "提示模板",
        "theme" => Language == "en-US" ? "Themes" : "主题",
        _ => kind
    };

    private void NotifyPluginPackagePropertiesChanged()
    {
        foreach (var property in new[]
        {
            nameof(PluginPackagesSummary), nameof(SelectedPluginPackageTitle), nameof(SelectedPluginPackageSource), nameof(SelectedPluginPackageStatus),
            nameof(SelectedPluginPackageVersion), nameof(SelectedPluginPackageName), nameof(SelectedPluginPackageResources), nameof(SelectedPluginPackagePath),
            nameof(SelectedPluginPackageScope), nameof(SelectedPluginPackageUninstallTarget), nameof(SelectedPluginPackageHasManagedInstall), nameof(SelectedPluginPackageIsReadOnly), nameof(SelectedPackageActionButtonEnabled), nameof(SelectedPackageNpmActionVisibility), nameof(PluginPackagesEmptyVisibility), nameof(PluginPackagesDetailVisibility),
            nameof(PackageActionStatus), nameof(PackageActionStatusVisibility), nameof(PackageAddVisibility), nameof(PackageActionButtonEnabled)
        }) OnPropertyChanged(property);
    }

    public void RefreshRuntimeDiagnostics()
    {
        RuntimeDiagnostics.Clear();
        var settings = _pi.ReadSettingsSummary();
        var runtime = _pi.RuntimeInfo;
        AddDiagnostic("info", Language == "en-US" ? "Runtime mode" : "运行时模式", Language == "en-US"
            ? $"{runtime.RuntimeMode} · bundled: {YesNo(runtime.IsBundled)} · initialized: {YesNo(runtime.IsInitialized)}"
            : $"{runtime.RuntimeMode} · 内置: {YesNo(runtime.IsBundled)} · 已初始化: {YesNo(runtime.IsInitialized)}");
        AddPathDiagnostic(Language == "en-US" ? "Agent directory" : "Agent 目录", _pi.AgentDir, true);
        AddPathDiagnostic("settings.json", _pi.SettingsPath, false);
        AddPathDiagnostic("models.json", _pi.ModelsPath, false);
        AddPathDiagnostic(Language == "en-US" ? "Sessions directory" : "Sessions 目录", _pi.SessionsDir, true);
        foreach (var source in _pi.ListSkillSources())
        {
            AddPathDiagnostic(Language == "en-US" ? $"Skill source · {source.Label}" : $"Skill 来源 · {source.Label}", source.Path, true);
        }
        AddPathDiagnostic(Language == "en-US" ? "npm directory" : "npm 目录", runtime.NpmPackagesDir, true);
        if (!string.IsNullOrWhiteSpace(runtime.PiCodingAgentRoot)) AddPathDiagnostic(Language == "en-US" ? "Pi package root" : "Pi 包根目录", runtime.PiCodingAgentRoot, true);
        else AddDiagnostic("warn", Language == "en-US" ? "Pi package root" : "Pi 包根目录", Language == "en-US" ? "not found; run setup to install the upstream Pi package" : "未找到；运行 setup 安装上游 Pi 包");
        AddPathDiagnostic("agent-bridge.mjs", Path.Combine(AppContext.BaseDirectory, "agent-bridge.mjs"), false);

        var nodeProbe = ProbeNodeVersion();
        AddDiagnostic(nodeProbe.Level, Language == "en-US" ? "Node runtime" : "Node 运行时", nodeProbe.Detail);

        var providerReady = IsConfiguredValue(settings.DefaultProvider) && IsConfiguredValue(settings.DefaultModel);
        AddDiagnostic(providerReady ? "ok" : "warn", Language == "en-US" ? "Default model" : "默认模型", providerReady
            ? $"{settings.DefaultProvider}/{settings.DefaultModel} · thinking: {settings.DefaultThinking}"
            : Language == "en-US" ? "Default provider/model is not set. Complete provider onboarding before real chat." : "未设置默认 provider/model；真实聊天前需要完成 provider onboarding。");

        var allLocalModels = _pi.ReadModelOptions(settings).ToList();
        var modelsJsonModels = allLocalModels.Where(model => model.Source.Equals("models.json", StringComparison.OrdinalIgnoreCase)).ToList();
        var modelsJsonProviderCount = modelsJsonModels.Select(model => model.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var registryProviderCount = _registryModels.Select(model => model.Provider).Where(provider => !string.IsNullOrWhiteSpace(provider)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var hasDefaultProvider = IsConfiguredValue(settings.DefaultProvider) && IsConfiguredValue(settings.DefaultModel);
        var hasAnyProviderEvidence = hasDefaultProvider || modelsJsonProviderCount > 0 || registryProviderCount > 0;
        var providerDetail = modelsJsonProviderCount > 0
            ? (Language == "en-US"
                ? $"models.json has {modelsJsonProviderCount} custom provider(s) and {modelsJsonModels.Count} model(s). API keys are not displayed."
                : $"models.json 中发现 {modelsJsonProviderCount} 个自定义 provider、{modelsJsonModels.Count} 个 model。不会显示 API key。")
            : hasDefaultProvider
                ? (Language == "en-US"
                    ? $"Using default provider/model from settings or OAuth registry: {settings.DefaultProvider}/{settings.DefaultModel}. models.json has no custom provider, which is OK."
                    : $"正在使用 settings/OAuth registry 中的默认 provider/model：{settings.DefaultProvider}/{settings.DefaultModel}。models.json 没有自定义 provider 是正常的。")
                : registryProviderCount > 0
                    ? (Language == "en-US" ? $"Registry reports {registryProviderCount} provider(s)." : $"registry 中发现 {registryProviderCount} 个 provider。")
                    : (Language == "en-US" ? "No provider evidence found yet." : "暂未发现 provider 配置证据。");
        AddDiagnostic(hasAnyProviderEvidence ? "ok" : "warn", Language == "en-US" ? "Provider configuration" : "Provider 配置", providerDetail);

        AddDiagnostic("info", Language == "en-US" ? "Authentication check" : "认证检查", Language == "en-US"
            ? "Diagnostics does not read or display secrets; the local agent runtime validates auth."
            : "诊断页不会读取或显示密钥；认证由本地 agent runtime 验证。");

        var sessionCount = _pi.ListSessions(500).Count;
        var skills = _pi.ListSkills(800);
        var skillCount = skills.Count;
        var enabledSkillCount = skills.Count(skill => skill.IsEnabled);
        var packageCount = _pi.ListPackages().Count;
        AddDiagnostic(sessionCount > 0 ? "ok" : "info", Language == "en-US" ? "Session index" : "会话索引", Language == "en-US" ? $"Loaded {sessionCount} session(s)." : $"已加载 {sessionCount} 个会话。");
        AddDiagnostic(skillCount > 0 ? "ok" : "warn", Language == "en-US" ? "Skill index" : "技能索引", Language == "en-US" ? $"Discovered {skillCount} skill(s), {enabledSkillCount} enabled." : $"已发现 {skillCount} 个 skill，启用 {enabledSkillCount} 个。");
        AddDiagnostic("info", Language == "en-US" ? "Plugin packages" : "插件包", Language == "en-US" ? $"Read {packageCount} package source(s)." : $"已读取 {packageCount} 个 package source。");

        var cwd = Environment.CurrentDirectory;
        AddDiagnostic(Directory.Exists(cwd) ? "ok" : "error", Language == "en-US" ? "Current working directory" : "当前工作目录", cwd);
        var gitRoot = ResolveGitRoot(cwd);
        if (string.IsNullOrWhiteSpace(gitRoot)) AddDiagnostic("info", "Git", Language == "en-US" ? "Current working directory is not inside a Git repository." : "当前工作目录不在 Git 仓库中。");
        else
        {
            var worktrees = RunGit(gitRoot, "worktree", "list", "--porcelain");
            var count = worktrees.ExitCode == 0 ? worktrees.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith("worktree ", StringComparison.OrdinalIgnoreCase)) : 0;
            AddDiagnostic("ok", "Git", Language == "en-US" ? $"root: {gitRoot} · worktrees: {count}" : $"root: {gitRoot} · worktrees: {count}");
        }

        OnPropertyChanged(nameof(RuntimeDiagnosticsSummary));
    }

    private void AddPathDiagnostic(string title, string path, bool isDirectory)
    {
        var exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
        AddDiagnostic(exists ? "ok" : "error", title, exists ? SafePathSummary(path, isDirectory) : (Language == "en-US" ? $"missing · {path}" : $"缺失 · {path}"));
    }

    private void AddDiagnostic(string level, string title, string detail)
    {
        RuntimeDiagnostics.Add(new SettingsRuntimeDiagnosticItem(level, BadgeFor(level), title, detail));
    }

    private (string Level, string Detail) ProbeNodeVersion()
    {
        var label = _pi.RuntimeInfo.NodePath ?? "PATH: node";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pi.RuntimeInfo.NodePath ?? "node",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi);
            if (process is null) return ("error", Language == "en-US" ? $"Unable to start Node · {label}" : $"无法启动 Node · {label}");
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return ("error", Language == "en-US" ? $"Node check timed out · {label}" : $"Node 检查超时 · {label}");
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            return process.ExitCode == 0 ? ("ok", $"{label} · {output}") : ("error", $"{label} · {error}");
        }
        catch (Exception ex)
        {
            return ("error", $"{label} · {ex.Message}");
        }
    }

    private string? ResolveGitRoot(string path)
    {
        var result = RunGit(path, "rev-parse", "--show-toplevel");
        if (result.ExitCode != 0) return null;
        var root = result.Output.Trim();
        return Directory.Exists(root) ? root : null;
    }

    private static SettingsGitCommandResult RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return new SettingsGitCommandResult(-1, "", "git failed to start");
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new SettingsGitCommandResult(-1, "", "git command timed out");
            }
            return new SettingsGitCommandResult(process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
        }
        catch (Exception ex)
        {
            return new SettingsGitCommandResult(-1, "", ex.Message);
        }
    }

    private string SafePathSummary(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var info = new DirectoryInfo(path);
                return Language == "en-US" ? $"exists · {path} · modified {info.LastWriteTime:g}" : $"存在 · {path} · 修改于 {info.LastWriteTime:g}";
            }
            var file = new FileInfo(path);
            return Language == "en-US" ? $"exists · {path} · {FormatSize(file.Length)} · modified {file.LastWriteTime:g}" : $"存在 · {path} · {FormatSize(file.Length)} · 修改于 {file.LastWriteTime:g}";
        }
        catch
        {
            return path;
        }
    }

    private string BadgeFor(string level) => level switch
    {
        "ok" => Language == "en-US" ? "ok" : "通过",
        "warn" => Language == "en-US" ? "warn" : "注意",
        "error" => Language == "en-US" ? "error" : "错误",
        _ => Language == "en-US" ? "info" : "信息",
    };

    private string YesNo(bool value) => value ? (Language == "en-US" ? "yes" : "是") : (Language == "en-US" ? "no" : "否");

    private static bool IsConfiguredValue(string value) => !string.IsNullOrWhiteSpace(value) && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:F1} {units[unit]}";
    }

    public void RestoreArchivedSession(ArchivedSessionViewItem item)
    {
        _archive.Restore(item.FilePath);
    }

    public void RestoreAllArchivedSessions()
    {
        _archive.RestoreAll();
    }

    public ArchiveDeleteResult DeleteArchivedSession(ArchivedSessionViewItem item)
    {
        var result = _archive.DeleteArchivedSession(item.FilePath);
        RefreshArchivedSessions();
        return result;
    }

    public ArchiveDeleteResult DeleteAllArchivedSessions()
    {
        var result = _archive.DeleteAllArchivedSessions();
        RefreshArchivedSessions();
        return result;
    }

    public void CleanMissingArchivedSessions()
    {
        _archive.RemoveMissing();
    }

    public void SelectModelProvider(ModelProviderViewItem item)
    {
        _isAddingProvider = false;
        SetSelectedModelProvider(item.Provider);
        NotifyModelPageStateChanged();
    }

    public void StartAddProvider()
    {
        _isAddingProvider = true;
        _isProviderConfigVisible = false;
        NewProviderStatus = "";
        ProviderSearchText = "";
        _ = LoadProviderCatalogAsync();
        NotifyModelPageStateChanged();
    }

    public void CancelAddProvider()
    {
        if (_isProviderConfigVisible)
        {
            _isProviderConfigVisible = false;
            NewProviderStatus = "";
            foreach (var item in ProviderTemplates) item.IsSelected = false;
            NotifyModelPageStateChanged();
            return;
        }
        _isAddingProvider = false;
        _isProviderConfigVisible = false;
        NewProviderStatus = "";
        NotifyModelPageStateChanged();
    }

    public void SelectProviderTemplate(ProviderTemplateItem template)
    {
        _selectedProviderTemplate = template;
        foreach (var item in ProviderTemplates) item.IsSelected = item.Key == template.Key;
        NewProviderId = template.ProviderId;
        NewProviderBaseUrl = template.BaseUrl;
        NewProviderApi = template.Api;
        NewProviderApiKeyRef = template.ApiKeyRef;
        NewProviderModelIds = template.ModelIds;
        NewProviderStatus = template.IsConfigured
            ? (Language == "en-US" ? "This provider already has auth configured in Pi." : "这个 provider 已在 Pi 中配置认证。")
            : "";
        _isProviderConfigVisible = true;
        NotifyModelPageStateChanged();
    }

    public void SaveNewProvider()
    {
        var providerId = NewProviderId.Trim();
        var baseUrl = NewProviderBaseUrl.Trim();
        var apiKeyReference = string.IsNullOrWhiteSpace(NewProviderApiKeyRef) ? DefaultApiKeyRef(providerId) : NewProviderApiKeyRef.Trim();
        var isBuiltInProvider = _selectedProviderTemplate is { IsCatalogProvider: true };
        var modelIds = NewProviderModelIds
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!Regex.IsMatch(providerId, "^[A-Za-z0-9_.-]+$"))
        {
            NewProviderStatus = Language == "en-US" ? "Provider id can only use letters, numbers, dot, dash, and underscore." : "Provider ID 只能包含字母、数字、点、短横线和下划线。";
            return;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var secureEndpoint)
            || !string.IsNullOrEmpty(secureEndpoint.UserInfo)
            || !(secureEndpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                 || secureEndpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && secureEndpoint.IsLoopback))
        {
            NewProviderStatus = Language == "en-US"
                ? "Remote endpoints require HTTPS; HTTP is loopback-only, and embedded URL credentials are not allowed."
                : "远程地址必须使用 HTTPS；HTTP 仅允许回环地址，且 URL 不得包含凭据。";
            return;
        }
        if (!Regex.IsMatch(apiKeyReference, "^\\$[A-Za-z_][A-Za-z0-9_]*$"))
        {
            NewProviderStatus = Language == "en-US"
                ? "API key must be an environment variable reference such as $OPENAI_API_KEY."
                : "API key 必须使用环境变量引用，例如 $OPENAI_API_KEY。";
            return;
        }
        if (string.IsNullOrWhiteSpace(NewProviderApi))
        {
            NewProviderStatus = Language == "en-US" ? "API type is required." : "必须填写 API 类型。";
            return;
        }
        if (!isBuiltInProvider && modelIds.Count == 0)
        {
            NewProviderStatus = Language == "en-US" ? "Add at least one model id." : "至少添加一个模型 ID。";
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pi.ModelsPath)!);
            JsonObject root;
            if (File.Exists(_pi.ModelsPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(_pi.ModelsPath)))
            {
                root = JsonNode.Parse(File.ReadAllText(_pi.ModelsPath)) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var providers = root["providers"] as JsonObject;
            if (providers is null)
            {
                providers = new JsonObject();
                root["providers"] = providers;
            }
            if (providers.ContainsKey(providerId))
            {
                NewProviderStatus = Language == "en-US" ? "That provider already exists in models.json." : "models.json 中已经存在这个 provider。";
                return;
            }

            var provider = new JsonObject
            {
                ["baseUrl"] = baseUrl,
                ["apiKey"] = apiKeyReference,
            };
            if (!isBuiltInProvider)
            {
                provider["api"] = NewProviderApi.Trim();
                var models = new JsonArray();
                foreach (var id in modelIds) models.Add(new JsonObject { ["id"] = id });
                provider["models"] = models;
            }
            providers[providerId] = provider;

            File.WriteAllText(_pi.ModelsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            NewProviderStatus = Language == "en-US" ? "Saved. The provider is now in models.json." : "已保存。Provider 已写入 models.json。";
            _isAddingProvider = false;
            RefreshModelProviders(providerId);
            _ = LoadRegistryModelsAsync();
            NotifyModelPageStateChanged();
        }
        catch (Exception ex)
        {
            NewProviderStatus = ex.Message;
        }
    }

    private string ModeLabel(string key) => Language == "en-US"
        ? key switch
        {
            "light" => "Light",
            "dark" => "Dark",
            "system" => "System",
            _ => key,
        }
        : key switch
        {
            "light" => "明亮",
            "dark" => "暗色",
            "system" => "系统",
            _ => key,
        };

    private void RefreshModeLabels()
    {
        foreach (var option in ModeOptions) option.SetLabel(ModeLabel(option.Key));
    }

    private void RefreshThemeLabels()
    {
        foreach (var theme in Themes) theme.SetEnglish(Language == "en-US");
    }

    private void SaveAndNotify()
    {
        var settings = CurrentSettings.Normalize();
        _service.Save(settings);
        SettingsChanged?.Invoke(settings);
    }

    private void RebuildFilteredThemes()
    {
        FilteredThemes.Clear();
        var query = ThemeSearchText.Trim();
        foreach (var theme in Themes)
        {
            if (string.IsNullOrWhiteSpace(query)
                || theme.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || theme.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredThemes.Add(theme);
            }
        }
    }

    private void BuildProviderTemplates()
    {
        ProviderTemplates.Clear();
        ProviderTemplates.Add(new ProviderTemplateItem(
            "custom-openai", "CUSTOM", "OpenAI / Anthropic compatible",
            "自定义端点格式", "Custom endpoint format",
            "custom-openai", "https://api.example.com/v1", "openai-completions", "$CUSTOM_OPENAI_API_KEY", "model-id",
            0, false, false));
        RebuildProviderTemplatesFromCatalog();
        RefreshProviderTemplateLabels();
        RebuildFilteredProviderTemplates();
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
                RebuildProviderTemplatesFromCatalog();
                RefreshProviderTemplateLabels();
            });
        }
        catch
        {
            RebuildFilteredProviderTemplates();
        }
    }

    private void RebuildProviderTemplatesFromCatalog()
    {
        var selected = _selectedProviderTemplate?.Key;
        var custom = ProviderTemplates.Where(item => !item.IsCatalogProvider).ToList();
        ProviderTemplates.Clear();
        foreach (var item in custom) ProviderTemplates.Add(item);

        foreach (var provider in _providerCatalog
            .Where(item => !string.IsNullOrWhiteSpace(item.Provider))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (provider.Provider.Equals("openai-codex", StringComparison.OrdinalIgnoreCase) && provider.IsConfigured) continue;
            var category = ProviderCategory(provider);
            ProviderTemplates.Add(new ProviderTemplateItem(
                provider.Provider,
                category,
                CleanProviderName(provider.DisplayName),
                ProviderDetail(provider, false),
                ProviderDetail(provider, true),
                provider.Provider,
                provider.BaseUrl,
                provider.Api,
                DefaultApiKeyRef(provider.Provider),
                "",
                provider.ModelCount,
                provider.IsConfigured,
                true));
        }

        foreach (var item in ProviderTemplates) item.IsSelected = item.Key == selected;
        RebuildFilteredProviderTemplates();
    }

    private void RefreshProviderTemplateLabels()
    {
        foreach (var template in ProviderTemplates) template.SetEnglish(Language == "en-US");
        RebuildFilteredProviderTemplates();
    }

    private void RefreshPackageScopeOptions()
    {
        PackageScopeOptions.Clear();
        PackageScopeOptions.Add(new("global", Language == "en-US" ? "Global" : "全局"));
        PackageInstallScope = "global";
    }

    private void RebuildFilteredProviderTemplates()
    {
        FilteredProviderTemplates.Clear();
        FilteredProviderTemplateGroups.Clear();
        var query = ProviderSearchText.Trim();
        foreach (var template in ProviderTemplates)
        {
            if (string.IsNullOrWhiteSpace(query)
                || template.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || template.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
                || template.ProviderId.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProviderTemplates.Add(template);
            }
        }

        foreach (var group in FilteredProviderTemplates
            .GroupBy(item => item.Category)
            .OrderBy(group => ProviderCategoryOrder(group.Key)))
        {
            FilteredProviderTemplateGroups.Add(new ProviderTemplateGroup(CategoryTitle(group.Key), group.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private string ProviderCategory(PiProviderCatalogRecord provider)
    {
        var id = provider.Provider.ToLowerInvariant();
        if (id == "github-copilot") return "SUBSCRIPTIONS";
        return "API KEY";
    }

    private int ProviderCategoryOrder(string category) => category switch
    {
        "CUSTOM" => 0,
        "SUBSCRIPTIONS" => 1,
        "API KEY" => 2,
        _ => 9,
    };

    private string CategoryTitle(string category) => category switch
    {
        "CUSTOM" => "CUSTOM",
        "SUBSCRIPTIONS" => "SUBSCRIPTIONS",
        "API KEY" => "API KEY",
        _ => category,
    };

    private string ProviderDetail(PiProviderCatalogRecord provider, bool english)
    {
        if (provider.Provider.Equals("github-copilot", StringComparison.OrdinalIgnoreCase)) return "OAuth";
        if (provider.IsConfigured) return english ? $"{provider.ModelCount} models · configured" : $"{provider.ModelCount} 个模型 · 已配置";
        return english ? $"{provider.ModelCount} models" : $"{provider.ModelCount} 个模型";
    }

    private static string CleanProviderName(string name)
        => name.Replace(" (Codex Subscription)", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" (Claude Pro/Max)", "", StringComparison.OrdinalIgnoreCase);

    private static string DefaultApiKeyRef(string provider)
    {
        var normalized = Regex.Replace(provider.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "$API_KEY" : $"${normalized}_API_KEY";
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
                RefreshModelProviders(_selectedModelProvider?.Provider);
            });
        }
        catch
        {
            RefreshModelProviders(_selectedModelProvider?.Provider);
        }
    }

    private void RefreshModelProviders(string? preferredProvider = null)
    {
        _modelRecords.Clear();
        void Add(PiModelOptionRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.Provider) || string.IsNullOrWhiteSpace(record.Model)) return;
            if (_modelRecords.Any(item => item.Provider.Equals(record.Provider, StringComparison.OrdinalIgnoreCase) && item.Model.Equals(record.Model, StringComparison.OrdinalIgnoreCase))) return;
            _modelRecords.Add(record);
        }

        foreach (var record in _registryModels) Add(record);
        foreach (var record in _pi.ReadModelOptions(_pi.ReadSettingsSummary())) Add(record);

        var current = preferredProvider ?? _selectedModelProvider?.Provider;
        ModelProviders.Clear();
        foreach (var group in _modelRecords.GroupBy(item => item.Provider).OrderBy(group => FriendlyProviderTitle(group.Key, group.FirstOrDefault()?.ProviderDisplayName)))
        {
            var first = group.First();
            var count = group.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var source = group.Any(item => item.Source.Contains("registry", StringComparison.OrdinalIgnoreCase)) ? "Pi registry" : "models.json";
            var title = FriendlyProviderTitle(group.Key, first.ProviderDisplayName);
            var detail = Language == "en-US" ? $"{count} models · {source}" : $"{count} 个模型 · {source}";
            ModelProviders.Add(new ModelProviderViewItem(group.Key, title, detail, count) { IsSelected = group.Key.Equals(current, StringComparison.OrdinalIgnoreCase) });
        }

        if (ModelProviders.Count > 0)
        {
            var provider = ModelProviders.FirstOrDefault(item => item.IsSelected) ?? ModelProviders[0];
            SetSelectedModelProvider(provider.Provider);
        }
        else
        {
            _selectedModelProvider = null;
            SelectedProviderModels.Clear();
        }

        NotifyModelPageStateChanged();
    }

    private void SetSelectedModelProvider(string? provider)
    {
        foreach (var item in ModelProviders) item.IsSelected = item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase);
        _selectedModelProvider = ModelProviders.FirstOrDefault(item => item.IsSelected);
        SelectedProviderModels.Clear();
        if (_selectedModelProvider is not null)
        {
            foreach (var model in _modelRecords
                .Where(item => item.Provider.Equals(_selectedModelProvider.Provider, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                SelectedProviderModels.Add(new ModelDetailViewItem(model.Model, model.DisplayName, model.Source, model.IsConfigured));
            }
        }
        OnPropertyChanged(nameof(SelectedProviderTitle));
        OnPropertyChanged(nameof(SelectedProviderDetail));
        OnPropertyChanged(nameof(SelectedProviderModelsVisibility));
        OnPropertyChanged(nameof(ModelDetailEmptyVisibility));
    }

    private string FriendlyProviderTitle(string provider, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) return displayName!;
        return provider switch
        {
            "openai-codex" => "ChatGPT Plus/Pro",
            _ => provider,
        };
    }

    private void NotifyModelPageStateChanged()
    {
        OnPropertyChanged(nameof(IsModelsSection));
        OnPropertyChanged(nameof(IsAddingProvider));
        OnPropertyChanged(nameof(ModelsPageVisibility));
        OnPropertyChanged(nameof(ModelProviderEmptyVisibility));
        OnPropertyChanged(nameof(ModelProviderListVisibility));
        OnPropertyChanged(nameof(ModelProviderDetailVisibility));
        OnPropertyChanged(nameof(AddProviderVisibility));
        OnPropertyChanged(nameof(ProviderGalleryVisibility));
        OnPropertyChanged(nameof(ProviderConfigVisibility));
        OnPropertyChanged(nameof(NewProviderModelsVisibility));
        OnPropertyChanged(nameof(SelectedProviderModelsVisibility));
        OnPropertyChanged(nameof(ModelDetailEmptyVisibility));
        OnPropertyChanged(nameof(ProviderCountText));
        OnPropertyChanged(nameof(SelectedProviderTitle));
        OnPropertyChanged(nameof(SelectedProviderDetail));
    }

    private void RefreshArchivedSessions()
    {
        ArchivedSessions.Clear();
        var query = ArchiveSearchText.Trim();
        foreach (var session in _archive.ListArchived())
        {
            if (!string.IsNullOrWhiteSpace(query)
                && !session.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !session.Cwd.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !session.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ArchivedSessions.Add(new ArchivedSessionViewItem(session, Language == "en-US"));
        }

        OnPropertyChanged(nameof(ArchivedCountText));
        OnPropertyChanged(nameof(ArchivedEmptyVisibility));
        OnPropertyChanged(nameof(ArchivedListVisibility));
    }

    private void RefreshSelectionState()
    {
        var isDark = CurrentSettings.EffectiveMode() == "dark";
        foreach (var option in ModeOptions)
        {
            option.SetTheme(Theme);
            option.SetDarkMode(isDark);
            option.IsSelected = option.Key == Mode;
        }
        foreach (var theme in Themes)
        {
            theme.SetEnglish(Language == "en-US");
            theme.SetDarkMode(isDark);
            theme.IsSelected = theme.Key == Theme;
        }
    }

    private static string LoadWorkspacePath()
    {
        try
        {
            var appData = IpiPathService.AppDataDir;
            var workspacePath = Path.Combine(appData, "workspace.json");
            if (File.Exists(workspacePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(workspacePath));
                if (doc.RootElement.TryGetProperty("lastProjectPath", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var path = value.GetString() ?? "";
                    if (Directory.Exists(path)) return path;
                }
            }
        }
        catch { }
        return Directory.Exists(Environment.CurrentDirectory) ? Environment.CurrentDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            left = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            right = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            left = left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            right = right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record SettingsRuntimeDiagnosticItem(string Level, string Badge, string Title, string Detail);

public sealed record SettingsGitCommandResult(int ExitCode, string Output, string Error);

public sealed class ModelProviderViewItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public ModelProviderViewItem(string provider, string title, string detail, int modelCount)
    {
        Provider = provider;
        Title = title;
        Detail = detail;
        ModelCount = modelCount;
    }

    public string Provider { get; }
    public string Title { get; }
    public string Detail { get; }
    public int ModelCount { get; }
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

public sealed record ModelDetailViewItem(string Id, string Name, string Source, bool IsConfigured)
{
    public string Detail => string.IsNullOrWhiteSpace(Source) ? Id : $"{Id} · {Source}";
    public string StateText => IsConfigured ? "ready" : "unavailable";
}

public sealed class ProviderTemplateGroup
{
    public ProviderTemplateGroup(string title, IEnumerable<ProviderTemplateItem> items)
    {
        Title = title;
        Items = new ObservableCollection<ProviderTemplateItem>(items);
    }

    public string Title { get; }
    public ObservableCollection<ProviderTemplateItem> Items { get; }
}

public sealed class ProviderTemplateItem : INotifyPropertyChanged
{
    private bool _english;
    private bool _isSelected;

    public ProviderTemplateItem(
        string key,
        string category,
        string title,
        string zhDetail,
        string enDetail,
        string providerId,
        string baseUrl,
        string api,
        string apiKeyRef,
        string modelIds,
        int modelCount,
        bool isConfigured,
        bool isCatalogProvider)
    {
        Key = key;
        Category = category;
        Title = title;
        ZhDetail = zhDetail;
        EnDetail = enDetail;
        ProviderId = providerId;
        BaseUrl = baseUrl;
        Api = api;
        ApiKeyRef = apiKeyRef;
        ModelIds = modelIds;
        ModelCount = modelCount;
        IsConfigured = isConfigured;
        IsCatalogProvider = isCatalogProvider;
        Badge = category == "CUSTOM" ? "+" : BuildBadge(title);
    }

    public string Key { get; }
    public string Category { get; }
    public string Title { get; }
    public string ZhDetail { get; }
    public string EnDetail { get; }
    public string Detail => _english ? EnDetail : ZhDetail;
    public string ProviderId { get; }
    public string BaseUrl { get; }
    public string Api { get; }
    public string ApiKeyRef { get; }
    public string ModelIds { get; }
    public int ModelCount { get; }
    public bool IsConfigured { get; }
    public bool IsCatalogProvider { get; }
    public string Badge { get; }
    public bool ShowPlus => !IsConfigured;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public void SetEnglish(bool english)
    {
        if (_english == english) return;
        _english = english;
        OnPropertyChanged(nameof(Detail));
    }

    private static string BuildBadge(string title)
    {
        var parts = title.Split(new[] { ' ', '/', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "+";
        var first = parts[0][0].ToString().ToUpperInvariant();
        if (parts.Length == 1) return first;
        return first + parts[1][0].ToString().ToUpperInvariant();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ArchivedSessionViewItem
{
    public ArchivedSessionViewItem(ArchivedSessionRecord session, bool english)
    {
        FilePath = session.FilePath;
        Title = string.IsNullOrWhiteSpace(session.Title) ? (english ? "Untitled chat" : "未命名对话") : session.Title;
        ProjectPath = string.IsNullOrWhiteSpace(session.Cwd) ? session.FilePath : session.Cwd;
        ProjectName = ProjectNameFromPath(session.Cwd, english);
        MessageCountText = english ? $"{session.MessageCount} messages" : $"{session.MessageCount} 条消息";
        ModifiedText = english ? $"Modified {session.Modified:g}" : $"修改于 {session.Modified:g}";
        ArchivedText = english ? $"Archived {session.ArchivedAt:g}" : $"归档于 {session.ArchivedAt:g}";
        Exists = File.Exists(session.FilePath);
        ExistsText = Exists ? (english ? "file present" : "文件存在") : (english ? "missing file" : "文件缺失");
        RestoreText = english ? "Restore" : "恢复";
        DeleteText = english ? "Delete" : "删除";
        Detail = $"{ProjectName} · {MessageCountText} · {ArchivedText}";
    }

    public string FilePath { get; }
    public string Title { get; }
    public string ProjectName { get; }
    public string ProjectPath { get; }
    public string Detail { get; }
    public string MessageCountText { get; }
    public string ModifiedText { get; }
    public string ArchivedText { get; }
    public string ExistsText { get; }
    public string RestoreText { get; }
    public string DeleteText { get; }
    public bool Exists { get; }

    private static string ProjectNameFromPath(string path, bool english)
    {
        if (string.IsNullOrWhiteSpace(path)) return english ? "Unknown project" : "未知项目";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}

public sealed record LanguageOption(string Key, string Label);

public sealed class ModeOption : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isDarkMode;
    private bool _isBroadsheet;
    private bool _isCandyBlock;

    public ModeOption(string key, string icon, string label)
    {
        Key = key;
        Icon = icon;
        Label = label;
    }

    public string Key { get; }
    public string Icon { get; }
    public string Label { get; private set; }

    public void SetLabel(string label)
    {
        if (Label == label) return;
        Label = label;
        OnPropertyChanged(nameof(Label));
    }

    public void SetTheme(string theme)
    {
        var isBroadsheet = theme == "broadsheet";
        var isCandyBlock = theme == "candy-block";
        if (_isBroadsheet == isBroadsheet && _isCandyBlock == isCandyBlock) return;
        _isBroadsheet = isBroadsheet;
        _isCandyBlock = isCandyBlock;
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(Foreground));
    }

    public void SetDarkMode(bool isDarkMode)
    {
        if (_isDarkMode == isDarkMode) return;
        _isDarkMode = isDarkMode;
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(Foreground));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Background));
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(Foreground));
        }
    }

    public Brush Background => IsSelected
        ? new SolidColorBrush(_isBroadsheet
            ? _isDarkMode ? Color.FromRgb(232, 230, 224) : Color.FromRgb(25, 37, 170)
            : _isCandyBlock ? Color.FromRgb(255, 131, 218)
            : _isDarkMode ? Color.FromRgb(43, 48, 58) : Color.FromRgb(247, 249, 254))
        : Brushes.Transparent;
    public Brush BorderBrush => IsSelected
        ? new SolidColorBrush(_isBroadsheet
            ? _isDarkMode ? Color.FromRgb(232, 230, 224) : Color.FromRgb(25, 37, 170)
            : _isCandyBlock ? Color.FromRgb(255, 131, 218)
            : _isDarkMode ? Color.FromRgb(84, 92, 109) : Color.FromRgb(191, 200, 221))
        : Brushes.Transparent;
    public Brush Foreground => IsSelected
        ? new SolidColorBrush(_isBroadsheet
            ? _isDarkMode ? Color.FromRgb(25, 37, 170) : Color.FromRgb(232, 230, 224)
            : _isCandyBlock ? Color.FromRgb(0, 0, 0)
            : _isDarkMode ? Color.FromRgb(243, 244, 246) : Color.FromRgb(36, 39, 51))
        : new SolidColorBrush(_isBroadsheet
            ? _isDarkMode ? Color.FromRgb(198, 202, 255) : Color.FromRgb(94, 105, 196)
            : _isCandyBlock ? (_isDarkMode ? Color.FromRgb(204, 204, 204) : Color.FromRgb(51, 51, 51))
            : _isDarkMode ? Color.FromRgb(150, 158, 174) : Color.FromRgb(122, 129, 144));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ThemeCardItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isDarkMode;
    private bool _english;

    public ThemeCardItem(string key, string name, string zhDescription, string enDescription, string sideColor, string pillColor, string pillBorder, string lineColor)
    {
        Key = key;
        Name = name;
        ZhDescription = zhDescription;
        EnDescription = enDescription;
        SideBrush = BrushFrom(sideColor);
        PillBrush = BrushFrom(pillColor);
        PillBorderBrush = BrushFrom(pillBorder);
        LineBrush = BrushFrom(lineColor);
    }

    public string Key { get; }
    public string Name { get; }
    public string ZhDescription { get; }
    public string EnDescription { get; }
    public string Description => _english ? EnDescription : ZhDescription;
    public Brush SideBrush { get; }
    public Brush PillBrush { get; }
    public Brush PillBorderBrush { get; }
    public Brush LineBrush { get; }

    public void SetEnglish(bool english)
    {
        if (_english == english) return;
        _english = english;
        OnPropertyChanged(nameof(Description));
    }

    public void SetDarkMode(bool isDarkMode)
    {
        if (_isDarkMode == isDarkMode) return;
        _isDarkMode = isDarkMode;
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(PreviewFrameBg));
        OnPropertyChanged(nameof(PreviewFrameBorder));
        OnPropertyChanged(nameof(PreviewTitleBg));
        OnPropertyChanged(nameof(PreviewTitleBorder));
        OnPropertyChanged(nameof(PreviewChromeBrush));
        OnPropertyChanged(nameof(PreviewSidebarBg));
        OnPropertyChanged(nameof(PreviewSidebarBorder));
        OnPropertyChanged(nameof(PreviewSidebarText));
        OnPropertyChanged(nameof(PreviewWorkspaceBg));
        OnPropertyChanged(nameof(PreviewHeroIcon));
        OnPropertyChanged(nameof(PreviewHeroText));
        OnPropertyChanged(nameof(PreviewHeroDetail));
        OnPropertyChanged(nameof(PreviewComposerBg));
        OnPropertyChanged(nameof(PreviewComposerBorder));
        OnPropertyChanged(nameof(PreviewComposerIcon));
        OnPropertyChanged(nameof(PreviewFooterBg));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(CardBorderThickness));
            OnPropertyChanged(nameof(CardBackground));
        }
    }

    public Brush CardBorderBrush => IsSelected
        ? new SolidColorBrush(IsBroadsheet
            ? _isDarkMode ? Color.FromRgb(232, 230, 224) : Color.FromRgb(25, 37, 170)
            : IsCandyBlock ? Color.FromRgb(255, 131, 218)
            : Color.FromRgb(80, 103, 255))
        : new SolidColorBrush(IsBroadsheet
            ? Color.FromArgb(70, 25, 37, 170)
            : IsCandyBlock ? Color.FromRgb(196, 201, 221)
            : _isDarkMode ? Color.FromRgb(68, 75, 90) : Color.FromRgb(200, 209, 229));
    public Thickness CardBorderThickness => IsSelected ? new Thickness(1.2) : new Thickness(1);
    public Brush CardBackground => IsBroadsheet
        ? new SolidColorBrush(_isDarkMode ? Color.FromRgb(17, 25, 103) : Color.FromRgb(232, 230, 224))
        : IsCandyBlock
            ? new SolidColorBrush(_isDarkMode ? Color.FromRgb(22, 22, 22) : Color.FromRgb(255, 252, 246))
            : IsSelected
                ? new SolidColorBrush(_isDarkMode ? Color.FromRgb(35, 40, 54) : Color.FromRgb(233, 237, 250))
                : new SolidColorBrush(_isDarkMode ? Color.FromRgb(29, 32, 40) : Color.FromRgb(233, 237, 246));

    private bool IsBroadsheet => Key == "broadsheet";
    private bool IsCandyBlock => Key == "candy-block";
    public Brush PreviewFrameBg => Solid(IsBroadsheet ? _isDarkMode ? "#111967" : "#E8E6E0" : IsCandyBlock ? _isDarkMode ? "#161616" : "#FFFFFF" : _isDarkMode ? "#11161C" : "#F3F6FC");
    public Brush PreviewFrameBorder => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#1925AA" : IsCandyBlock ? "#C4C9DD" : _isDarkMode ? "#303746" : "#C8D2E8");
    public Brush PreviewTitleBg => Solid(IsBroadsheet ? _isDarkMode ? "#0D1355" : "#E8E6E0" : IsCandyBlock ? "#FFC5EE" : _isDarkMode ? "#14181E" : "#E9EEF8");
    public Brush PreviewTitleBorder => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#1925AA" : IsCandyBlock ? "#CCCCCC" : _isDarkMode ? "#252B35" : "#CCD5E8");
    public Brush PreviewChromeBrush => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#1925AA" : IsCandyBlock ? "#000000" : _isDarkMode ? "#AAB0BE" : "#667188");
    public Brush PreviewSidebarBg => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#1925AA" : IsCandyBlock ? "#161616" : _isDarkMode ? "#152229" : "#EEF3FF");
    public Brush PreviewSidebarBorder => Solid(IsBroadsheet ? _isDarkMode ? "#1925AA" : "#1925AA" : IsCandyBlock ? "#C4C9DD" : _isDarkMode ? "#22313A" : "#D1DAEE");
    public Brush PreviewSidebarText => Solid(IsBroadsheet ? _isDarkMode ? "#1925AA" : "#E8E6E0" : IsCandyBlock ? "#FFFCF6" : _isDarkMode ? "#F0F3F7" : "#151821");
    public Brush PreviewWorkspaceBg => IsBroadsheet
        ? Solid(_isDarkMode ? "#111967" : "#E8E6E0")
        : IsCandyBlock
            ? Solid(_isDarkMode ? "#161616" : "#FFFFFF")
            : _isDarkMode
                ? new LinearGradientBrush(Color.FromRgb(14, 58, 61), Color.FromRgb(58, 30, 33), new Point(0, 0), new Point(1, 1))
                : new LinearGradientBrush(Color.FromRgb(230, 238, 249), Color.FromRgb(244, 239, 234), new Point(0, 0), new Point(1, 1));
    public string PreviewHeroIcon => _isDarkMode ? "Assets/Brand/export/white/ipi-icon-white.png" : "Assets/Brand/export/black/ipi-icon-black.png";
    public Brush PreviewHeroText => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#1925AA" : IsCandyBlock ? _isDarkMode ? "#FFFCF6" : "#000000" : _isDarkMode ? "#E7EBEF" : "#222733");
    public Brush PreviewHeroDetail => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#1925AA" : IsCandyBlock ? _isDarkMode ? "#CCCCCC" : "#333333" : _isDarkMode ? "#B5BAC6" : "#697287");
    public Brush PreviewComposerBg => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#1925AA" : IsCandyBlock ? "#FFFCF6" : _isDarkMode ? "#202329" : "#F8FAFE");
    public Brush PreviewComposerBorder => Solid(IsBroadsheet ? _isDarkMode ? "#E8E6E0" : "#E8E6E0" : IsCandyBlock ? "#FF83DA" : _isDarkMode ? "#596172" : "#AEB9D0");
    public Brush PreviewComposerIcon => Solid(IsBroadsheet ? _isDarkMode ? "#1925AA" : "#E8E6E0" : IsCandyBlock ? "#FF83DA" : _isDarkMode ? "#F3F5F8" : "#202633");
    public Brush PreviewFooterBg => Solid(IsBroadsheet ? _isDarkMode ? "#0D1355" : "#E8E6E0" : IsCandyBlock ? "#FFC5EE" : _isDarkMode ? "#10141A" : "#E9EEF8");

    public event PropertyChangedEventHandler? PropertyChanged;

    private static Brush BrushFrom(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    private static Brush Solid(string value) => BrushFrom(value);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record PackageScopeOption(string Key, string Label);

public sealed class SettingsSkillSourceItem
{
    private readonly bool _english;

    public SettingsSkillSourceItem(SkillSourceRecord source, bool english, bool isSelected)
    {
        _english = english;
        Label = source.Label;
        Path = source.Path;
        IsEnabled = source.IsEnabled;
        IsBuiltIn = source.IsBuiltIn;
        IsSelected = isSelected;
    }

    public string Label { get; }
    public string Path { get; }
    public bool IsEnabled { get; }
    public bool IsBuiltIn { get; }
    public bool IsSelected { get; }
    public string Badge => IsEnabled ? "on" : "off";
    public string StateText => IsEnabled ? _english ? "enabled" : "已启用" : _english ? "disabled" : "已禁用";
}

public sealed class SettingsSkillItem
{
    private readonly bool _english;

    public SettingsSkillItem(SkillRecord skill, bool english)
    {
        _english = english;
        Name = skill.Name;
        Description = skill.Description;
        Path = skill.Path;
        Source = skill.Source;
        IsEnabled = skill.IsEnabled;
        SourceEnabled = skill.SourceEnabled;
    }

    public string Name { get; }
    public string Description { get; }
    public string Path { get; }
    public string Source { get; }
    public bool IsEnabled { get; }
    public bool SourceEnabled { get; }
    public string Badge => IsEnabled ? "on" : "off";
    public string Title => Name;
    public string Detail => string.IsNullOrWhiteSpace(Description) ? Path : Description;
    public string StateText => SourceEnabled
        ? IsEnabled ? _english ? "enabled" : "已启用" : _english ? "disabled" : "已禁用"
        : _english ? "source disabled" : "来源已禁用";
}
