using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ipi.Desktop.Models;
using Ipi.Desktop.Services;

namespace Ipi.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly string AppDataRoot = IpiPathService.AppDataDir;
    private static readonly string WorkspaceConfigPath = Path.Combine(AppDataRoot, "workspace.json");
    private static readonly string UiSettingsPath = Path.Combine(AppDataRoot, "ui-settings.json");
    private static readonly string GlobalChatRoot = Path.Combine(IpiPathService.AppDataDir, "default-chat");
    private const string LiveStatusPath = "__ipi_live_status__";

    private readonly PiDataService _pi = new();
    private readonly ArchiveStoreService _archive;
    private readonly Dictionary<string, string> _projectDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly PiAgentBridgeService _agentBridge = new();
    private readonly WindowsSpeechTranscriptionService _speech = new();

    public event Action? RequestWindowsDictationToggle;
    public event Action? RequestScrollChatToLatest;
    private bool _isAgentRunning;
    private int _runVersion;
    private CancellationTokenSource? _runCancellation;
    private readonly HashSet<string> _runAllowedTools = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _sessionLoadCancellation;
    private int _sessionLoadVersion;
    private readonly DispatcherTimer _runElapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _runStartedAt;
    private string _liveStatusTitle = string.Empty;
    private string _liveStatusDetail = string.Empty;
    public bool IsAgentRunning
    {
        get => _isAgentRunning;
        private set
        {
            _isAgentRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AgentActivityVisibility));
            OnPropertyChanged(nameof(SendButtonGlyph));
            OnPropertyChanged(nameof(SendButtonToolTip));
            OnPropertyChanged(nameof(SendButtonBackground));
            OnPropertyChanged(nameof(SendButtonIconStroke));
        }
    }
    public Visibility AgentActivityVisibility => IsAgentRunning ? Visibility.Visible : Visibility.Collapsed;
    public string SendButtonGlyph => IsAgentRunning ? "stop" : "arrow-up";
    public string SendButtonToolTip => IsAgentRunning ? L("停止本轮生成", "Stop current run") : L("发送", "Send");
    public Brush SendButtonBackground => ResourceBrush(IsAgentRunning ? "SendButtonRunningBg" : "SendButtonIdleBg", IsAgentRunning ? Color.FromRgb(231, 233, 237) : Color.FromRgb(36, 39, 51));
    public Brush SendButtonIconStroke => ResourceBrush(IsAgentRunning ? "SendButtonRunningIcon" : "SendButtonIdleIcon", IsAgentRunning ? Color.FromRgb(17, 18, 20) : Color.FromRgb(255, 255, 255));
    private static Brush ResourceBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private string? _activeSessionFile;
    private string? _pendingBranchFromEntryId;
    private string? _pendingEditSessionFile;
    private int? _pendingEditFromRowIndex;
    private int? _pendingEditUserOrdinal;
    private bool _activeSessionMarkerOnly;
    private string? _activeCwd;
    private List<PiSessionRecord> _sessions = new();
    private List<WorkspaceFileRecord> _files = new();
    private List<SkillRecord> _skills = new();
    private List<SkillSourceRecord> _skillSources = new();
    private List<PluginPackageRecord> _packages = new();
    private List<PiModelOptionRecord> _registryModelOptions = new();
    private bool _modelSelectionInitialized;
    private int _modelOptionsLoadVersion;
    private readonly List<BranchItem> _allBranchItems = new();
    private string? _branchRepoRoot;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _projectPath = "";
    public string ProjectPath
    {
        get => _projectPath;
        private set
        {
            _projectPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(ProjectPathShort));
            OnPropertyChanged(nameof(WorkspaceChipLabel));
            OnPropertyChanged(nameof(IsGlobalChat));
        }
    }

    public bool IsGlobalChat => IsGlobalCwd(ProjectPath);

    public string ProjectName
    {
        get
        {
            if (IsGlobalChat) return L("默认聊天", "Default chat");
            return ProjectDisplayNameFor(ProjectPath);
        }
    }

    public string ProjectPathShort => IsGlobalChat ? L("不使用项目 · 全局对话", "No project · global chat") : ShortenPath(ProjectPath);
    public string ActiveLocationPath => string.IsNullOrWhiteSpace(_activeCwd) ? ProjectPath : _activeCwd!;
    public string UserLabel { get; } = Environment.UserName;
    public string ModeLabel => "Local";

    private string _language = "zh-CN";
    public bool IsEnglishUi => _language == "en-US";
    private bool IsEnglish => IsEnglishUi;
    private string L(string zh, string en) => IsEnglish ? en : zh;

    public string FileMenuText => L("文件", "File");
    public string EditMenuText => L("编辑", "Edit");
    public string ViewMenuText => L("视图", "View");
    public string HelpMenuText => L("帮助", "Help");
    public string MenuNewWindowText => L("新建窗口", "New Window");
    public string MenuNewChatText => L("新对话", "New Chat");
    public string MenuQuickChatText => L("快速对话", "Quick Chat");
    public string MenuOpenFolderText => L("打开文件夹...", "Open Folder...");
    public string MenuCloseText => L("关闭", "Close");
    public string MenuSettingsText => L("设置...", "Settings...");
    public string MenuLogOutText => L("退出登录", "Log Out");
    public string MenuExitText => L("退出", "Exit");
    public string MenuUndoText => L("撤销", "Undo");
    public string MenuCutText => L("剪切", "Cut");
    public string MenuCopyText => L("复制", "Copy");
    public string MenuPasteText => L("粘贴", "Paste");
    public string MenuSelectAllText => L("全选", "Select All");
    public string ToggleSidebarToolTip => L("展开/收起侧边栏", "Show/hide sidebar");
    public string BackToChatToolTip => L("返回聊天", "Back to chat");
    public string OpenRecentChatToolTip => L("打开最近对话", "Open recent chat");
    public string ToggleThemeToolTip => L("切换主题", "Toggle theme");
    public string SettingsToolTip => L("设置", "Settings");
    public string ToggleRightPanelToolTip => L("切换右侧面板", "Toggle right panel");
    public string RefreshLocalDataToolTip => L("刷新本地数据", "Refresh local data");
    public string AddFileToolTip => L("添加文件", "Add file");
    public string VoiceModeToolTip => L("选择录音方式", "Choose voice input mode");
    public string VoiceModeTitle => L("录音方式", "Voice input");
    public string OpenCurrentSessionLocationToolTip => L("打开当前会话位置", "Open current session location");
    public string CurrentProjectToolTip => L("当前项目", "Current project");
    public string ApprovalModeToolTip => L("批准模式", "Approval mode");
    public string ReturnToChatText => L("← 返回聊天", "← Back to chat");
    public string CopyMessageText => L("复制", "Copy");
    public string EditFromHereText => L("从这里编辑", "Edit from here");
    public string NewSessionFromHereText => L("新会话", "New session");
    public string MenuToggleSidebarText => L("切换侧边栏", "Toggle Sidebar");
    public string MenuToggleBottomPanelText => L("切换底部面板", "Toggle Bottom Panel");
    public string MenuTogglePinnedSummaryText => L("切换固定摘要", "Toggle Pinned Summary");
    public string MenuOpenTerminalText => L("打开终端", "Open Terminal");
    public string MenuToggleFileTreeText => L("切换文件树", "Toggle File Tree");
    public string MenuOpenBrowserTabText => L("打开浏览器标签", "Open Browser Tab");
    public string MenuFocusBrowserAddressBarText => L("聚焦浏览器地址栏", "Focus Browser Address Bar");
    public string MenuReloadBrowserPageText => L("重新加载浏览器页面", "Reload Browser Page");
    public string MenuToggleSidePanelText => L("切换侧面板", "Toggle Side Panel");
    public string MenuFindText => L("查找", "Find");
    public string MenuPreviousChatText => L("上一条对话", "Previous Chat");
    public string MenuNextChatText => L("下一条对话", "Next Chat");
    public string MenuBackText => L("后退", "Back");
    public string MenuForwardText => L("前进", "Forward");
    public string MenuZoomInText => L("放大", "Zoom In");
    public string MenuZoomOutText => L("缩小", "Zoom Out");
    public string MenuActualSizeText => L("实际大小", "Actual Size");
    public string MenuToggleFullScreenText => L("切换全屏", "Toggle Full Screen");
    public string ExportText => L("导出", "Export");
    public string BranchesText => L("分支", "Branches");
    public string BranchSearchPlaceholder => L("搜索分支", "Search branches");
    public string BranchCreateFallbackText => L("创建并检出新分支...", "Create and checkout new branch...");
    public string BranchCreateLabel => string.IsNullOrWhiteSpace(BranchSearchText)
        ? BranchCreateFallbackText
        : L($"创建并检出 {BranchSearchText.Trim()}", $"Create and checkout {BranchSearchText.Trim()}");
    public bool CanCreateBranch => !string.IsNullOrWhiteSpace(_branchRepoRoot) && !string.IsNullOrWhiteSpace(BranchSearchText) && !_allBranchItems.Any(b => b.Name.Equals(BranchSearchText.Trim(), StringComparison.OrdinalIgnoreCase));
    public string SystemText => L("系统", "System");
    public string WorkspaceTitle => L("Workspace", "Workspace");
    public string WorkspaceSubtitle => string.Empty;
    public string ProjectsText => L("项目", "Projects");
    public string ConversationsText => L("对话", "Chats");
    public string ConfigText => L("配置", "Config");
    public string PromptPlaceholderText => string.Empty;
    public string OpenLocationText => L("打开位置", "Open location");
    public string CurrentProjectText => L("当前项目", "Current project");
    public string LocalReadyText => L("本地就绪", "local ready");
    public string NewProjectText => L("新建项目", "New project");
    public string NewBlankProjectText => L("新建空白项目", "New blank project");
    public string UseExistingFolderText => L("使用现有文件夹", "Use existing folder");
    public string DefaultChatNoProjectText => L("默认聊天（不使用项目）", "Default chat (no project)");
    public string ProjectActionsText => L("项目操作", "Project actions");
    public string SidebarArchiveAllChatsText => L("归档所有聊天", "Archive all chats");
    public string SidebarOrganizeByProjectText => L("按项目整理", "Organize by project");
    public string SidebarRecentProjectsText => L("最近项目", "Recent projects");
    public string SidebarSortChronologicalText => L("按时间顺序", "Chronological");
    public string SidebarSortRecentText => L("最近更新", "Recently updated");
    public string ProjectPinText => L("置顶项目", "Pin project");
    public string ProjectOpenExplorerText => L("在资源管理器中打开", "Open in Explorer");
    public string ProjectCreateWorktreeText => L("创建永久工作树", "Create permanent worktree");
    public string ProjectRenameText => L("重命名项目", "Rename project");
    public string ProjectNewChatText => L("新对话", "New chat");
    public string ProjectRemoveText => L("移除", "Remove");
    public string ApprovalQuestionText => L("应如何批准 ipi 操作?", "How should ipi approve actions?");
    public string ReasoningText => L("推理", "Reasoning");
    public string ModelText => L("模型", "Model");

    private readonly string[] _approvalModeKeys = { "ask", "auto", "full", "custom" };
    private int _approvalModeIndex;
    public string ApprovalMode => ApprovalLabel;
    public string ApprovalLabel => ApprovalLabelFor(_approvalModeIndex);

    private string ApprovalLabelFor(int index) => index switch
    {
        0 => L("请求批准", "Ask first"),
        1 => L("替我审批", "Auto approve"),
        2 => L("完全访问权限", "Full access"),
        _ => L("自定义 (config.toml)", "Custom (config.toml)"),
    };

    private bool _isMainApprovalPickerOpen;
    public bool IsMainApprovalPickerOpen { get => _isMainApprovalPickerOpen; set { _isMainApprovalPickerOpen = value; OnPropertyChanged(); } }

    private bool _isChatApprovalPickerOpen;
    public bool IsChatApprovalPickerOpen { get => _isChatApprovalPickerOpen; set { _isChatApprovalPickerOpen = value; OnPropertyChanged(); } }

    private bool _isBottomApprovalPickerOpen;
    public bool IsBottomApprovalPickerOpen { get => _isBottomApprovalPickerOpen; set { _isBottomApprovalPickerOpen = value; OnPropertyChanged(); } }

    private bool _isBranchPickerOpen;
    public bool IsBranchPickerOpen { get => _isBranchPickerOpen; set { _isBranchPickerOpen = value; OnPropertyChanged(); } }

    private string _branchSearchText = string.Empty;
    public string BranchSearchText
    {
        get => _branchSearchText;
        set
        {
            if (_branchSearchText == value) return;
            _branchSearchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BranchCreateLabel));
            OnPropertyChanged(nameof(CanCreateBranch));
            FilterBranches();
        }
    }

    private string _branchStatusText = string.Empty;
    public string BranchStatusText { get => _branchStatusText; set { _branchStatusText = value; OnPropertyChanged(); } }

    private bool _isMainThinkingPickerOpen;
    public bool IsMainThinkingPickerOpen { get => _isMainThinkingPickerOpen; set { _isMainThinkingPickerOpen = value; OnPropertyChanged(); } }

    private bool _isChatThinkingPickerOpen;
    public bool IsChatThinkingPickerOpen { get => _isChatThinkingPickerOpen; set { _isChatThinkingPickerOpen = value; OnPropertyChanged(); } }

    private readonly ToolPresetOption[] _toolPresets =
    {
        new("default", "tools: default", "Pi default tool set: read · bash · edit · write plus enabled extension tools", null, null),
        new("read", "tools: read", "Read-only tools: read · grep · find · ls", new[] { "read", "grep", "find", "ls" }, null),
        new("no-bash", "tools: no bash", "File tools without shell: read · edit · write · grep · find · ls", new[] { "read", "edit", "write", "grep", "find", "ls" }, null),
        new("none", "tools: none", "No tools exposed to the model", null, "all"),
    };
    private int _toolPresetIndex;
    private ToolPresetOption CurrentToolPreset => _toolPresets[_toolPresetIndex];
    public string ToolLabel => CurrentToolPreset.Label;

    private string _modelLabel = "model: loading";
    private string _activeProvider = "unknown";
    private string _activeModel = "unknown";
    public string ModelLabel { get => _modelLabel; set { _modelLabel = value; OnPropertyChanged(); } }

    private bool _isSystemContextPage;
    private bool _isPluginsPage;
    public Visibility SystemContextVisibility => _isSystemContextPage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PluginsPageVisibility => _isPluginsPage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MainHeroVisibility => _isSystemContextPage || _isPluginsPage ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MainRowsVisibility => _isSystemContextPage || _isPluginsPage ? Visibility.Collapsed : Visibility.Visible;

    private string _systemContextText = string.Empty;
    public string SystemContextText { get => _systemContextText; set { _systemContextText = value; OnPropertyChanged(); } }
    public ObservableCollection<SystemContextSection> SystemContextSections { get; } = new();
    public ObservableCollection<PluginPackageViewItem> PluginPackages { get; } = new();
    public ObservableCollection<PluginResourceGroupViewItem> PluginResourceGroups { get; } = new();
    private PluginPackageViewItem? _selectedPluginPackage;
    private bool _isPluginActionRunning;
    private bool _isPluginAddOpen;
    private string _pluginNewSource = "npm:";
    private string _pluginActionStatus = string.Empty;
    public bool IsPluginActionRunning { get => _isPluginActionRunning; private set { _isPluginActionRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(PluginActionButtonEnabled)); } }
    public bool IsPluginAddOpen { get => _isPluginAddOpen; set { _isPluginAddOpen = value; OnPropertyChanged(); OnPropertyChanged(nameof(PluginAddVisibility)); } }
    public string PluginNewSource { get => _pluginNewSource; set { _pluginNewSource = value; OnPropertyChanged(); } }
    public string PluginActionStatus { get => _pluginActionStatus; private set { _pluginActionStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(PluginActionStatusVisibility)); } }
    public bool PluginActionButtonEnabled => !IsPluginActionRunning;
    public Visibility PluginAddVisibility => IsPluginAddOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PluginActionStatusVisibility => string.IsNullOrWhiteSpace(PluginActionStatus) ? Visibility.Collapsed : Visibility.Visible;
    public string PluginSettingsPath => _pi.SettingsPath;
    public string PluginCountText => L($"{PluginPackages.Count} 个 package", $"{PluginPackages.Count} packages");
    public string PluginFooterText => BuildPluginFooterText();
    public string SelectedPluginTitle => _selectedPluginPackage?.Title ?? L("选择插件", "Select a plugin");
    public string SelectedPluginSource => _selectedPluginPackage?.Source ?? string.Empty;
    public string PluginPageDescription => L("管理 Pi package、扩展、技能、提示和主题。", "Manage Pi packages, extensions, skills, prompts, and themes.");
    public string PluginAddButtonText => L("添加", "Add");
    public string PluginUpdateButtonText => L("更新", "Update");
    public string PluginReloadButtonText => L("重新加载", "Reload");
    public string PluginRemoveButtonText => L("移除", "Remove");
    public string PluginCancelButtonText => L("取消", "Cancel");
    public string PluginEmptyTitle => L("没有 package", "No packages");
    public string PluginEmptyDescription => L("settings.json 里没有 packages", "settings.json has no packages");
    public string PluginStatusLabel => L("状态", "Status");
    public string PluginVersionLabel => L("版本", "Version");
    public string PluginPackageLabel => L("包名", "Package");
    public string PluginResourcesLabel => L("资源", "Resources");
    public string PluginInstalledPathLabel => L("安装路径", "Installed path");
    public string PluginScopeLabel => L("配置范围", "Config scope");
    public string PluginResolvedResourcesLabel => L("已解析资源", "Resolved resources");
    public string SelectedPluginStatus => _selectedPluginPackage?.StatusText ?? "-";
    public bool SelectedPluginEnabled => _selectedPluginPackage is not null && !_selectedPluginPackage.Disabled;
    public Brush SelectedPluginStatusBrush => _selectedPluginPackage?.StatusBrush ?? Brushes.Transparent;
    public Brush SelectedPluginStatusForeground => _selectedPluginPackage?.StatusForeground ?? Brushes.Gray;
    public string SelectedPluginVersion => _selectedPluginPackage?.VersionText ?? "-";
    public string SelectedPluginPackageName => _selectedPluginPackage?.PackageName ?? "-";
    public string SelectedPluginResources => _selectedPluginPackage?.ResourceSummary ?? "-";
    public string SelectedPluginPath => _selectedPluginPackage?.InstalledPath ?? "-";
    public string SelectedPluginScope => _selectedPluginPackage?.ScopeText ?? "-";
    public Visibility PluginEmptyVisibility => PluginPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PluginDetailVisibility => PluginPackages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelectedPluginNpmActionVisibility => _selectedPluginPackage?.IsNpmPackage == true ? Visibility.Visible : Visibility.Collapsed;

    private string _modelControlLabel = "model";
    public string ModelControlLabel { get => _modelControlLabel; set { _modelControlLabel = value; OnPropertyChanged(); } }

    private string _modelMenuLabel = "Model";
    public string ModelMenuLabel { get => _modelMenuLabel; set { _modelMenuLabel = value; OnPropertyChanged(); } }

    private string _thinkingLevel = "medium";
    public string ThinkingLevel { get => _thinkingLevel; set { _thinkingLevel = value; OnPropertyChanged(); } }

    private bool _isVoiceBusy;
    private bool _isVoiceRecording;
    public bool IsVoiceRecording
    {
        get => _isVoiceRecording;
        private set
        {
            _isVoiceRecording = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VoiceButtonLabel));
        }
    }

    public string VoiceButtonLabel => IsVoiceRecording ? "stop" : "mic";

    private bool _isMainVoicePickerOpen;
    public bool IsMainVoicePickerOpen { get => _isMainVoicePickerOpen; set { _isMainVoicePickerOpen = value; OnPropertyChanged(); } }

    private bool _isChatVoicePickerOpen;
    public bool IsChatVoicePickerOpen { get => _isChatVoicePickerOpen; set { _isChatVoicePickerOpen = value; OnPropertyChanged(); } }

    private string _voiceBackendKey = "windows-dictation";
    public string VoiceBackendKey
    {
        get => _voiceBackendKey;
        private set
        {
            _voiceBackendKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VoiceBackendLabel));
            OnPropertyChanged(nameof(VoiceButtonLabel));
        }
    }

    public string VoiceBackendLabel => VoiceOptions.FirstOrDefault(v => v.Key == VoiceBackendKey)?.Title ?? "Windows Dictation";

    public string WorkspaceChipLabel => IsGlobalChat ? L("默认聊天", "Default chat") : CurrentProjectText;

    private bool _isProjectPickerOpen;
    public bool IsProjectPickerOpen { get => _isProjectPickerOpen; set { _isProjectPickerOpen = value; OnPropertyChanged(); } }

    private bool _isBottomProjectPickerOpen;
    public bool IsBottomProjectPickerOpen { get => _isBottomProjectPickerOpen; set { _isBottomProjectPickerOpen = value; OnPropertyChanged(); } }

    private bool _isNewProjectMenuOpen;
    public bool IsNewProjectMenuOpen { get => _isNewProjectMenuOpen; set { _isNewProjectMenuOpen = value; OnPropertyChanged(); } }

    private string _projectSearchText = "";
    public string ProjectSearchText
    {
        get => _projectSearchText;
        set
        {
            _projectSearchText = value;
            OnPropertyChanged();
            UpdateProjectPickerItems();
        }
    }

    private string _statusText = "loading local data";
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(PinnedSummaryText)); } }

    private long? _contextLimitTokens;

    private SessionUsageSummary _currentUsage = new(0, 0, 0, 0, 0, 0);
    private string _topStatsText = "";
    public string TopStatsText { get => _topStatsText; set { _topStatsText = value; OnPropertyChanged(); } }
    public string TopInputText { get; private set; } = "0";
    public string TopOutputText { get; private set; } = "0";
    public string TopCacheText { get; private set; } = "0";
    public string TopCostText { get; private set; } = "$0.00";
    public string TopContextText { get; private set; } = "0% / loading";
    public double TopContextFillWidth { get; private set; } = 0;

    private bool _isUpdatePopupOpen;
    private bool _isUpdateCheckRunning;
    private bool _isUpdateApplyRunning;
    private bool _hasUpstreamUpdate;
    private string _updateStatusText = string.Empty;
    private string _updateDetailText = string.Empty;
    private string? _updateRepoRoot;
    private string? _updateUpstream;
    public bool IsUpdatePopupOpen { get => _isUpdatePopupOpen; set { _isUpdatePopupOpen = value; OnPropertyChanged(); } }
    public bool IsUpdateCheckRunning { get => _isUpdateCheckRunning; private set { _isUpdateCheckRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(UpdateButtonVisibility)); OnPropertyChanged(nameof(UpdateActionText)); OnPropertyChanged(nameof(UpdateAvailableText)); } }
    public bool IsUpdateApplyRunning { get => _isUpdateApplyRunning; private set { _isUpdateApplyRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApplyUpdate)); OnPropertyChanged(nameof(UpdateActionText)); } }
    public bool HasUpstreamUpdate { get => _hasUpstreamUpdate; private set { _hasUpstreamUpdate = value; OnPropertyChanged(); OnPropertyChanged(nameof(UpdateButtonVisibility)); OnPropertyChanged(nameof(CanApplyUpdate)); OnPropertyChanged(nameof(UpdateAvailableText)); } }
    public Visibility UpdateButtonVisibility => HasUpstreamUpdate || IsUpdateCheckRunning ? Visibility.Visible : Visibility.Collapsed;
    public string UpdateStatusText { get => _updateStatusText; private set { _updateStatusText = value; OnPropertyChanged(); } }
    public string UpdateDetailText { get => _updateDetailText; private set { _updateDetailText = value; OnPropertyChanged(); } }
    public string UpdateAvailableText => L("有更新", "Update");
    public string UpdatePopupTitle => L("上游有更新", "Upstream update available");
    public string UpdatePopupDescription => L("更新会拉取上游代码，重新发布 Windows 桌面端，然后自动重启 ipi。", "This pulls upstream changes, republishes the Windows desktop app, then restarts ipi.");
    public string UpdateActionText => IsUpdateApplyRunning ? L("正在更新…", "Updating…") : IsUpdateCheckRunning ? L("检查中…", "Checking…") : L("一键更新", "Update now");
    public bool CanApplyUpdate => HasUpstreamUpdate && !IsUpdateApplyRunning;

    private bool _isSessionInfoPopupOpen;
    public bool IsSessionInfoPopupOpen { get => _isSessionInfoPopupOpen; set { _isSessionInfoPopupOpen = value; OnPropertyChanged(); } }
    public string SessionInfoFile { get; private set; } = "No active session";
    public string SessionInfoId { get; private set; } = "-";
    public string SessionInfoUserMessages { get; private set; } = "0";
    public string SessionInfoAssistantMessages { get; private set; } = "0";
    public string SessionInfoToolCalls { get; private set; } = "0";
    public string SessionInfoToolResults { get; private set; } = "0";
    public string SessionInfoTotalMessages { get; private set; } = "0";
    public string SessionInfoInputTokens { get; private set; } = "0";
    public string SessionInfoOutputTokens { get; private set; } = "0";
    public string SessionInfoCacheReadTokens { get; private set; } = "0";
    public string SessionInfoTotalTokens { get; private set; } = "0";
    public string SessionInfoCost { get; private set; } = "$0.0000";
    public string SessionInfoContext { get; private set; } = "0% / loading";

    private string _appearanceMode = "light";
    public string AppearanceIcon => _appearanceMode switch
    {
        "dark" => "sun",
        "system" => "monitor",
        _ => "moon",
    };

    private bool _isBottomPanelVisible;
    public bool IsBottomPanelVisible
    {
        get => _isBottomPanelVisible;
        private set { _isBottomPanelVisible = value; OnPropertyChanged(); }
    }

    private bool _isPinnedSummaryVisible;
    public bool IsPinnedSummaryVisible
    {
        get => _isPinnedSummaryVisible;
        private set { _isPinnedSummaryVisible = value; OnPropertyChanged(); }
    }

    public string PinnedSummaryText => string.IsNullOrWhiteSpace(_activeSessionFile)
        ? $"{ProjectName} · {StatusText}"
        : $"{PanelTitle} · {SessionStatsText} · {ShortenPath(_activeSessionFile)}";

    private string _panelTitle = "ipi";
    public string PanelTitle { get => _panelTitle; set { _panelTitle = value; OnPropertyChanged(); OnPropertyChanged(nameof(PinnedSummaryText)); } }

    private string _panelSubtitle = "";
    public string PanelSubtitle { get => _panelSubtitle; set { _panelSubtitle = value; OnPropertyChanged(); } }

    private bool _isInspectorVisible;
    public bool IsInspectorVisible
    {
        get => _isInspectorVisible;
        set
        {
            if (_isInspectorVisible == value) return;
            _isInspectorVisible = value;
            if (!value)
            {
                _isRightPanelAutoHidden = false;
                _isRightPanelPeekOpen = false;
            }
            OnPropertyChanged();
            NotifyRightPanelLayoutChanged();
        }
    }

    private const double MinPinnedRightPanelWidth = 320;
    private const double MaxExpandedRightPanelWidth = 760;
    private double _expandedRightPanelWidth = 460;
    private bool _isRightPanelAutoHidden;
    private bool _isRightPanelPeekOpen;

    public bool IsRightPanelAutoHidden
    {
        get => _isRightPanelAutoHidden;
        private set
        {
            if (_isRightPanelAutoHidden == value) return;
            _isRightPanelAutoHidden = value;
            NotifyRightPanelLayoutChanged();
        }
    }

    public bool IsRightPanelPeekOpen
    {
        get => _isRightPanelPeekOpen;
        private set
        {
            if (_isRightPanelPeekOpen == value) return;
            _isRightPanelPeekOpen = value;
            NotifyRightPanelLayoutChanged();
        }
    }

    public GridLength RightPanelWidth => !IsInspectorVisible || IsRightPanelAutoHidden ? new GridLength(0) : new GridLength(_expandedRightPanelWidth);
    public double RightPanelMinWidth => !IsInspectorVisible || IsRightPanelAutoHidden ? 0 : MinPinnedRightPanelWidth;
    public double RightPanelMaxWidth => !IsInspectorVisible || IsRightPanelAutoHidden ? 0 : MaxExpandedRightPanelWidth;
    public double RightPanelPanelWidth => _expandedRightPanelWidth;
    public Thickness RightPanelResizeThumbMargin => new(0, 0, Math.Max(0, RightPanelPanelWidth - 7), 0);
    public Visibility RightPanelPanelVisibility => IsInspectorVisible ? Visibility.Visible : Visibility.Collapsed;
    public bool RightPanelPanelIsHitTestVisible => IsInspectorVisible && (!IsRightPanelAutoHidden || IsRightPanelPeekOpen);
    public Visibility RightPanelResizeThumbVisibility => IsInspectorVisible && (!IsRightPanelAutoHidden || IsRightPanelPeekOpen) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RightPanelAutoHideEdgeVisibility => IsInspectorVisible && IsRightPanelAutoHidden && !IsRightPanelPeekOpen ? Visibility.Visible : Visibility.Collapsed;
    public double RightPanelResizeStartWidth => _expandedRightPanelWidth;

    private void NotifyRightPanelLayoutChanged()
    {
        OnPropertyChanged(nameof(IsRightPanelAutoHidden));
        OnPropertyChanged(nameof(IsRightPanelPeekOpen));
        OnPropertyChanged(nameof(RightPanelWidth));
        OnPropertyChanged(nameof(RightPanelMinWidth));
        OnPropertyChanged(nameof(RightPanelMaxWidth));
        OnPropertyChanged(nameof(RightPanelPanelWidth));
        OnPropertyChanged(nameof(RightPanelResizeThumbMargin));
        OnPropertyChanged(nameof(RightPanelPanelVisibility));
        OnPropertyChanged(nameof(RightPanelPanelIsHitTestVisible));
        OnPropertyChanged(nameof(RightPanelResizeThumbVisibility));
        OnPropertyChanged(nameof(RightPanelAutoHideEdgeVisibility));
    }

    private string _rightPanelMode = "actions";
    private bool _isFilePanelMode;
    private bool _isRightActionsMode;
    public Visibility InspectorRowsVisibility => _rightPanelMode == "inspector" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilePanelVisibility => _rightPanelMode == "file" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RightPanelActionsVisibility => _rightPanelMode == "actions" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RightPanelTerminalVisibility => _rightPanelMode == "terminal" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RightPanelBrowserVisibility => _rightPanelMode == "browser" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RightPanelFilesVisibility => _rightPanelMode == "files" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RightPanelChatVisibility => _rightPanelMode == "chat" ? Visibility.Visible : Visibility.Collapsed;

    private string _inspectorTitle = "Context";
    public string InspectorTitle { get => _inspectorTitle; set { _inspectorTitle = value; OnPropertyChanged(); } }

    private bool _isExplorerExpanded;
    public bool IsExplorerExpanded { get => _isExplorerExpanded; set { _isExplorerExpanded = value; OnPropertyChanged(); } }
    public string ExplorerText => L("资源管理器", "EXPLORER");
    public string RightPanelText => L("侧边栏", "Side panel");
    public string SideTerminalTitle => L("终端", "Terminal");
    public string SideBrowserTitle => L("浏览器", "Browser");
    public string SideFilesTitle => L("文件", "Files");
    public string SideChatTitle => L("侧边聊天", "Side chat");
    public string SideTerminalRunText => L("运行", "Run");
    public string SideBrowserOpenText => L("系统浏览器打开", "Open in browser");
    public string SideBrowserNativeNote => L("ipi 不在右侧栏内嵌 WebView。这里保留浏览器动作和地址；只有点击上方按钮时才会打开系统浏览器。", "ipi does not embed a WebView in the side panel. This view keeps browser actions and the address here; the system browser opens only when you click the button above.");
    public string SideTerminalCwd => CurrentRunDirectory;

    private string? _selectedExplorerFile;
    private string _filePanelTitle = "";
    private string _filePanelPath = "";
    private string _filePanelMeta = "";
    private string _filePanelText = "";
    private string _filePanelStatus = "";
    private bool _isFilePanelRawMode;
    private bool _canEditFilePanel;
    public string FilePanelTitle { get => _filePanelTitle; private set { _filePanelTitle = value; OnPropertyChanged(); } }
    public string FilePanelPath { get => _filePanelPath; private set { _filePanelPath = value; OnPropertyChanged(); } }
    public string FilePanelMeta { get => _filePanelMeta; private set { _filePanelMeta = value; OnPropertyChanged(); } }
    public string FilePanelText { get => _filePanelText; set { _filePanelText = value; OnPropertyChanged(); } }
    public string FilePanelStatus { get => _filePanelStatus; private set { _filePanelStatus = value; OnPropertyChanged(); } }
    public bool IsFilePanelRawMode { get => _isFilePanelRawMode; private set { _isFilePanelRawMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilePreviewButtonWeight)); OnPropertyChanged(nameof(FileRawButtonWeight)); OnPropertyChanged(nameof(FileRawEditorVisibility)); OnPropertyChanged(nameof(FilePreviewVisibility)); } }
    public bool CanEditFilePanel { get => _canEditFilePanel; private set { _canEditFilePanel = value; OnPropertyChanged(); } }

    private string _sideTerminalCommand = "git status --short";
    private string _sideTerminalOutput = "";
    private bool _isSideTerminalRunning;
    private string _sideBrowserUrl = "about:blank";
    private Uri _sideBrowserUri = new("about:blank");
    private string _selectedChatText = string.Empty;
    private bool _isSelectedTextActionOpen;
    private string _sideChatContextText = string.Empty;
    private string _sideChatInputText = string.Empty;
    private bool _isSideChatRunning;
    private string? _sideChatSessionFile;
    private CancellationTokenSource? _sideChatCancellation;
    public string SideTerminalCommand { get => _sideTerminalCommand; set { _sideTerminalCommand = value; OnPropertyChanged(); } }
    public string SideTerminalOutput { get => _sideTerminalOutput; private set { _sideTerminalOutput = value; OnPropertyChanged(); } }
    public bool IsSideTerminalRunning { get => _isSideTerminalRunning; private set { _isSideTerminalRunning = value; OnPropertyChanged(); } }
    public string SideBrowserUrl { get => _sideBrowserUrl; set { _sideBrowserUrl = value; OnPropertyChanged(); } }
    public Uri SideBrowserUri { get => _sideBrowserUri; private set { _sideBrowserUri = value; OnPropertyChanged(); } }
    public bool IsSelectedTextActionOpen { get => _isSelectedTextActionOpen; set { _isSelectedTextActionOpen = value; OnPropertyChanged(); } }
    public string SelectedTextActionPreview => string.IsNullOrWhiteSpace(_selectedChatText) ? string.Empty : TrimForRow(_selectedChatText.Replace("\r", " ").Replace("\n", " "), 80);
    public string SideChatContextText { get => _sideChatContextText; private set { _sideChatContextText = value; OnPropertyChanged(); OnPropertyChanged(nameof(SideChatContextVisibility)); OnPropertyChanged(nameof(SideChatContextLabel)); } }
    public string SideChatContextLabel => string.IsNullOrWhiteSpace(SideChatContextText) ? L("无选中文本", "No selected text") : L("1 个已选文本片段", "1 selected text snippet");
    public Visibility SideChatContextVisibility => string.IsNullOrWhiteSpace(SideChatContextText) ? Visibility.Collapsed : Visibility.Visible;
    public string SideChatInputText { get => _sideChatInputText; set { _sideChatInputText = value; OnPropertyChanged(); } }
    public bool IsSideChatRunning { get => _isSideChatRunning; private set { _isSideChatRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(SideChatSendText)); } }
    public string SideChatSendText => IsSideChatRunning ? L("停止", "Stop") : L("发送", "Send");
    public string SideChatTemporaryNote => L("临时子对话 · 不进入左侧列表", "Temporary side chat · not added to the sidebar");
    public string NewTemporarySideChatText => L("新临时对话", "New temporary chat");
    public string AddToConversationText => L("添加到对话", "Add to chat");
    public string AskInSideChatText => L("在侧边聊天中提问", "Ask in side chat");
    public ObservableCollection<FileExplorerNode> RightPanelFileItems { get; } = new();
    public ObservableCollection<PanelRow> RightPanelChatRows { get; } = new();

    public Visibility FileRawEditorVisibility => IsFilePanelRawMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilePreviewVisibility => IsFilePanelRawMode ? Visibility.Collapsed : Visibility.Visible;
    public FontWeight FilePreviewButtonWeight => IsFilePanelRawMode ? FontWeights.Regular : FontWeights.SemiBold;
    public FontWeight FileRawButtonWeight => IsFilePanelRawMode ? FontWeights.SemiBold : FontWeights.Regular;

    private bool _isComposerVisible = true;
    public bool IsComposerVisible { get => _isComposerVisible; set { _isComposerVisible = value; OnPropertyChanged(); } }

    private bool _isChatMode;
    public bool IsChatMode
    {
        get => _isChatMode;
        set
        {
            _isChatMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MainPanelVisibility));
            OnPropertyChanged(nameof(ChatPanelVisibility));
        }
    }

    public Visibility MainPanelVisibility => IsChatMode || _isPluginsPage ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChatPanelVisibility => IsChatMode ? Visibility.Visible : Visibility.Collapsed;

    private bool _isStartActionsVisible = true;
    public bool IsStartActionsVisible { get => _isStartActionsVisible; set { _isStartActionsVisible = value; OnPropertyChanged(); } }

    private bool _canReturnToChat;
    public bool CanReturnToChat
    {
        get => _canReturnToChat;
        set
        {
            _canReturnToChat = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReturnToChatVisibility));
        }
    }

    public Visibility ReturnToChatVisibility => CanReturnToChat ? Visibility.Visible : Visibility.Collapsed;

    private string _localSummaryText = "";
    public string LocalSummaryText { get => _localSummaryText; set { _localSummaryText = value; OnPropertyChanged(); } }

    private bool _isToolbarVisible;
    public bool IsToolbarVisible { get => _isToolbarVisible; set { _isToolbarVisible = value; OnPropertyChanged(); } }

    private string _sessionStatsText = "";
    public string SessionStatsText { get => _sessionStatsText; set { _sessionStatsText = value; OnPropertyChanged(); OnPropertyChanged(nameof(PinnedSummaryText)); } }

    private const double CollapsedSidebarWidth = 58;
    private const double MinPinnedSidebarWidth = 260;
    private const double AutoHideTriggerWidth = -80;
    private const double MaxExpandedSidebarWidth = 420;
    private double _expandedSidebarWidth = 276;
    private bool _isSidebarExpanded = true;
    private bool _isSidebarAutoHidden;
    private bool _isSidebarPeekOpen;
    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set
        {
            _isSidebarExpanded = value;
            OnPropertyChanged();
            if (!_isSidebarExpanded)
            {
                IsSidebarAutoHidden = false;
                IsSidebarPeekOpen = false;
            }
            NotifySidebarLayoutChanged();
        }
    }

    public bool IsSidebarAutoHidden
    {
        get => _isSidebarAutoHidden;
        private set
        {
            if (_isSidebarAutoHidden == value) return;
            _isSidebarAutoHidden = value;
            NotifySidebarLayoutChanged();
        }
    }

    public bool IsSidebarPeekOpen
    {
        get => _isSidebarPeekOpen;
        private set
        {
            if (_isSidebarPeekOpen == value) return;
            _isSidebarPeekOpen = value;
            NotifySidebarLayoutChanged();
        }
    }

    public GridLength SidebarWidth => !IsSidebarExpanded
        ? new GridLength(CollapsedSidebarWidth)
        : IsSidebarAutoHidden
            ? new GridLength(0)
            : new GridLength(_expandedSidebarWidth);

    public double SidebarMinWidth => IsSidebarAutoHidden ? 0 : IsSidebarExpanded ? MinPinnedSidebarWidth : CollapsedSidebarWidth;
    public double SidebarMaxWidth => IsSidebarAutoHidden ? 0 : IsSidebarExpanded ? MaxExpandedSidebarWidth : CollapsedSidebarWidth;
    public double SidebarPanelWidth => IsSidebarExpanded ? _expandedSidebarWidth : CollapsedSidebarWidth;
    public Thickness SidebarResizeThumbMargin => new(Math.Max(0, SidebarPanelWidth - 7), 0, 0, 0);
    public double SidebarHeaderWidth => Math.Max(150, _expandedSidebarWidth - 82);
    public double SidebarRowWidth => Math.Max(168, _expandedSidebarWidth - 52);
    public double SidebarNestedRowWidth => Math.Max(136, _expandedSidebarWidth - 82);
    public Visibility SidebarPanelVisibility => Visibility.Visible;
    public bool SidebarPanelIsHitTestVisible => !IsSidebarAutoHidden || IsSidebarPeekOpen;
    public Visibility ExpandedSidebarVisibility => IsSidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CollapsedSidebarVisibility => !IsSidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SidebarResizeThumbVisibility => IsSidebarExpanded && (!IsSidebarAutoHidden || IsSidebarPeekOpen) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SidebarAutoHideEdgeVisibility => IsSidebarExpanded && IsSidebarAutoHidden && !IsSidebarPeekOpen ? Visibility.Visible : Visibility.Collapsed;
    public double SidebarResizeStartWidth => _expandedSidebarWidth;

    private void NotifySidebarLayoutChanged()
    {
        OnPropertyChanged(nameof(IsSidebarExpanded));
        OnPropertyChanged(nameof(IsSidebarAutoHidden));
        OnPropertyChanged(nameof(IsSidebarPeekOpen));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(SidebarMinWidth));
        OnPropertyChanged(nameof(SidebarMaxWidth));
        OnPropertyChanged(nameof(SidebarPanelWidth));
        OnPropertyChanged(nameof(SidebarResizeThumbMargin));
        OnPropertyChanged(nameof(SidebarHeaderWidth));
        OnPropertyChanged(nameof(SidebarRowWidth));
        OnPropertyChanged(nameof(SidebarNestedRowWidth));
        OnPropertyChanged(nameof(SidebarPanelVisibility));
        OnPropertyChanged(nameof(SidebarPanelIsHitTestVisible));
        OnPropertyChanged(nameof(ExpandedSidebarVisibility));
        OnPropertyChanged(nameof(CollapsedSidebarVisibility));
        OnPropertyChanged(nameof(SidebarResizeThumbVisibility));
        OnPropertyChanged(nameof(SidebarAutoHideEdgeVisibility));
    }

    public void BeginSidebarResize()
    {
        IsSidebarExpanded = true;
    }

    public void PreviewSidebarResize(double requestedWidth)
    {
        var pinnedWidth = Math.Clamp(requestedWidth, MinPinnedSidebarWidth, MaxExpandedSidebarWidth);
        if (IsSidebarAutoHidden && requestedWidth >= AutoHideTriggerWidth)
        {
            _isSidebarAutoHidden = false;
            _isSidebarPeekOpen = false;
        }
        if (Math.Abs(_expandedSidebarWidth - pinnedWidth) < 0.5 && !IsSidebarAutoHidden) return;
        _expandedSidebarWidth = pinnedWidth;
        NotifySidebarLayoutChanged();
    }

    public void CompleteSidebarResize(double requestedWidth)
    {
        if (requestedWidth < AutoHideTriggerWidth)
        {
            _expandedSidebarWidth = MinPinnedSidebarWidth;
            IsSidebarExpanded = true;
            IsSidebarAutoHidden = true;
            IsSidebarPeekOpen = false;
            StatusText = "sidebar auto-hide enabled";
            return;
        }

        _expandedSidebarWidth = Math.Clamp(requestedWidth, MinPinnedSidebarWidth, MaxExpandedSidebarWidth);
        IsSidebarExpanded = true;
        IsSidebarAutoHidden = false;
        IsSidebarPeekOpen = false;
        NotifySidebarLayoutChanged();
    }

    public void OpenSidebarPeek()
    {
        if (IsSidebarAutoHidden) IsSidebarPeekOpen = true;
    }

    public void CloseSidebarPeek()
    {
        if (IsSidebarAutoHidden) IsSidebarPeekOpen = false;
    }

    public void BeginRightPanelResize()
    {
        if (!IsInspectorVisible) ShowRightActionsPanel();
    }

    public void PreviewRightPanelResize(double requestedWidth)
    {
        var pinnedWidth = Math.Clamp(requestedWidth, MinPinnedRightPanelWidth, MaxExpandedRightPanelWidth);
        if (IsRightPanelAutoHidden && requestedWidth >= AutoHideTriggerWidth)
        {
            _isRightPanelAutoHidden = false;
            _isRightPanelPeekOpen = false;
        }
        if (Math.Abs(_expandedRightPanelWidth - pinnedWidth) < 0.5 && !IsRightPanelAutoHidden) return;
        _expandedRightPanelWidth = pinnedWidth;
        NotifyRightPanelLayoutChanged();
    }

    public void CompleteRightPanelResize(double requestedWidth)
    {
        if (requestedWidth < AutoHideTriggerWidth)
        {
            _expandedRightPanelWidth = MinPinnedRightPanelWidth;
            IsInspectorVisible = true;
            IsRightPanelAutoHidden = true;
            IsRightPanelPeekOpen = false;
            StatusText = "right panel auto-hide enabled";
            return;
        }

        _expandedRightPanelWidth = Math.Clamp(requestedWidth, MinPinnedRightPanelWidth, MaxExpandedRightPanelWidth);
        IsInspectorVisible = true;
        IsRightPanelAutoHidden = false;
        IsRightPanelPeekOpen = false;
        NotifyRightPanelLayoutChanged();
    }

    public void OpenRightPanelPeek()
    {
        if (IsInspectorVisible && IsRightPanelAutoHidden) IsRightPanelPeekOpen = true;
    }

    public void CloseRightPanelPeek()
    {
        if (IsRightPanelAutoHidden) IsRightPanelPeekOpen = false;
    }

    public void PinRightPanelOpen()
    {
        if (!IsInspectorVisible) IsInspectorVisible = true;
        IsRightPanelAutoHidden = false;
        IsRightPanelPeekOpen = false;
        NotifyRightPanelLayoutChanged();
    }

    private bool _isProjectsExpanded;
    public bool IsProjectsExpanded { get => _isProjectsExpanded; set { _isProjectsExpanded = value; OnPropertyChanged(); } }

    private bool _isSessionsExpanded;
    public bool IsSessionsExpanded { get => _isSessionsExpanded; set { _isSessionsExpanded = value; OnPropertyChanged(); } }

    private bool _isConfigExpanded;
    public bool IsConfigExpanded { get => _isConfigExpanded; set { _isConfigExpanded = value; OnPropertyChanged(); } }

    private string _promptText = string.Empty;
    public string PromptText
    {
        get => _promptText;
        set
        {
            _promptText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PromptPlaceholderVisibility));
        }
    }
    public Visibility PromptPlaceholderVisibility => string.IsNullOrWhiteSpace(PromptText) ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<string> MenuItems { get; } = new();
    public ObservableCollection<string> TopActions { get; } = new();
    public ObservableCollection<NavItem> PrimaryNav { get; } = new();
    public ObservableCollection<ProjectItem> Projects { get; } = new();
    public ObservableCollection<ProjectGroupItem> ProjectGroups { get; } = new();
    public ObservableCollection<ProjectGroupItem> ProjectPickerItems { get; } = new();
    public ObservableCollection<SessionItem> Sessions { get; } = new();
    public ObservableCollection<ExplorerItem> ExplorerItems { get; } = new();
    public ObservableCollection<FileExplorerNode> ExplorerRoots { get; } = new();
    public ObservableCollection<ConfigItem> ConfigItems { get; } = new();
    public ObservableCollection<BottomNavItem> BottomTabs { get; } = new();
    public ObservableCollection<ToolbarActionItem> ToolbarActions { get; } = new();
    public ObservableCollection<QuickActionItem> StartActions { get; } = new();
    public ObservableCollection<SuggestionItem> Suggestions { get; } = new();
    public ObservableCollection<AttachmentItem> Attachments { get; } = new();
    public ObservableCollection<VoiceOptionItem> VoiceOptions { get; } = new();
    public ObservableCollection<ApprovalOptionItem> ApprovalOptions { get; } = new();
    public ObservableCollection<ReasoningOptionItem> ReasoningOptions { get; } = new();
    public ObservableCollection<ModelOptionItem> ModelOptions { get; } = new();
    public ObservableCollection<BranchItem> BranchItems { get; } = new();
    public ObservableCollection<ChatMarkerItem> ChatMarkers { get; } = new();
    public ObservableCollection<ToolApprovalRequestItem> ToolApprovalRequests { get; } = new();
    public ObservableCollection<PanelRow> ContentRows { get; } = new();
    public ObservableCollection<PanelRow> InspectorRows { get; } = new();
    public ObservableCollection<RightPanelActionItem> RightPanelActions { get; } = new();

    public MainWindowViewModel(ArchiveStoreService? archiveStore = null)
    {
        _archive = archiveStore ?? new ArchiveStoreService();
        LoadProjectDisplayNames();
        LoadUiSettings();
        ProjectPath = FindWorkspacePath();

        RebuildChromeCollections();
        RebuildVoiceOptions();
        Projects.Add(new(ProjectName, ProjectPathShort, L("当前工作区", "Current workspace"), ProjectPath));
        ProjectGroups.Add(new(ProjectName, ProjectPathShort, ProjectPath, new ObservableCollection<SessionItem>(), LooksLikeGitRepository(ProjectPath)));
        UpdateApprovalOptions();
        UpdateReasoningOptions();
        _runElapsedTimer.Tick += (_, _) => UpdateLiveStatusRow();

        RefreshLocalData();
        LoadHome();
        _ = CheckForAppUpdateAsync();
    }

    public async Task CheckForAppUpdateAsync()
    {
        if (IsUpdateCheckRunning || IsUpdateApplyRunning) return;
        var repoRoot = FindAppRepoRoot();
        if (repoRoot is null)
        {
            UpdateStatusText = L("未找到 Git 仓库，无法检查更新。", "No Git repository found; update check unavailable.");
            return;
        }

        IsUpdateCheckRunning = true;
        UpdateStatusText = L("正在检查上游更新…", "Checking upstream updates…");
        UpdateDetailText = repoRoot;
        try
        {
            await RunProcessAsync("git", new[] { "fetch", "--quiet" }, repoRoot);
            var upstream = (await RunProcessAsync("git", new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" }, repoRoot)).Trim();
            var ahead = (await RunProcessAsync("git", new[] { "rev-list", "--count", $"HEAD..{upstream}" }, repoRoot)).Trim();
            var count = int.TryParse(ahead, out var parsed) ? parsed : 0;
            _updateRepoRoot = repoRoot;
            _updateUpstream = upstream;
            HasUpstreamUpdate = count > 0;
            UpdateStatusText = count > 0
                ? L($"上游有 {count} 个新提交。", $"{count} upstream commit(s) available.")
                : L("当前已是最新。", "Already up to date.");
            UpdateDetailText = string.IsNullOrWhiteSpace(upstream) ? repoRoot : $"{upstream} · {repoRoot}";
        }
        catch (Exception ex)
        {
            HasUpstreamUpdate = false;
            UpdateStatusText = L("检查更新失败。", "Update check failed.");
            UpdateDetailText = ex.Message;
        }
        finally
        {
            IsUpdateCheckRunning = false;
        }
    }

    public async Task ApplyAppUpdateAsync()
    {
        if (!CanApplyUpdate) return;
        var repoRoot = _updateRepoRoot ?? FindAppRepoRoot();
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (repoRoot is null || string.IsNullOrWhiteSpace(exePath))
        {
            UpdateStatusText = L("无法定位应用或仓库路径。", "Unable to locate app or repository path.");
            return;
        }

        IsUpdateApplyRunning = true;
        UpdateStatusText = L("正在启动更新器，ipi 将自动重启…", "Starting updater; ipi will restart automatically…");
        UpdateDetailText = L("请等待窗口关闭并重新打开。", "Wait for the window to close and reopen.");
        await Task.Delay(200);

        var scriptPath = Path.Combine(Path.GetTempPath(), $"ipi-update-{Environment.ProcessId}.ps1");
        var projectPath = Path.Combine(repoRoot, "apps", "windows", "Ipi.Desktop", "Ipi.Desktop.csproj");
        var logPath = Path.Combine(Path.GetTempPath(), $"ipi-update-{Environment.ProcessId}.log");
        var script = $$"""
$ErrorActionPreference = 'Stop'
$pidToWait = {{Environment.ProcessId}}
$repo = {{PowerShellQuote(repoRoot)}}
$project = {{PowerShellQuote(projectPath)}}
$exe = {{PowerShellQuote(exePath)}}
$log = {{PowerShellQuote(logPath)}}
Start-Sleep -Milliseconds 500
try { Wait-Process -Id $pidToWait -Timeout 45 -ErrorAction SilentlyContinue } catch {}
Set-Location $repo
"[$(Get-Date -Format o)] git pull --ff-only" | Out-File -FilePath $log -Encoding utf8
& git pull --ff-only 2>&1 | Tee-Object -FilePath $log -Append
"[$(Get-Date -Format o)] dotnet publish" | Tee-Object -FilePath $log -Append
& dotnet publish $project -c Release -r win-x64 --self-contained false 2>&1 | Tee-Object -FilePath $log -Append
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
""";
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = repoRoot,
        });
        Application.Current.Shutdown();
    }

    private static string? FindAppRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<string> RunProcessAsync(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        return stdout;
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private void SetSystemContextMode(bool enabled)
    {
        if (_isSystemContextPage == enabled && (!enabled || !_isPluginsPage)) return;
        _isSystemContextPage = enabled;
        if (enabled) _isPluginsPage = false;
        OnPropertyChanged(nameof(SystemContextVisibility));
        OnPropertyChanged(nameof(PluginsPageVisibility));
        OnPropertyChanged(nameof(MainPanelVisibility));
        OnPropertyChanged(nameof(MainHeroVisibility));
        OnPropertyChanged(nameof(MainRowsVisibility));
    }

    private void SetPluginsPageMode(bool enabled)
    {
        if (_isPluginsPage == enabled && (!enabled || !_isSystemContextPage)) return;
        _isPluginsPage = enabled;
        if (enabled) _isSystemContextPage = false;
        OnPropertyChanged(nameof(SystemContextVisibility));
        OnPropertyChanged(nameof(PluginsPageVisibility));
        OnPropertyChanged(nameof(MainPanelVisibility));
        OnPropertyChanged(nameof(MainHeroVisibility));
        OnPropertyChanged(nameof(MainRowsVisibility));
    }

    public void SetLanguage(string language)
    {
        var normalized = language == "en-US" ? "en-US" : "zh-CN";
        if (_language == normalized) return;
        _language = normalized;
        RebuildChromeCollections();
        RebuildRightPanelActions();
        RebuildVoiceOptions();
        UpdateApprovalOptions();
        UpdateReasoningOptions();
        UpdateModelControlLabel();
        LoadSidebarConfig(_pi.ReadSettingsSummary());
        LoadSidebarProjectGroups();
        LoadSidebarSessions();
        RefreshLocalizedPanelText();
        if (_isPluginsPage) BuildPluginPackages(_selectedPluginPackage?.Source);
        NotifyLanguagePropertiesChanged();
    }

    private void RebuildVoiceOptions()
    {
        VoiceOptions.Clear();
        VoiceOptions.Add(new("windows-dictation", "Windows Dictation", L("免费 · 系统听写 · 控制权交给 Windows，不切换成停止按钮", "Free · system dictation · Windows owns the mic controls"), "free", true));
        VoiceOptions.Add(new("windows-native", "Windows Native", L("免费 · app 控制开始/停止 · 依赖本机 SpeechRecognizer", "Free · app-controlled start/stop · uses local SpeechRecognizer"), "free", true));
        VoiceOptions.Add(new("openai-transcribe", "OpenAI Transcribe", L("需要 API · 高质量 · 待配置录音上传", "API required · higher quality · upload flow not configured yet"), "api", false));
        VoiceOptions.Add(new("local-whisper", "Local Whisper", L("离线 · 需要本地模型/运行环境 · 待配置", "Offline · requires local model/runtime setup"), "local", false));
        OnPropertyChanged(nameof(VoiceBackendLabel));
    }

    private void RebuildChromeCollections()
    {
        MenuItems.Clear();
        foreach (var item in new[] { FileMenuText, EditMenuText, ViewMenuText, HelpMenuText }) MenuItems.Add(item);

        TopActions.Clear();
        foreach (var item in new[] { L("会话树", "Session tree"), L("上下文", "Context"), L("状态", "Status") }) TopActions.Add(item);

        PrimaryNav.Clear();

        RebuildRightPanelActions();

        BottomTabs.Clear();

        ToolbarActions.Clear();
        ToolbarActions.Add(new("download", ExportText, "export"));
        ToolbarActions.Add(new("git-branch", BranchesText, "branches"));
        ToolbarActions.Add(new("terminal-square", SystemText, "system"));

        StartActions.Clear();
        StartActions.Add(new(L("最近", "Recent"), "recent"));
        StartActions.Add(new(L("会话", "Sessions"), "sessions"));
        StartActions.Add(new(SystemText, "system"));
    }

    private void RebuildRightPanelActions()
    {
        RightPanelActions.Clear();
        RightPanelActions.Add(new("shield-check", L("审查", "Review"), "Ctrl+Shift+G", "review"));
        RightPanelActions.Add(new("terminal-square", L("终端", "Terminal"), "", "terminal"));
        RightPanelActions.Add(new("globe", L("浏览器", "Browser"), "Ctrl+T", "browser"));
        RightPanelActions.Add(new("folder", L("文件", "Files"), "Ctrl+P", "files"));
        RightPanelActions.Add(new("message-square", L("侧边聊天", "Side chat"), "Ctrl+Alt+S", "chat"));
    }

    private void RefreshLocalizedPanelText()
    {
        if (!IsChatMode && IsStartActionsVisible)
        {
            PanelTitle = L("我们该做什么？", "What should we do?");
            PanelSubtitle = IsGlobalChat ? L("默认聊天", "Default chat") : $"{L("项目", "Project")} · {ProjectPathShort}";
        }
        else if (PanelSubtitle.StartsWith("项目 ·", StringComparison.OrdinalIgnoreCase) || PanelSubtitle.StartsWith("Project ·", StringComparison.OrdinalIgnoreCase))
        {
            PanelSubtitle = $"{L("项目", "Project")} · {ProjectPathShort}";
        }
        else if (PanelSubtitle == "默认聊天" || PanelSubtitle == "Default chat")
        {
            PanelSubtitle = L("默认聊天", "Default chat");
        }
    }

    private void UpdateModelControlLabel() => UpdateModelLabels();

    private void UpdateModelLabels()
    {
        var model = string.IsNullOrWhiteSpace(_activeModel) ? "model" : _activeModel;
        ModelLabel = $"model: {model} · {ThinkingLevel}";
        ModelControlLabel = $"{ShortModel(model)} {FormatThinking(ThinkingLevel)}";
        ModelMenuLabel = PrettyModel(model);
    }

    private void RebuildModelOptions()
    {
        var combined = new List<PiModelOptionRecord>();
        void Add(PiModelOptionRecord option)
        {
            if (string.IsNullOrWhiteSpace(option.Provider) || string.IsNullOrWhiteSpace(option.Model)) return;
            if (option.Provider == "unknown" || option.Model == "unknown") return;
            if (combined.Any(item => item.Provider.Equals(option.Provider, StringComparison.OrdinalIgnoreCase) && item.Model.Equals(option.Model, StringComparison.OrdinalIgnoreCase))) return;
            combined.Add(option);
        }

        foreach (var option in _registryModelOptions) Add(option);
        foreach (var option in _pi.ReadModelOptions((_activeProvider, _activeModel, ThinkingLevel))) Add(option);

        if (combined.Count == 0 && !string.IsNullOrWhiteSpace(_activeModel) && _activeModel != "unknown")
        {
            combined.Add(new PiModelOptionRecord(_activeProvider, _activeModel, _activeModel, "settings.json", true));
        }

        ModelOptions.Clear();
        foreach (var option in combined
            .OrderByDescending(item => item.Provider.Equals(_activeProvider, StringComparison.OrdinalIgnoreCase) && item.Model.Equals(_activeModel, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var selected = option.Provider.Equals(_activeProvider, StringComparison.OrdinalIgnoreCase) && option.Model.Equals(_activeModel, StringComparison.OrdinalIgnoreCase);
            ModelOptions.Add(new ModelOptionItem(option.Provider, option.Model, PrettyModel(option.DisplayName), option.Provider, option.Source, selected ? "✓" : ""));
        }
    }

    private async Task LoadRegistryModelOptionsAsync()
    {
        var version = ++_modelOptionsLoadVersion;
        try
        {
            var models = await _agentBridge.ListModelsAsync(ProjectPath, _pi.AgentDir);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (version != _modelOptionsLoadVersion) return;
                _registryModelOptions = models.ToList();
                RebuildModelOptions();
            });
        }
        catch
        {
            // Keep the settings.json/models.json fallback visible if Node or Pi registry is unavailable.
        }
    }

    private void NotifyLanguagePropertiesChanged()
    {
        foreach (var name in new[]
        {
            nameof(ProjectName), nameof(ProjectPathShort), nameof(WorkspaceChipLabel), nameof(FileMenuText), nameof(EditMenuText), nameof(ViewMenuText), nameof(HelpMenuText),
            nameof(MenuNewWindowText), nameof(MenuNewChatText), nameof(MenuQuickChatText), nameof(MenuOpenFolderText), nameof(MenuCloseText),
            nameof(MenuSettingsText), nameof(MenuLogOutText), nameof(MenuExitText), nameof(MenuUndoText), nameof(MenuCutText), nameof(MenuCopyText),
            nameof(MenuPasteText), nameof(MenuSelectAllText), nameof(ToggleSidebarToolTip), nameof(BackToChatToolTip), nameof(OpenRecentChatToolTip), nameof(ToggleThemeToolTip), nameof(SettingsToolTip), nameof(ToggleRightPanelToolTip), nameof(RefreshLocalDataToolTip), nameof(AddFileToolTip), nameof(VoiceModeToolTip), nameof(VoiceModeTitle), nameof(OpenCurrentSessionLocationToolTip), nameof(CurrentProjectToolTip), nameof(ApprovalModeToolTip), nameof(ReturnToChatText), nameof(CopyMessageText), nameof(EditFromHereText), nameof(NewSessionFromHereText), nameof(SendButtonToolTip), nameof(MenuToggleSidebarText), nameof(MenuToggleBottomPanelText), nameof(MenuTogglePinnedSummaryText),
            nameof(MenuOpenTerminalText), nameof(MenuToggleFileTreeText), nameof(MenuOpenBrowserTabText), nameof(MenuFocusBrowserAddressBarText),
            nameof(MenuReloadBrowserPageText), nameof(MenuToggleSidePanelText), nameof(MenuFindText), nameof(MenuPreviousChatText), nameof(MenuNextChatText),
            nameof(MenuBackText), nameof(MenuForwardText), nameof(MenuZoomInText), nameof(MenuZoomOutText), nameof(MenuActualSizeText), nameof(MenuToggleFullScreenText),
            nameof(ExportText), nameof(BranchesText), nameof(BranchSearchPlaceholder), nameof(BranchCreateFallbackText), nameof(BranchCreateLabel), nameof(SystemText), nameof(UpdateAvailableText), nameof(UpdatePopupTitle), nameof(UpdatePopupDescription), nameof(UpdateActionText), nameof(WorkspaceTitle), nameof(WorkspaceSubtitle), nameof(ProjectsText), nameof(ExplorerText), nameof(RightPanelText), nameof(SideTerminalTitle), nameof(SideBrowserTitle), nameof(SideFilesTitle), nameof(SideChatTitle), nameof(SideTerminalRunText), nameof(SideBrowserOpenText), nameof(SideBrowserNativeNote), nameof(SideChatContextLabel), nameof(SideChatSendText), nameof(SideChatTemporaryNote), nameof(NewTemporarySideChatText), nameof(AddToConversationText), nameof(AskInSideChatText),
            nameof(ConversationsText), nameof(ConfigText), nameof(PromptPlaceholderText), nameof(OpenLocationText), nameof(CurrentProjectText),
            nameof(LocalReadyText), nameof(NewProjectText), nameof(NewBlankProjectText), nameof(UseExistingFolderText), nameof(DefaultChatNoProjectText), nameof(ProjectActionsText), nameof(SidebarArchiveAllChatsText), nameof(SidebarOrganizeByProjectText), nameof(SidebarRecentProjectsText), nameof(SidebarSortChronologicalText), nameof(SidebarSortRecentText), nameof(ProjectPinText), nameof(ProjectOpenExplorerText), nameof(ProjectCreateWorktreeText), nameof(ProjectRenameText), nameof(ProjectNewChatText), nameof(ProjectRemoveText),
            nameof(ApprovalQuestionText), nameof(ReasoningText), nameof(ModelText), nameof(ApprovalLabel), nameof(ApprovalMode),
            nameof(PluginPageDescription), nameof(PluginAddButtonText), nameof(PluginUpdateButtonText), nameof(PluginReloadButtonText),
            nameof(PluginRemoveButtonText), nameof(PluginCancelButtonText), nameof(PluginEmptyTitle), nameof(PluginEmptyDescription),
            nameof(PluginStatusLabel), nameof(PluginVersionLabel), nameof(PluginPackageLabel), nameof(PluginResourcesLabel),
            nameof(PluginInstalledPathLabel), nameof(PluginScopeLabel), nameof(PluginResolvedResourcesLabel), nameof(PluginFooterText)
        })
        {
            OnPropertyChanged(name);
        }
    }

    public void RefreshLocalData()
    {
        var archivedPaths = _archive.ArchivedSessionPaths();
        _sessions = _pi.ListSessions(180)
            .Where(session => !archivedPaths.Contains(NormalizePath(session.FilePath)))
            .OrderByDescending(session => session.Modified)
            .Take(140)
            .ToList();
        _files = _pi.ListWorkspaceFiles(ProjectPath, 1800).ToList();
        _skillSources = _pi.ListSkillSources().ToList();
        _skills = _pi.ListSkills(260).ToList();
        _packages = _pi.ListPackages().ToList();
        var settings = _pi.ReadSettingsSummary();
        if (!_modelSelectionInitialized || string.IsNullOrWhiteSpace(_activeModel) || _activeModel == "unknown")
        {
            ThinkingLevel = NormalizeThinking(settings.DefaultThinking);
            _activeProvider = settings.DefaultProvider;
            _activeModel = settings.DefaultModel;
            _modelSelectionInitialized = true;
        }
        _contextLimitTokens = _pi.ReadContextWindow(_activeProvider, _activeModel);
        UpdateModelLabels();
        UpdateReasoningOptions();
        RebuildModelOptions();
        _ = LoadRegistryModelOptionsAsync();
        LoadSidebarProjectGroups();
        LoadSidebarSessions();
        LoadSidebarExplorer();
        LoadExplorerTree();
        LoadSidebarConfig(settings);
        var enabledSkills = _skills.Count(skill => skill.IsEnabled);
        LocalSummaryText = $"{_sessions.Count} sessions · {_files.Count} files · {enabledSkills}/{_skills.Count} skills";
        SetTopUsage(new SessionUsageSummary(0, 0, 0, 0, 0, 0));
        StatusText = $"loaded {_sessions.Count} sessions · {_files.Count} files · {enabledSkills}/{_skills.Count} skills · {_packages.Count} packages";
    }

    public async void SendPrompt()
    {
        if (IsAgentRunning)
        {
            _runCancellation?.Cancel();
            StatusText = "stopping current run";
            return;
        }

        var text = PromptText.Trim();
        if (text.Length == 0) return;

        if (text.StartsWith('/'))
        {
            PromptText = string.Empty;
            RunSlashCommand(text);
            return;
        }

        if (PanelTitle == L("搜索", "Search"))
        {
            PromptText = string.Empty;
            RunSearch(text);
            return;
        }

        var toolConfig = BuildToolRunConfig();

        var editFromRowIndex = _pendingEditFromRowIndex;
        var sessionFileForRun = editFromRowIndex.HasValue ? _pendingEditSessionFile ?? _activeSessionFile : _activeSessionFile;
        var branchFromEntryId = _pendingBranchFromEntryId;
        if (editFromRowIndex.HasValue)
        {
            if (string.IsNullOrWhiteSpace(sessionFileForRun) || !File.Exists(sessionFileForRun))
            {
                StatusText = L("无法编辑：没有找到原对话文件。", "Cannot edit: original session file was not found.");
                return;
            }

            if (branchFromEntryId is null && _pendingEditUserOrdinal.HasValue)
            {
                branchFromEntryId = FindParentIdForNthUserMessage(sessionFileForRun, _pendingEditUserOrdinal.Value);
            }

            if (branchFromEntryId is null)
            {
                StatusText = L("无法编辑：没有找到这条消息的分支位置。", "Cannot edit: branch point for this message was not found.");
                return;
            }
        }

        var userAttachments = Attachments.Select(a => new ChatAttachmentPreviewItem(a.Name, a.Path, a.Detail)).ToList();
        var userDetail = "";
        var agentMessage = BuildPromptWithAttachments(text);
        Attachments.Clear();
        PromptText = string.Empty;

        var continuing = !string.IsNullOrWhiteSpace(sessionFileForRun) && ContentRows.Count > 0;
        var runVersion = ++_runVersion;
        var runCancellation = new CancellationTokenSource();
        IsAgentRunning = true;
        _runStartedAt = DateTime.Now;
        _runElapsedTimer.Start();
        _runAllowedTools.Clear();
        _runCancellation = runCancellation;
        IsChatMode = true;
        IsStartActionsVisible = false;
        CanReturnToChat = false;
        PanelTitle = continuing ? PanelTitle : text.Length > 38 ? text[..38] + "…" : text;
        PanelSubtitle = IsGlobalChat ? L("默认聊天", "Default chat") : $"{L("项目", "Project")} · {ProjectPathShort}";
        IsComposerVisible = true;
        IsToolbarVisible = true;
        IsInspectorVisible = false;
        SessionStatsText = string.Empty;
        if (editFromRowIndex.HasValue)
        {
            var index = Math.Clamp(editFromRowIndex.Value, 0, ContentRows.Count);
            while (ContentRows.Count > index) ContentRows.RemoveAt(ContentRows.Count - 1);
            RebuildChatMarkersFromRows();
        }
        else if (!continuing)
        {
            ContentRows.Clear();
            ChatMarkers.Clear();
        }
        Suggestions.Clear();
        AddUserRow(text, userDetail, userAttachments);
        AddOrUpdateLiveStatus(L("正在准备回复…", "Preparing reply…"));
        StatusText = $"starting local agent · {ApprovalLabel} · {ToolLabel}";

        try
        {
            var runCwd = ResolveRunCwd();
            ClearPendingEditFromHere(false);
            var result = await _agentBridge.RunPromptAsync(runCwd, _pi.AgentDir, agentMessage, evt => AddBridgeEvent(runVersion, evt), sessionFileForRun, ThinkingLevel, toolConfig.Tools, toolConfig.NoTools, RequestToolApprovalAsync, ApprovalModeKey(), ApprovalRulesForBridge(), branchFromEntryId: branchFromEntryId, provider: _activeProvider, model: _activeModel, cancellationToken: runCancellation.Token);
            await SwitchToUiAsync();
            if (!IsCurrentRun(runVersion)) return;
            RemoveLiveStatusRows();
            var finalText = !string.IsNullOrWhiteSpace(result.FinalText)
                ? TrimForRow(result.FinalText, 8000)
                : L("Agent 已完成，但没有返回文本。可从侧边栏打开保存的会话查看完整记录。", "Agent finished without a text response. Open the saved session from the sidebar for full details.");
            await RevealAssistantMessageAsync(finalText, runCancellation.Token);
            if (!string.IsNullOrWhiteSpace(result.SessionFile)) _activeSessionFile = result.SessionFile;
            StatusText = string.IsNullOrWhiteSpace(result.SessionId) ? "agent finished" : $"agent finished · {result.SessionId}";
            SessionStatsText = L("就绪", "Ready");
            RefreshLocalData();
            if (!string.IsNullOrWhiteSpace(_activeSessionFile) && File.Exists(_activeSessionFile))
            {
                SetTopUsage(_pi.ReadSessionUsageSummary(_activeSessionFile));
            }
        }
        catch (OperationCanceledException)
        {
            await SwitchToUiAsync();
            if (IsCurrentRun(runVersion))
            {
                RemoveLiveStatusRows();
                ContentRows.Add(new("state", L("Agent 已停止", "Agent run stopped"), L("本轮生成已停止。", "This run has stopped."), null, "message"));
                StatusText = "agent stopped";
                SessionStatsText = L("已停止", "Stopped");
            }
        }
        catch (Exception ex)
        {
            await SwitchToUiAsync();
            if (IsCurrentRun(runVersion))
            {
                RemoveLiveStatusRows();
                ContentRows.Add(new("error", "Local agent failed", ex.Message, null, "message"));
                StatusText = "agent failed";
                SessionStatsText = "error";
            }
        }
        finally
        {
            if (IsCurrentRun(runVersion))
            {
                _runElapsedTimer.Stop();
                IsAgentRunning = false;
                if (ReferenceEquals(_runCancellation, runCancellation)) _runCancellation = null;
                ToolApprovalRequests.Clear();
            }
            runCancellation.Dispose();
        }
    }

    public void SelectNav(object? item)
    {
        CancelSessionLoad();
        SetSystemContextMode(false);
        SetPluginsPageMode(false);
        IsStartActionsVisible = false;
        CanReturnToChat = false;
        switch (item)
        {
            case NavItem nav:
                if (nav.Kind == "new") LoadHome();
                else if (nav.Kind == "search") LoadSearchPage();
                else if (nav.Kind == "scheduled") LoadRunningPage();
                break;
            case ProjectItem project:
                LoadFilesPage(project.RootPath);
                break;
            case ProjectGroupItem project:
                SelectProjectFolder(project.RootPath);
                break;
            case ExplorerItem explorer:
                if (explorer.IsDirectory) LoadFilesPage(explorer.Path);
                else OpenFile(explorer.Path, explorer.Name);
                break;
            case BottomNavItem bottom:
                if (bottom.Kind == "models") LoadModelsPage();
                else if (bottom.Kind == "skills") LoadSkillsPage();
                break;
            case ToolbarActionItem action:
                if (action.Kind == "export") ExportSession(_activeSessionFile);
                else if (action.Kind == "branches") ToggleBranchPicker();
                else LoadSystemPage();
                break;
            case QuickActionItem action:
                if (action.Kind == "recent")
                {
                    var recent = _sessions.FirstOrDefault();
                    if (recent is not null) OpenSession(recent.FilePath, recent.Title);
                    else StatusText = "no recent sessions";
                }
                else if (action.Kind == "sessions") LoadSessionsPage();
                else if (action.Kind == "models") LoadModelsPage();
                else if (action.Kind == "skills") LoadSkillsPage();
                else LoadSystemPage();
                break;
            case SessionItem session:
                OpenSession(session.FilePath, session.Title);
                break;
            case ConfigItem config:
                if (config.Kind == "models") LoadModelsPage();
                else if (config.Kind == "tools") LoadToolsPage();
                else if (config.Kind == "settings") LoadSettingsPage();
                else LoadSkillsPage();
                break;
        }
    }

    public void OpenRow(PanelRow row)
    {
        CancelSessionLoad();
        SetSystemContextMode(false);
        IsStartActionsVisible = false;
        CanReturnToChat = false;
        if (row.Kind == "session") OpenSession(row.Path, row.Title);
        else if (row.Kind == "file") OpenFile(row.Path, row.Title);
        else if (row.Kind == "skill") OpenSkill(row.Path, row.Title);
        else if (row.Kind == "skillToggle") ToggleSkillEnabled(row.Path);
        else if (row.Kind == "skillSourceToggle") ToggleSkillSourceEnabled(row.Path);
        else if (row.Kind == "files") LoadFilesPage(row.Path ?? ProjectPath);
        else if (row.Kind == "export") ExportSession(row.Path);
        else if (row.Kind == "toolPreset") SetToolPreset(row.Path);
        else if (row.Kind == "command")
        {
            PromptText = row.Detail;
            StatusText = "loaded suggestion into composer";
        }
    }

    public void UseSuggestion(SuggestionItem item)
    {
        if (item.Command == "open-recent")
        {
            var recent = _sessions.FirstOrDefault();
            if (recent is not null) OpenSession(recent.FilePath, recent.Title);
            return;
        }

        if (item.Command.StartsWith('/'))
        {
            RunSlashCommand(item.Command);
            return;
        }

        PromptText = item.Command;
        StatusText = "suggestion loaded into composer";
    }

    public void ConnectLocal()
    {
        RefreshLocalData();
        LoadHome();
    }

    private bool IsCurrentRun(int runVersion) => runVersion == _runVersion;

    private void CancelActiveRunForNewConversation()
    {
        if (!IsAgentRunning && _runCancellation is null) return;
        _runVersion++;
        try { _runCancellation?.Cancel(); } catch { }
        _runElapsedTimer.Stop();
        _liveStatusTitle = string.Empty;
        _liveStatusDetail = string.Empty;
        IsAgentRunning = false;
        _runCancellation = null;
        _runAllowedTools.Clear();
        ToolApprovalRequests.Clear();
    }

    public void NewConversation()
    {
        CancelActiveRunForNewConversation();
        LoadHome();
    }

    public void ReturnToChat()
    {
        if (!string.IsNullOrWhiteSpace(_activeSessionFile) && File.Exists(_activeSessionFile))
        {
            var session = _sessions.FirstOrDefault(s => s.FilePath == _activeSessionFile);
            OpenSession(_activeSessionFile, session?.Title ?? "Chat");
            return;
        }

        LoadHome();
    }

    public void ToggleBranchPicker()
    {
        if (IsBranchPickerOpen)
        {
            IsBranchPickerOpen = false;
            return;
        }

        BranchSearchText = string.Empty;
        RefreshBranches();
        IsBranchPickerOpen = true;
    }

    public void RefreshBranches()
    {
        _allBranchItems.Clear();
        BranchItems.Clear();
        _branchRepoRoot = ResolveGitRoot(ActiveLocationPath);

        if (string.IsNullOrWhiteSpace(_branchRepoRoot))
        {
            BranchStatusText = L("当前工作区不是 Git 仓库", "Current workspace is not a Git repository");
            StatusText = BranchStatusText;
            OnPropertyChanged(nameof(CanCreateBranch));
            OnPropertyChanged(nameof(BranchCreateLabel));
            return;
        }

        var current = RunGit(_branchRepoRoot, "branch", "--show-current").Output.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            var detached = RunGit(_branchRepoRoot, "rev-parse", "--short", "HEAD");
            current = detached.ExitCode == 0 ? $"detached@{detached.Output.Trim()}" : "detached";
        }

        var status = RunGit(_branchRepoRoot, "status", "--porcelain");
        var dirtyCount = status.ExitCode == 0 ? CountNonEmptyLines(status.Output) : 0;
        var dirtyText = dirtyCount > 0 ? L($"未提交：{dirtyCount} 个文件", $"Uncommitted: {dirtyCount} files") : L("工作区干净", "Clean working tree");
        BranchStatusText = $"{current} · {dirtyText}";

        var branches = RunGit(_branchRepoRoot, "branch", "--format=%(refname:short)");
        var names = branches.ExitCode == 0
            ? branches.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        if (!names.Any(n => n.Equals(current, StringComparison.OrdinalIgnoreCase))) names.Insert(0, current);

        foreach (var name in names.OrderByDescending(n => n.Equals(current, StringComparison.OrdinalIgnoreCase)).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var detail = name.Equals(current, StringComparison.OrdinalIgnoreCase) ? dirtyText : L("本地分支", "Local branch");
            _allBranchItems.Add(new BranchItem(name, detail, name.Equals(current, StringComparison.OrdinalIgnoreCase)));
        }

        FilterBranches();
        StatusText = $"git branches · {Path.GetFileName(_branchRepoRoot)}";
    }

    public void CheckoutBranch(BranchItem branch)
    {
        if (string.IsNullOrWhiteSpace(_branchRepoRoot) || branch.IsCurrent)
        {
            IsBranchPickerOpen = false;
            return;
        }

        var result = RunGit(_branchRepoRoot, "switch", branch.Name);
        if (result.ExitCode != 0) result = RunGit(_branchRepoRoot, "checkout", branch.Name);
        if (result.ExitCode == 0)
        {
            StatusText = L($"已切换到 {branch.Name}", $"switched to {branch.Name}");
            RefreshBranches();
            IsBranchPickerOpen = false;
        }
        else
        {
            StatusText = TrimForRow(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error, 180);
            RefreshBranches();
        }
    }

    public void CreateAndCheckoutBranch()
    {
        if (!CanCreateBranch || string.IsNullOrWhiteSpace(_branchRepoRoot)) return;
        var name = BranchSearchText.Trim();
        var check = RunGit(_branchRepoRoot, "check-ref-format", "--branch", name);
        if (check.ExitCode != 0)
        {
            StatusText = L("分支名称无效", "invalid branch name");
            return;
        }

        var result = RunGit(_branchRepoRoot, "switch", "-c", name);
        if (result.ExitCode != 0) result = RunGit(_branchRepoRoot, "checkout", "-b", name);
        if (result.ExitCode == 0)
        {
            BranchSearchText = string.Empty;
            RefreshBranches();
            IsBranchPickerOpen = false;
            StatusText = L($"已创建并切换到 {name}", $"created and switched to {name}");
        }
        else
        {
            StatusText = TrimForRow(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error, 180);
            RefreshBranches();
        }
    }

    private void FilterBranches()
    {
        BranchItems.Clear();
        var query = BranchSearchText.Trim();
        var items = string.IsNullOrWhiteSpace(query)
            ? _allBranchItems
            : _allBranchItems.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var item in items.Take(40)) BranchItems.Add(item);
        OnPropertyChanged(nameof(CanCreateBranch));
        OnPropertyChanged(nameof(BranchCreateLabel));
    }

    public void ToggleSidebar()
    {
        if (IsSidebarAutoHidden)
        {
            IsSidebarAutoHidden = false;
            IsSidebarPeekOpen = false;
            IsSidebarExpanded = true;
            return;
        }

        IsSidebarExpanded = !IsSidebarExpanded;
    }

    public void ToggleBottomPanel()
    {
        IsBottomPanelVisible = !IsBottomPanelVisible;
        StatusText = IsBottomPanelVisible ? "bottom panel opened" : "bottom panel closed";
    }

    public void ToggleSessionInfoPopup()
    {
        if (IsSessionInfoPopupOpen)
        {
            IsSessionInfoPopupOpen = false;
            return;
        }

        RefreshSessionInfoPopup();
        IsSessionInfoPopupOpen = true;
    }

    private void RefreshSessionInfoPopup()
    {
        var summary = !string.IsNullOrWhiteSpace(_activeSessionFile) && File.Exists(_activeSessionFile)
            ? _pi.ReadSessionInspectionSummary(_activeSessionFile)
            : new SessionInspectionSummary("No active session", "-", 0, 0, 0, 0, 0, _currentUsage);

        var usage = summary.Usage;
        SessionInfoFile = summary.FilePath;
        SessionInfoId = string.IsNullOrWhiteSpace(summary.Id) ? "-" : summary.Id;
        SessionInfoUserMessages = summary.UserMessages.ToString("N0");
        SessionInfoAssistantMessages = summary.AssistantMessages.ToString("N0");
        SessionInfoToolCalls = summary.ToolCalls.ToString("N0");
        SessionInfoToolResults = summary.ToolResults.ToString("N0");
        SessionInfoTotalMessages = summary.TotalMessages.ToString("N0");
        SessionInfoInputTokens = usage.Input.ToString("N0");
        SessionInfoOutputTokens = usage.Output.ToString("N0");
        SessionInfoCacheReadTokens = usage.CacheRead.ToString("N0");
        var contextTokens = usage.TotalTokens > 0 ? usage.TotalTokens : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
        SessionInfoTotalTokens = contextTokens.ToString("N0");
        SessionInfoCost = $"${usage.Cost:F4}";
        SessionInfoContext = _contextLimitTokens is > 0
            ? $"{Math.Min(999, Math.Round(contextTokens * 100.0 / _contextLimitTokens.Value, 1)):0.#}% / {FormatCompactNumber(_contextLimitTokens.Value)}"
            : $"{FormatCompactNumber(contextTokens)} tok";
        foreach (var property in new[]
        {
            nameof(SessionInfoFile), nameof(SessionInfoId), nameof(SessionInfoUserMessages), nameof(SessionInfoAssistantMessages),
            nameof(SessionInfoToolCalls), nameof(SessionInfoToolResults), nameof(SessionInfoTotalMessages), nameof(SessionInfoInputTokens),
            nameof(SessionInfoOutputTokens), nameof(SessionInfoCacheReadTokens), nameof(SessionInfoTotalTokens), nameof(SessionInfoCost), nameof(SessionInfoContext)
        }) OnPropertyChanged(property);
    }

    public void TogglePinnedSummary()
    {
        IsPinnedSummaryVisible = !IsPinnedSummaryVisible;
        OnPropertyChanged(nameof(PinnedSummaryText));
        StatusText = IsPinnedSummaryVisible ? "pinned summary visible" : "pinned summary hidden";
    }

    public string CurrentRunDirectory => ResolveRunCwd();

    public void OpenAdjacentSession(int direction)
    {
        if (_sessions.Count == 0)
        {
            StatusText = "no recent sessions";
            return;
        }

        var currentIndex = string.IsNullOrWhiteSpace(_activeSessionFile)
            ? -1
            : _sessions.FindIndex(s => s.FilePath.Equals(_activeSessionFile, StringComparison.OrdinalIgnoreCase));
        var nextIndex = currentIndex < 0 ? 0 : Math.Clamp(currentIndex + direction, 0, _sessions.Count - 1);
        if (nextIndex == currentIndex)
        {
            StatusText = direction < 0 ? "already at newest chat" : "already at oldest chat";
            return;
        }

        var session = _sessions[nextIndex];
        OpenSession(session.FilePath, session.Title);
    }

    public void ToggleProjectPicker()
    {
        IsBottomProjectPickerOpen = false;
        IsProjectPickerOpen = !IsProjectPickerOpen;
        if (!IsProjectPickerOpen) IsNewProjectMenuOpen = false;
    }

    public void ToggleBottomProjectPicker()
    {
        IsProjectPickerOpen = false;
        IsBottomProjectPickerOpen = !IsBottomProjectPickerOpen;
        if (!IsBottomProjectPickerOpen) IsNewProjectMenuOpen = false;
    }

    public void OpenCurrentProject() => ToggleProjectPicker();

    public void ToggleNewProjectMenu() => IsNewProjectMenuOpen = !IsNewProjectMenuOpen;

    public void SelectProject(ProjectGroupItem project) => SelectProjectFolder(project.RootPath);

    public bool RenameProject(ProjectGroupItem project, string name, out string error)
    {
        error = "";
        var displayName = CleanProjectDisplayName(name);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            error = L("请输入有效的项目名称。", "Enter a valid project name.");
            return false;
        }

        SetProjectDisplayName(project.RootPath, displayName);
        ReplaceProjectGroup(project, project with { Name = displayName });
        UpdateProjectPickerItems();
        if (PathsEqual(ProjectPath, project.RootPath))
        {
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(WorkspaceChipLabel));
            Projects.Clear();
            Projects.Add(new(ProjectName, ProjectPathShort, L("当前工作区", "Current workspace"), ProjectPath));
            if (!IsChatMode) PanelTitle = ProjectName;
        }
        StatusText = L($"已重命名项目 · {displayName}", $"renamed project · {displayName}");
        return true;
    }

    public bool CreatePermanentWorktree(ProjectGroupItem project, string name, out string error)
    {
        error = "";
        var worktreeName = Regex.Replace(SanitizeProjectName(name), @"\s+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(worktreeName))
        {
            error = L("请输入有效的工作树名称。", "Enter a valid worktree name.");
            return false;
        }

        var repoRoot = ResolveGitRoot(project.RootPath);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            error = L("当前项目不是 Git 仓库，无法创建工作树。", "This project is not a Git repository.");
            return false;
        }

        var branchCheck = RunGit(repoRoot, "check-ref-format", "--branch", worktreeName);
        if (branchCheck.ExitCode != 0)
        {
            error = L($"工作树名称不能作为 Git 分支名：{branchCheck.Error.Trim()}", $"Worktree name is not a valid Git branch name: {branchCheck.Error.Trim()}");
            return false;
        }

        var parent = Directory.GetParent(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            error = L("无法确定工作树创建位置。", "Unable to determine worktree location.");
            return false;
        }

        var target = Path.Combine(parent, worktreeName);
        if (Directory.Exists(target) || File.Exists(target))
        {
            error = L($"目标路径已存在：{target}", $"Target path already exists: {target}");
            return false;
        }

        var result = RunGit(repoRoot, "worktree", "add", "-b", worktreeName, target, "HEAD");
        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            error = L($"创建工作树失败：{message.Trim()}", $"Failed to create worktree: {message.Trim()}");
            return false;
        }

        SetProjectDisplayName(target, worktreeName);
        SelectProjectFolder(target);
        StatusText = L($"已创建永久工作树 · {worktreeName}", $"created permanent worktree · {worktreeName}");
        return true;
    }

    public void StartNewChatForProject(ProjectGroupItem project)
    {
        SelectProjectFolder(project.RootPath);
        StatusText = L($"新对话 · {project.Name}", $"new chat · {project.Name}");
    }

    public void PinProject(ProjectGroupItem project)
    {
        var index = ProjectGroups.IndexOf(project);
        if (index > 0) ProjectGroups.Move(index, 0);
        StatusText = L($"已置顶项目 · {project.Name}", $"pinned project · {project.Name}");
    }

    public void OpenProjectInExplorer(ProjectGroupItem project)
    {
        try
        {
            if (Directory.Exists(project.RootPath)) Process.Start(new ProcessStartInfo { FileName = project.RootPath, UseShellExecute = true });
            StatusText = L($"已打开项目 · {project.Name}", $"opened project · {project.Name}");
        }
        catch (Exception ex)
        {
            StatusText = L($"打开项目失败 · {ex.Message}", $"open project failed · {ex.Message}");
        }
    }

    public void ArchiveProject(ProjectGroupItem project)
    {
        if (ProjectGroups.Contains(project)) ProjectGroups.Remove(project);
        StatusText = L($"已从侧栏移除 · {project.Name}", $"removed from sidebar · {project.Name}");
    }

    private void ReplaceProjectGroup(ProjectGroupItem oldProject, ProjectGroupItem newProject)
    {
        var index = ProjectGroups.IndexOf(oldProject);
        if (index >= 0) ProjectGroups[index] = newProject;
    }

    private void SetProjectDisplayName(string path, string displayName)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        var folderName = DefaultProjectDisplayName(path);
        if (displayName.Equals(folderName, StringComparison.OrdinalIgnoreCase)) _projectDisplayNames.Remove(normalized);
        else _projectDisplayNames[normalized] = displayName;
        SaveProjectDisplayNames();
    }

    private string ProjectDisplayNameFor(string path)
    {
        var normalized = NormalizePath(path);
        if (!string.IsNullOrWhiteSpace(normalized) && _projectDisplayNames.TryGetValue(normalized, out var displayName) && !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }
        return DefaultProjectDisplayName(path);
    }

    private static string DefaultProjectDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "workspace" : name;
    }

    private static string CleanProjectDisplayName(string name)
    {
        var cleaned = Regex.Replace(name.Trim(), @"[\r\n\t]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Length > 80 ? cleaned[..80].Trim() : cleaned;
    }

    private string ProjectMetadataPath => Path.Combine(_pi.AgentDir, "ipi-projects.json");

    private void LoadProjectDisplayNames()
    {
        try
        {
            var path = ProjectMetadataPath;
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (data is null) return;
            foreach (var pair in data)
            {
                var key = NormalizePath(pair.Key);
                var value = CleanProjectDisplayName(pair.Value);
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) _projectDisplayNames[key] = value;
            }
        }
        catch
        {
            // Ignore malformed local UI metadata.
        }
    }

    private void SaveProjectDisplayNames()
    {
        try
        {
            Directory.CreateDirectory(_pi.AgentDir);
            var ordered = _projectDisplayNames
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProjectMetadataPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            StatusText = L($"保存项目名称失败 · {ex.Message}", $"failed to save project name · {ex.Message}");
        }
    }

    public void PinSession(SessionItem session)
    {
        if (string.IsNullOrWhiteSpace(session.FilePath)) return;
        MoveSessionToTop(Sessions, session.FilePath);
        foreach (var project in ProjectGroups) MoveSessionToTop(project.Sessions, session.FilePath);
        StatusText = $"pinned chat · {session.Title}";
    }

    public void ArchiveSession(SessionItem session)
    {
        if (string.IsNullOrWhiteSpace(session.FilePath)) return;
        var record = _sessions.FirstOrDefault(s => s.FilePath.Equals(session.FilePath, StringComparison.OrdinalIgnoreCase));
        if (record is not null) _archive.Archive(record);
        RemoveSessionFrom(Sessions, session.FilePath);
        foreach (var project in ProjectGroups) RemoveSessionFrom(project.Sessions, session.FilePath);
        StatusText = $"archived chat · {session.Title}";
    }

    public void ArchiveAllVisibleSessions()
    {
        var paths = Sessions.Concat(ProjectGroups.SelectMany(p => p.Sessions))
            .Select(s => s.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in paths)
        {
            var record = _sessions.FirstOrDefault(s => s.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (record is not null) _archive.Archive(record);
        }
        RefreshLocalData();
        StatusText = L($"已归档 {paths.Count} 个侧边栏聊天", $"archived {paths.Count} sidebar chat(s)");
    }

    public void OrganizeSidebarByProject()
    {
        LoadSidebarProjectGroups();
        IsProjectsExpanded = true;
        IsSessionsExpanded = false;
        StatusText = L("已按项目整理侧边栏", "organized sidebar by project");
    }

    public void ShowRecentProjectsFirst()
    {
        var ordered = ProjectGroups.OrderByDescending(project => project.Sessions
            .Select(s => _sessions.FirstOrDefault(record => record.FilePath == s.FilePath)?.Modified ?? DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max()).ToList();
        ProjectGroups.Clear();
        foreach (var project in ordered) ProjectGroups.Add(project);
        IsProjectsExpanded = true;
        StatusText = L("已按最近项目排序", "sorted by recent projects");
    }

    public void SortSidebarSessionsChronological()
    {
        ApplySidebarSessionSort(records => records.OrderBy(s => s.Modified));
        StatusText = L("已按时间顺序排序", "sorted chronologically");
    }

    public void SortSidebarSessionsRecent()
    {
        ApplySidebarSessionSort(records => records.OrderByDescending(s => s.Modified));
        StatusText = L("已按最近更新排序", "sorted by recent updates");
    }

    private void ApplySidebarSessionSort(Func<IEnumerable<PiSessionRecord>, IOrderedEnumerable<PiSessionRecord>> sort)
    {
        Sessions.Clear();
        foreach (var item in sort(_sessions).Select(ToSidebarSession)) Sessions.Add(item);

        foreach (var project in ProjectGroups)
        {
            var projectRecords = project.Sessions
                .Select(item => _sessions.FirstOrDefault(record => record.FilePath == item.FilePath))
                .Where(record => record is not null)!
                .Cast<PiSessionRecord>();
            project.Sessions.Clear();
            foreach (var item in sort(projectRecords).Select(ToSidebarSession)) project.Sessions.Add(item);
        }
    }

    public void RefreshAfterArchiveChange()
    {
        var status = StatusText;
        RefreshLocalData();
        StatusText = status;
        if (PanelTitle == L("对话", "Chats")) LoadSessionsPage();
    }

    private static void MoveSessionToTop(ObservableCollection<SessionItem> collection, string filePath)
    {
        var item = collection.FirstOrDefault(s => s.FilePath?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true);
        if (item is null) return;
        var index = collection.IndexOf(item);
        if (index > 0) collection.Move(index, 0);
    }

    private static void RemoveSessionFrom(ObservableCollection<SessionItem> collection, string filePath)
    {
        var item = collection.FirstOrDefault(s => s.FilePath?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true);
        if (item is not null) collection.Remove(item);
    }

    public void SelectProjectFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        ProjectPath = path;
        SaveLastWorkspacePath(path);
        _activeCwd = path;
        OnPropertyChanged(nameof(ActiveLocationPath));
        _activeSessionFile = null;
        IsProjectPickerOpen = false;
        IsBottomProjectPickerOpen = false;
        IsNewProjectMenuOpen = false;
        ProjectSearchText = "";
        Projects.Clear();
        Projects.Add(new(ProjectName, ProjectPathShort, L("当前工作区", "Current workspace"), ProjectPath));
        RefreshLocalData();
        LoadHome();
    }

    public bool CreateNamedProject(string name, out string error)
    {
        error = "";
        var projectName = SanitizeProjectName(name);
        if (string.IsNullOrWhiteSpace(projectName))
        {
            error = L("请输入有效的项目名称。", "Enter a valid project name.");
            return false;
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ipi-projects");
        Directory.CreateDirectory(root);
        var candidate = Path.Combine(root, projectName);
        if (Directory.Exists(candidate))
        {
            error = L($"项目已存在：{candidate}", $"Project already exists: {candidate}");
            return false;
        }

        try
        {
            Directory.CreateDirectory(candidate);
            SelectProjectFolder(candidate);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void CreateBlankProject()
    {
        if (CreateNamedProject("New project", out var error)) return;
        StatusText = error;
    }

    private static string SanitizeProjectName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        return cleaned.Trim().Trim('.');
    }

    public void ClearProject()
    {
        Directory.CreateDirectory(GlobalChatRoot);
        IsNewProjectMenuOpen = false;
        SelectProjectFolder(GlobalChatRoot);
    }

    private void AddUserRow(string text, string detail = "", IReadOnlyList<ChatAttachmentPreviewItem>? attachments = null, string? branchFromEntryId = null, bool addMarker = true)
    {
        var rowIndex = ContentRows.Count;
        var title = TrimForRow(text, 8000);
        ContentRows.Add(new("user", title, detail, branchFromEntryId, "message", attachments));
        if (!addMarker) return;
        var markerTitle = title.Replace("\r", " ").Replace("\n", " ").Trim();
        if (!string.IsNullOrWhiteSpace(markerTitle))
        {
            ChatMarkers.Add(new(ChatMarkers.Count + 1, TrimForRow(markerTitle, 220), rowIndex, IsEnglish));
            RefreshChatMarkerDensity();
        }
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Attachments.Any(a => PathsEqual(a.Path, path))) continue;
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            Attachments.Add(new AttachmentItem(Path.GetFileName(path), path, FormatSize(info.Length)));
            added++;
        }
        StatusText = added == 0 ? "no new files attached" : $"attached {added} file(s)";
    }

    public void RemoveAttachment(AttachmentItem item)
    {
        Attachments.Remove(item);
        StatusText = $"removed attachment · {item.Name}";
    }

    public async Task ToggleVoiceInputAsync()
    {
        if (_isVoiceBusy) return;

        if (VoiceBackendKey == "windows-dictation")
        {
            if (IsVoiceRecording) IsVoiceRecording = false;
            RequestWindowsDictationToggle?.Invoke();
            StatusText = "Windows Dictation opened · use its own mic/close controls";
            return;
        }

        if (VoiceBackendKey == "openai-transcribe")
        {
            StatusText = "OpenAI Transcribe needs API setup before app-controlled recording is available";
            return;
        }

        if (VoiceBackendKey == "local-whisper")
        {
            StatusText = "Local Whisper needs model/runtime setup before recording is available";
            return;
        }

        _isVoiceBusy = true;
        try
        {
            if (IsVoiceRecording)
            {
                await StopVoiceInputAsync();
                return;
            }

            IsVoiceRecording = true;
            StatusText = "recording with Windows Native · click stop to stop";
            await _speech.StartAsync();
        }
        catch (Exception ex)
        {
            IsVoiceRecording = false;
            StatusText = $"Windows Native failed · {ex.HResult:X8} · choose Windows Dictation from ▾";
        }
        finally
        {
            _isVoiceBusy = false;
        }
    }

    private async Task StopVoiceInputAsync()
    {
        try
        {
            StatusText = "transcribing voice";
            var text = await _speech.StopAsync();
            IsVoiceRecording = false;
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = "voice stopped · no speech recognized";
                return;
            }
            PromptText = string.IsNullOrWhiteSpace(PromptText) ? text : $"{PromptText.TrimEnd()}\n{text}";
            StatusText = "voice transcribed into composer";
        }
        catch (Exception ex)
        {
            IsVoiceRecording = false;
            StatusText = $"voice transcription failed · {ex.HResult:X8} · {ex.Message}";
        }
    }

    public void ToggleVoicePicker()
    {
        if (IsChatMode)
        {
            IsMainVoicePickerOpen = false;
            IsChatVoicePickerOpen = !IsChatVoicePickerOpen;
        }
        else
        {
            IsChatVoicePickerOpen = false;
            IsMainVoicePickerOpen = !IsMainVoicePickerOpen;
        }
    }

    public void SelectVoiceOption(VoiceOptionItem option)
    {
        IsMainVoicePickerOpen = false;
        IsChatVoicePickerOpen = false;
        if (!option.IsAvailable)
        {
            StatusText = $"{option.Title} is not configured yet · {option.Detail}";
            return;
        }
        if (IsVoiceRecording)
        {
            StatusText = "stop the current app-controlled recording before switching voice backend";
            return;
        }
        VoiceBackendKey = option.Key;
        StatusText = $"voice backend: {option.Title}";
    }

    private void RefreshChatMarkerDensity()
    {
        var count = ChatMarkers.Count;
        if (count == 0) return;

        const double maxSlotHeight = 42;
        const double minSlotHeight = 12.5;
        var slotHeight = Math.Clamp(maxSlotHeight - Math.Log2(Math.Max(1, count)) * 6.2, minSlotHeight, maxSlotHeight);
        foreach (var marker in ChatMarkers)
        {
            marker.SlotHeight = slotHeight;
        }
    }

    public void SetChatMarkerHover(ChatMarkerItem? hovered)
    {
        var center = hovered is null ? -1 : ChatMarkers.IndexOf(hovered);
        for (var i = 0; i < ChatMarkers.Count; i++)
        {
            var marker = ChatMarkers[i];
            var distance = center < 0 ? 99 : Math.Abs(i - center);
            marker.VisualWidth = distance switch
            {
                0 => 34,
                1 => 25,
                2 => 18,
                3 => 13,
                _ => 10,
            };
            marker.VisualOpacity = distance switch
            {
                0 => 0.88,
                1 => 0.68,
                2 => 0.54,
                3 => 0.44,
                _ => 0.38,
            };
            marker.VisualBrush = distance == 0
                ? new SolidColorBrush(Color.FromRgb(215, 218, 223))
                : new SolidColorBrush(Color.FromRgb(112, 116, 123));
        }
    }

    public void ToggleApprovalPicker(bool chatComposer)
    {
        UpdateApprovalOptions();
        IsBottomApprovalPickerOpen = false;
        if (chatComposer)
        {
            IsMainApprovalPickerOpen = false;
            IsChatApprovalPickerOpen = !IsChatApprovalPickerOpen;
        }
        else
        {
            IsChatApprovalPickerOpen = false;
            IsMainApprovalPickerOpen = !IsMainApprovalPickerOpen;
        }
    }

    public void ToggleBottomApprovalPicker()
    {
        UpdateApprovalOptions();
        IsMainApprovalPickerOpen = false;
        IsChatApprovalPickerOpen = false;
        IsBottomApprovalPickerOpen = !IsBottomApprovalPickerOpen;
    }

    public void SelectApprovalOption(ApprovalOptionItem option)
    {
        if (!option.IsEnabled)
        {
            StatusText = option.Detail;
            return;
        }
        var index = Enumerable.Range(0, _approvalModeKeys.Length).FirstOrDefault(i => ApprovalLabelFor(i).Equals(option.Title, StringComparison.OrdinalIgnoreCase), -1);
        if (index < 0) return;
        if (_approvalModeKeys[index] == "custom" && ReadCustomApprovalModeFromConfig().ModeKey is null)
        {
            StatusText = L("请选择要创建的自定义审批策略", "choose a custom approval policy to create");
            return;
        }
        _approvalModeIndex = index;
        SaveUiSettings();
        OnPropertyChanged(nameof(ApprovalMode));
        OnPropertyChanged(nameof(ApprovalLabel));
        UpdateApprovalOptions();
        IsMainApprovalPickerOpen = false;
        IsChatApprovalPickerOpen = false;
        IsBottomApprovalPickerOpen = false;
        StatusText = $"approval policy: {ApprovalMode}";
        if (PanelTitle == L("设置", "Settings")) LoadSettingsPage();
    }

    public void CycleApprovalMode()
    {
        var hasCustomConfig = ReadCustomApprovalModeFromConfig().ModeKey is not null;
        do
        {
            _approvalModeIndex = (_approvalModeIndex + 1) % _approvalModeKeys.Length;
        } while (_approvalModeKeys[_approvalModeIndex] == "custom" && !hasCustomConfig);
        SaveUiSettings();
        OnPropertyChanged(nameof(ApprovalMode));
        OnPropertyChanged(nameof(ApprovalLabel));
        UpdateApprovalOptions();
        StatusText = $"approval policy: {ApprovalMode}";
        if (PanelTitle == L("设置", "Settings")) LoadSettingsPage();
    }

    public void CycleToolPreset()
    {
        _toolPresetIndex = (_toolPresetIndex + 1) % _toolPresets.Length;
        SaveUiSettings();
        OnPropertyChanged(nameof(ToolLabel));
        StatusText = $"tool preset: {CurrentToolPreset.Label}";
        if (PanelTitle == L("工具", "Tools")) LoadToolsPage();
    }

    private void SetToolPreset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var index = Array.FindIndex(_toolPresets, p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _toolPresetIndex = index;
        SaveUiSettings();
        OnPropertyChanged(nameof(ToolLabel));
        StatusText = $"tool preset: {CurrentToolPreset.Label}";
        LoadToolsPage();
    }

    public void SetAppearanceMode(string mode)
    {
        var normalized = mode is "dark" or "system" ? mode : "light";
        if (_appearanceMode == normalized) return;
        _appearanceMode = normalized;
        OnPropertyChanged(nameof(AppearanceIcon));
    }

    public void ToggleThinkingPicker(bool chatComposer)
    {
        UpdateReasoningOptions();
        if (chatComposer)
        {
            IsMainThinkingPickerOpen = false;
            IsChatThinkingPickerOpen = !IsChatThinkingPickerOpen;
        }
        else
        {
            IsChatThinkingPickerOpen = false;
            IsMainThinkingPickerOpen = !IsMainThinkingPickerOpen;
        }
    }

    public void SelectReasoningOption(ReasoningOptionItem option)
    {
        ThinkingLevel = option.Key;
        UpdateModelLabels();
        UpdateReasoningOptions();
        RebuildModelOptions();
        IsMainThinkingPickerOpen = false;
        IsChatThinkingPickerOpen = false;
        StatusText = $"thinking: {ThinkingLevel}";
    }

    public void SelectModelOption(ModelOptionItem option)
    {
        _activeProvider = option.Provider;
        _activeModel = option.Model;
        _contextLimitTokens = _pi.ReadContextWindow(_activeProvider, _activeModel);
        UpdateModelLabels();
        RebuildModelOptions();
        SetTopUsage(_currentUsage);
        IsMainThinkingPickerOpen = false;
        IsChatThinkingPickerOpen = false;
        StatusText = $"model: {_activeProvider}/{_activeModel}";
    }

    public void OpenModelPickerFromComposer()
    {
        RebuildModelOptions();
    }

    public void CycleThinkingMode()
    {
        var options = new[] { "low", "medium", "high", "xhigh" };
        var index = Array.IndexOf(options, ThinkingLevel);
        SelectReasoningOption(new ReasoningOptionItem(options[(index + 1 + options.Length) % options.Length], "", ""));
    }

    private ToolRunConfig BuildToolRunConfig()
    {
        return new ToolRunConfig(CurrentToolPreset.Tools, CurrentToolPreset.NoTools, CurrentToolPreset.Description);
    }

    private string ApprovalModeKey()
    {
        if (_approvalModeKeys[_approvalModeIndex] == "custom")
        {
            return ApprovalRulesForBridge() is not null ? "custom" : ReadCustomApprovalModeFromConfig().ModeKey ?? "default";
        }

        return _approvalModeKeys[_approvalModeIndex] switch
        {
            "full" => "auto",
            "auto" => "on-risk",
            _ => "default"
        };
    }

    private IReadOnlyDictionary<string, string>? ApprovalRulesForBridge()
    {
        if (_approvalModeKeys[_approvalModeIndex] != "custom") return null;
        return ReadCustomApprovalRulesFromConfig().Rules;
    }

    private void UpdateApprovalOptions()
    {
        var customConfig = ReadCustomApprovalModeFromConfig();
        if (_approvalModeKeys[_approvalModeIndex] == "custom" && customConfig.ModeKey is null)
        {
            _approvalModeIndex = 0;
            OnPropertyChanged(nameof(ApprovalMode));
            OnPropertyChanged(nameof(ApprovalLabel));
        }

        ApprovalOptions.Clear();
        ApprovalOptions.Add(new("shield-check", ApprovalLabelFor(0), L("编辑、Shell、工作区外读取时询问", "Ask before edits, shell, and outside-workspace reads"), _approvalModeIndex == 0 ? "✓" : ""));
        ApprovalOptions.Add(new("sparkles", ApprovalLabelFor(1), L("Shell 总是询问；工作区内编辑自动允许", "Ask for shell; auto-allow in-workspace edits"), _approvalModeIndex == 1 ? "✓" : ""));
        ApprovalOptions.Add(new("globe", ApprovalLabelFor(2), L("不弹出工具审批；仍受工具/系统限制", "No tool approval prompts; still limited by tools/system"), _approvalModeIndex == 2 ? "✓" : ""));
        ApprovalOptions.Add(new("settings", ApprovalLabelFor(3), customConfig.ModeKey is null
            ? L("点击选择规则并创建 .ipi/config.toml", "Click to choose a policy and create .ipi/config.toml")
            : L($"读取 {ShortenPath(customConfig.Path!)} · {customConfig.Display}", $"Read {ShortenPath(customConfig.Path!)} · {customConfig.Display}"), _approvalModeIndex == 3 ? "✓" : "", true, customConfig.ModeKey is null));
    }

    public (bool Success, string Message) CreateWorkspaceApprovalConfigTemplate(IReadOnlyDictionary<string, string> rules)
    {
        try
        {
            var path = Path.Combine(ResolveRunCwd(), ".ipi", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var template = BuildApprovalConfigTemplate(rules);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, template);
                SelectCustomApprovalFromCreatedConfig(path);
                return (true, L($"已创建 {ShortenPath(path)}", $"created {ShortenPath(path)}"));
            }

            var existing = File.ReadAllText(path);
            if (!ContainsApprovalRules(existing) && !existing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(line => IsApprovalConfigKey(line.Split('#')[0].Split('=', 2)[0].Trim().Trim('"', '\''))))
            {
                File.AppendAllText(path, "\n" + template);
                SelectCustomApprovalFromCreatedConfig(path);
                return (true, L($"已更新 {ShortenPath(path)}", $"updated {ShortenPath(path)}"));
            }

            return (false, L($"{ShortenPath(path)} 已有审批配置；请直接编辑该文件。", $"{ShortenPath(path)} already has approval config; edit the file directly."));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string BuildApprovalConfigTemplate(IReadOnlyDictionary<string, string> rules)
    {
        string Value(string key) => NormalizeApprovalRuleValue(rules.TryGetValue(key, out var value) ? value : "ask");
        return "# ipi workspace approval rules\n" +
               "# Values per tool: ask | allow\n" +
               "[approval]\n" +
               $"bash = \"{Value("bash")}\"\n" +
               $"edit = \"{Value("edit")}\"\n" +
               $"write = \"{Value("write")}\"\n" +
               $"read_outside_workspace = \"{Value("read_outside_workspace")}\"\n";
    }

    private void SelectCustomApprovalFromCreatedConfig(string path)
    {
        _approvalModeIndex = 3;
        SaveUiSettings();
        OnPropertyChanged(nameof(ApprovalMode));
        OnPropertyChanged(nameof(ApprovalLabel));
        UpdateApprovalOptions();
        IsMainApprovalPickerOpen = false;
        IsChatApprovalPickerOpen = false;
        IsBottomApprovalPickerOpen = false;
        StatusText = L($"自定义审批策略已启用 · {ShortenPath(path)}", $"custom approval policy enabled · {ShortenPath(path)}");
    }

    private static bool IsApprovalConfigKey(string key)
    {
        return key.Equals("approval_mode", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("approvalMode", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("approval_policy", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("approval-policy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovalRuleKey(string key)
    {
        return key.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("write", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("read_outside_workspace", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("readOutsideWorkspace", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeApprovalRuleValue(string value)
    {
        return value.Equals("allow", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("never", StringComparison.OrdinalIgnoreCase)
            ? "allow"
            : "ask";
    }

    private static bool ContainsApprovalRules(string text)
    {
        return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)[0].Trim().Trim('"', '\''))
            .Any(IsApprovalRuleKey);
    }

    private (string? Path, string? ModeKey, string Display) ReadCustomApprovalModeFromConfig()
    {
        var granular = ReadCustomApprovalRulesFromConfig();
        if (granular.Rules is not null)
        {
            var display = string.Join(" · ", granular.Rules.Select(kv => $"{kv.Key}={kv.Value}"));
            return (granular.Path, "custom-rules", display);
        }
        foreach (var path in ApprovalConfigCandidates())
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var raw in File.ReadLines(path).Take(220))
                {
                    var line = raw.Split('#')[0].Trim();
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains('=')) continue;
                    var parts = line.Split('=', 2);
                    var key = parts[0].Trim().Trim('"', '\'');
                    if (!IsApprovalConfigKey(key)) continue;
                    var value = parts[1].Trim().Trim('"', '\'').ToLowerInvariant();
                    var mapped = value switch
                    {
                        "default" or "ask" or "on-request" or "on_request" => "default",
                        "on-risk" or "on_risk" or "risk" or "on-failure" or "on_failure" => "on-risk",
                        "auto" or "full" or "never" => "auto",
                        "read-only" or "read_only" => "read-only",
                        _ => null,
                    };
                    if (mapped is not null) return (path, mapped, $"{key}={value} → {mapped}");
                }
            }
            catch { }
        }
        return (null, null, "");
    }

    private (string? Path, IReadOnlyDictionary<string, string>? Rules) ReadCustomApprovalRulesFromConfig()
    {
        foreach (var path in ApprovalConfigCandidates())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var inApprovalSection = false;
                foreach (var raw in File.ReadLines(path).Take(260))
                {
                    var line = raw.Split('#')[0].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inApprovalSection = line.Equals("[approval]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!line.Contains('=')) continue;
                    var parts = line.Split('=', 2);
                    var key = parts[0].Trim().Trim('"', '\'');
                    if (!inApprovalSection && !IsApprovalRuleKey(key)) continue;
                    if (!IsApprovalRuleKey(key)) continue;
                    var normalizedKey = key.Equals("readOutsideWorkspace", StringComparison.OrdinalIgnoreCase) ? "read_outside_workspace" : key.ToLowerInvariant();
                    var value = parts[1].Trim().Trim('"', '\'');
                    rules[normalizedKey] = NormalizeApprovalRuleValue(value);
                }
                if (rules.Count > 0) return (path, rules);
            }
            catch { }
        }
        return (null, null);
    }

    private IEnumerable<string> ApprovalConfigCandidates()
    {
        var cwd = ResolveRunCwd();
        yield return Path.Combine(cwd, ".ipi", "config.toml");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "config.toml");
        yield return Path.Combine(_pi.AgentDir, "config.toml");
    }

    private void UpdateReasoningOptions()
    {
        ReasoningOptions.Clear();
        ReasoningOptions.Add(new("low", L("低", "Low"), ThinkingLevel == "low" ? "✓" : ""));
        ReasoningOptions.Add(new("medium", L("中", "Medium"), ThinkingLevel == "medium" ? "✓" : ""));
        ReasoningOptions.Add(new("high", L("高", "High"), ThinkingLevel == "high" ? "✓" : ""));
        ReasoningOptions.Add(new("xhigh", L("超高", "Ultra"), ThinkingLevel == "xhigh" ? "✓" : ""));
    }

    private Task<PiToolApprovalDecision> RequestToolApprovalAsync(PiToolApprovalRequest request)
    {
        if (_runAllowedTools.Contains(request.ToolName)) return Task.FromResult(new PiToolApprovalDecision(true));

        var tcs = new TaskCompletionSource<PiToolApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Current.Dispatcher.Invoke(() =>
        {
            var item = new ToolApprovalRequestItem(
                request.ApprovalId,
                request.ToolName,
                BuildToolIntent(request.ToolName, request.Summary),
                TrimForRow(string.IsNullOrWhiteSpace(request.Summary) ? request.Detail : request.Summary, 260),
                request.Detail,
                L("工具请求", "Tool request"),
                L("允许", "Allow"),
                L("始终允许", "Always allow"),
                L("拒绝", "Deny"),
                L("可选：告诉 agent 换一种做法…", "Optional: tell the agent what to do instead…"),
                tcs);
            ToolApprovalRequests.Add(item);
            StatusText = $"tool approval required · {request.ToolName}";
        });
        return tcs.Task;
    }

    private string BuildToolIntent(string toolName, string summary)
    {
        var preview = TrimForRow(summary, 120);
        return toolName.ToLowerInvariant() switch
        {
            "bash" => L("运行一条 Shell 命令", "Run a shell command"),
            "read" => L("读取一个本地文件", "Read a local file"),
            "write" => L("写入或创建一个本地文件", "Write or create a local file"),
            "edit" => L("修改一个本地文件", "Edit a local file"),
            _ => L($"执行工具：{toolName}", $"Run tool: {toolName}"),
        } + (string.IsNullOrWhiteSpace(preview) ? "" : $" · {preview}");
    }

    public void ResolveToolApproval(ToolApprovalRequestItem item, ToolApprovalDecisionKind decision)
    {
        if (ToolApprovalRequests.Contains(item)) ToolApprovalRequests.Remove(item);
        if (decision == ToolApprovalDecisionKind.AlwaysAllow) _runAllowedTools.Add(item.ToolName);
        var approved = decision is ToolApprovalDecisionKind.Allow or ToolApprovalDecisionKind.AlwaysAllow;
        var reason = approved ? "" : item.Guidance.Trim();
        item.Decision.TrySetResult(new PiToolApprovalDecision(approved, reason));
        StatusText = approved ? $"approved tool · {item.ToolName}" : $"denied tool · {item.ToolName}";
    }

    private string BuildPromptWithAttachments(string text)
    {
        if (Attachments.Count == 0) return text;
        var sb = new StringBuilder(text.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Attached local files:");
        foreach (var item in Attachments)
        {
            sb.AppendLine($"- {item.Path}");
            if (IsSensitiveAttachment(item.Path))
            {
                sb.AppendLine("[content omitted: sensitive-looking file; path only]");
                continue;
            }
            if (AttachmentImageHelper.IsImageFile(item.Path)) continue;
            var preview = _pi.ReadTextPreview(item.Path, 48 * 1024);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                var trimmed = TrimForRow(preview, 6000);
                sb.AppendLine("```text");
                sb.AppendLine(trimmed);
                sb.AppendLine("```");
            }
        }
        return sb.ToString();
    }

    private static bool IsSensitiveAttachment(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (name.StartsWith(".env")) return true;
        if (name.Contains("mnemonic") || name.Contains("seed") || name.Contains("wallet")) return true;
        if (name is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519") return true;
        return ext is ".pem" or ".key" or ".p12" or ".pfx";
    }

    private void LoadHome()
    {
        CancelSessionLoad();
        SetSystemContextMode(false);
        PanelTitle = L("我们该做什么？", "What should we do?");
        PanelSubtitle = IsGlobalChat ? L("默认聊天", "Default chat") : $"{L("项目", "Project")} · {ProjectPathShort}";
        _activeSessionFile = null;
        _activeCwd = ProjectPath;
        IsChatMode = false;
        IsStartActionsVisible = true;
        CanReturnToChat = false;
        IsComposerVisible = true;
        IsToolbarVisible = false;
        IsInspectorVisible = false;
        IsProjectsExpanded = false;
        IsSessionsExpanded = false;
        ContentRows.Clear();
        ChatMarkers.Clear();
        Suggestions.Clear();

        // New chat stays clean: no prompt suggestions below the composer.
    }

    private void LoadSearchPage()
    {
        PanelTitle = L("搜索", "Search");
        PanelSubtitle = L("输入关键词搜索 sessions、files、skills。", "Search sessions, files, and skills.");
        IsChatMode = false;
        IsStartActionsVisible = false;
        CanReturnToChat = true;
        IsComposerVisible = true;
        IsToolbarVisible = false;
        IsInspectorVisible = false;
        ContentRows.Clear();
        Suggestions.Clear();
        ContentRows.Add(new("hint", L("搜索范围", "Search scope"), $"{_sessions.Count} sessions · {_files.Count} files · {_skills.Count} skills"));
        LoadInspector("Search", new[]
        {
            new PanelRow("query", L("在 composer 输入关键词并回车", "Type a query in the composer and press Enter")),
            new PanelRow("slash", "/sessions /files /skills /model /tools")
        });
    }

    private void RunSearch(string query)
    {
        PanelTitle = $"{L("搜索", "Search")}: {query}";
        PanelSubtitle = L("本地结果", "Local results");
        ContentRows.Clear();
        var q = query.ToLowerInvariant();
        foreach (var session in _sessions.Where(s => s.Title.ToLowerInvariant().Contains(q) || s.Cwd.ToLowerInvariant().Contains(q) || s.FirstMessage.ToLowerInvariant().Contains(q)).Take(30))
        {
            ContentRows.Add(new("session", session.Title, ShortenPath(session.Cwd), session.FilePath, "session"));
        }
        foreach (var file in _files.Where(f => f.RelativePath.ToLowerInvariant().Contains(q)).Take(40))
        {
            ContentRows.Add(new(file.IsDirectory ? "dir" : "file", file.RelativePath, file.IsDirectory ? "directory" : FormatSize(file.Size), file.Path, file.IsDirectory ? "directory" : "file"));
        }
        foreach (var skill in _skills.Where(s => s.Name.ToLowerInvariant().Contains(q) || s.Description.ToLowerInvariant().Contains(q)).Take(30))
        {
            ContentRows.Add(new("skill", $"{skill.Source}: {skill.Name}", skill.Description, skill.Path, "skill"));
        }
        if (ContentRows.Count == 0) ContentRows.Add(new("empty", "No results", query));
        StatusText = $"search results: {ContentRows.Count}";
    }

    private void LoadRunningPage()
    {
        PanelTitle = L("已安排", "Scheduled");
        PanelSubtitle = L("待处理、运行中和近期完成的本地 Agent 任务。", "Pending, running, and recently completed local agent tasks.");
        IsChatMode = false;
        IsStartActionsVisible = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsInspectorVisible = false;
        ContentRows.Clear();
        Suggestions.Clear();
        ContentRows.Add(new(L("运行中", "running"), IsAgentRunning ? L("Agent 正在执行", "Agent is running") : L("没有正在运行的任务", "No active run"), IsAgentRunning ? L("等待事件返回", "Waiting for events") : L("发送 prompt 后会出现在这里", "Runs appear here after you send a prompt")));
        ContentRows.Add(new(L("近期", "recent"), L("最近本地会话", "Recent local sessions"), $"{_sessions.Count} sessions available"));
        foreach (var session in _sessions.Take(8)) ContentRows.Add(new("session", session.Title, $"{session.Modified:g} · {session.MessageCount} messages", session.FilePath, "session"));
        LoadRunInspector("no active run");
    }

    private void LoadFilesPage(string root)
    {
        PanelTitle = ProjectName;
        PanelSubtitle = root;
        IsChatMode = false;
        IsStartActionsVisible = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsProjectsExpanded = true;
        ContentRows.Clear();
        Suggestions.Clear();
        foreach (var file in _files.Take(180))
        {
            ContentRows.Add(new(file.IsDirectory ? "dir" : "file", file.RelativePath, file.IsDirectory ? "directory" : FormatSize(file.Size), file.Path, file.IsDirectory ? "directory" : "file"));
        }
        LoadInspector("File explorer", new[]
        {
            new PanelRow("root", root),
            new PanelRow("indexed", _files.Count.ToString()),
            new PanelRow("preview", L("点击 file 行读取文本预览", "Click a file row to preview text"))
        });
    }

    private void OpenFile(string? path, string title)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        PanelTitle = title;
        PanelSubtitle = path;
        IsChatMode = false;
        CanReturnToChat = true;
        IsToolbarVisible = false;
        ContentRows.Clear();
        Suggestions.Clear();
        var preview = _pi.ReadTextPreview(path);
        ContentRows.Add(new("preview", preview.Length > 12000 ? preview[..12000] + "…" : preview, "", path, "file"));
        LoadInspector("File", new[]
        {
            new PanelRow("path", path),
            new PanelRow("kind", Path.GetExtension(path)),
            new PanelRow("size", File.Exists(path) ? FormatSize(new FileInfo(path).Length) : "missing")
        });
    }

    private void OpenSession(string? filePath, string fallbackTitle)
    {
        CancelSessionLoad();
        var loadVersion = ++_sessionLoadVersion;
        var cts = new CancellationTokenSource();
        _sessionLoadCancellation = cts;
        _ = OpenSessionAsync(filePath, fallbackTitle, loadVersion, cts.Token);
    }

    private async Task OpenSessionAsync(string? filePath, string fallbackTitle, int loadVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            if (loadVersion != _sessionLoadVersion) return;
            LoadHome();
            return;
        }

        var record = _sessions.FirstOrDefault(s => s.FilePath == filePath);
        var displayTitle = record?.Title ?? fallbackTitle;
        var displaySubtitle = record is not null && IsGlobalCwd(record.Cwd) ? L("默认聊天", "Default chat") : record?.Cwd ?? filePath;
        var activeCwd = record is not null && IsGlobalCwd(record.Cwd) ? GlobalChatRoot : record?.Cwd ?? ProjectPath;
        var modified = record?.Modified ?? File.GetLastWriteTime(filePath);
        var knownMessageCount = record?.MessageCount ?? 0;

        PanelTitle = displayTitle;
        PanelSubtitle = displaySubtitle;
        _activeSessionFile = filePath;
        _activeCwd = activeCwd;
        OnPropertyChanged(nameof(ActiveLocationPath));
        IsChatMode = true;
        IsStartActionsVisible = false;
        CanReturnToChat = false;
        IsComposerVisible = true;
        IsToolbarVisible = true;
        IsSessionsExpanded = true;
        IsInspectorVisible = false;
        SessionStatsText = L("正在加载", "Loading");
        StatusText = L("正在打开对话…", "opening chat…");
        ContentRows.Clear();
        ChatMarkers.Clear();
        Suggestions.Clear();
        ContentRows.Add(new("system", L("正在加载对话…", "Loading chat…"), ShortenPath(filePath), null, "system"));

        try
        {
            var useMarkerOnly = new FileInfo(filePath).Length > 20 * 1024 * 1024 || knownMessageCount > 1500;
            if (useMarkerOnly)
            {
                var markers = await Task.Run(() => _pi.ReadSessionUserMarkers(filePath), cancellationToken);
                if (cancellationToken.IsCancellationRequested || loadVersion != _sessionLoadVersion) return;
                _activeSessionMarkerOnly = true;
                ContentRows.Clear();
                ChatMarkers.Clear();
                foreach (var marker in markers)
                {
                    var title = marker.Title.Replace("\r", " ").Replace("\n", " ").Trim();
                    ChatMarkers.Add(new(ChatMarkers.Count + 1, TrimForRow(title, 220), marker.TimelineIndex, IsEnglish));
                }
                RefreshChatMarkerDensity();
                var usageOnly = _pi.ReadSessionUsageSummary(filePath);
                SessionStatsText = $"{knownMessageCount} msgs · {modified:g}";
                SetTopUsage(usageOnly);
                StatusText = L($"已加载 {markers.Count} 个对话节点", $"loaded {markers.Count} chat markers");
                if (markers.Count > 0)
                {
                    await LoadSessionWindowForMarkerAsync(filePath, markers[^1].TimelineIndex, loadVersion, cancellationToken, before: 700, after: 80, scrollToEnd: true);
                }
                else
                {
                    ContentRows.Add(new("system", L("已加载对话节点", "Loaded chat markers"), L("这是大对话：正文按需加载。点击右侧用户节点查看附近上下文。", "Large chat: messages load on demand. Click a user marker on the right to load nearby context."), filePath, "system"));
                }
                return;
            }

            _activeSessionMarkerOnly = false;
            var snapshot = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeline = _pi.ReadSessionTimeline(filePath, take: 0, includeToolResults: true);
                cancellationToken.ThrowIfCancellationRequested();
                var usage = _pi.ReadSessionUsageSummary(filePath);
                cancellationToken.ThrowIfCancellationRequested();
                var messageCount = knownMessageCount > 0 ? knownMessageCount : timeline.Count(item => item.Kind == "message");
                return new SessionLoadSnapshot(timeline, usage, messageCount, modified);
            }, cancellationToken);

            if (cancellationToken.IsCancellationRequested || loadVersion != _sessionLoadVersion) return;

            ContentRows.Clear();
            ChatMarkers.Clear();
            await AddSessionRowsInBatchesAsync(snapshot.Timeline, loadVersion, cancellationToken);
            if (cancellationToken.IsCancellationRequested || loadVersion != _sessionLoadVersion) return;

            SessionStatsText = $"{snapshot.MessageCount} msgs · {snapshot.Modified:g}";
            SetTopUsage(snapshot.Usage);
            StatusText = $"opened session · {snapshot.MessageCount} messages";
            RequestScrollChatToLatest?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // A newer session/page was selected; leave the newer UI alone.
        }
        catch (Exception ex)
        {
            if (loadVersion != _sessionLoadVersion) return;
            ContentRows.Clear();
            ContentRows.Add(new("error", "Failed to open session", ex.Message, filePath, "error"));
            SessionStatsText = "error";
            StatusText = "session load failed";
        }
    }

    private async Task RefreshActiveSessionTimelineAsync(string sessionFile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionFile) || !File.Exists(sessionFile)) return;
        if (_activeSessionMarkerOnly)
        {
            var markers = await Task.Run(() => _pi.ReadSessionUserMarkers(sessionFile), cancellationToken);
            ChatMarkers.Clear();
            foreach (var marker in markers)
            {
                var title = marker.Title.Replace("\r", " ").Replace("\n", " ").Trim();
                ChatMarkers.Add(new(ChatMarkers.Count + 1, TrimForRow(title, 220), marker.TimelineIndex, IsEnglish));
            }
            RefreshChatMarkerDensity();
            if (markers.Count > 0) await LoadSessionWindowForMarkerAsync(sessionFile, markers[^1].TimelineIndex, _sessionLoadVersion, cancellationToken, before: 700, after: 80, scrollToEnd: true);
            return;
        }

        var timeline = await Task.Run(() => _pi.ReadSessionTimeline(sessionFile, take: 0, includeToolResults: true), cancellationToken);
        ContentRows.Clear();
        ChatMarkers.Clear();
        await AddSessionRowsInBatchesAsync(timeline, _sessionLoadVersion, cancellationToken);
        RequestScrollChatToLatest?.Invoke();
    }

    public bool LoadSessionWindowForMarker(int timelineIndex)
    {
        if (!_activeSessionMarkerOnly || string.IsNullOrWhiteSpace(_activeSessionFile) || !File.Exists(_activeSessionFile)) return false;
        var loadVersion = _sessionLoadVersion;
        var token = _sessionLoadCancellation?.Token ?? CancellationToken.None;
        _ = LoadSessionWindowForMarkerAsync(_activeSessionFile, timelineIndex, loadVersion, token, before: 300, after: 450);
        return true;
    }

    private async Task LoadSessionWindowForMarkerAsync(string sessionFile, int timelineIndex, int loadVersion, CancellationToken cancellationToken, int before = 300, int after = 450, bool scrollToEnd = false)
    {
        try
        {
            StatusText = L("正在加载节点附近对话…", "loading nearby messages…");
            var timeline = await Task.Run(() => _pi.ReadSessionTimelineWindow(sessionFile, timelineIndex, before: before, after: after, includeToolResults: true), cancellationToken);
            if (cancellationToken.IsCancellationRequested || loadVersion != _sessionLoadVersion) return;
            ContentRows.Clear();
            await AddSessionRowsInBatchesAsync(timeline, loadVersion, cancellationToken, rebuildMarkers: false);
            StatusText = L("已加载节点附近对话", "loaded nearby messages");
            if (scrollToEnd) RequestScrollChatToLatest?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (loadVersion != _sessionLoadVersion) return;
            ContentRows.Clear();
            ContentRows.Add(new("error", "Failed to load chat window", ex.Message, sessionFile, "error"));
        }
    }

    private async Task AddSessionRowsInBatchesAsync(IReadOnlyList<PiTimelineRecord> timeline, int loadVersion, CancellationToken cancellationToken, bool rebuildMarkers = true)
    {
        const int batchSize = 28;
        var added = 0;
        foreach (var item in timeline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (loadVersion != _sessionLoadVersion) return;

            if (item.Kind == "thinking" || item.Badge == "thinking") continue;
            if (item.Badge == "user")
            {
                var attachments = item.Attachments?.Select(a => new ChatAttachmentPreviewItem(a.Name, a.Path, a.Detail)).ToList();
                AddUserRow(item.Title, TrimForRow(item.Detail, 2000), attachments, item.ParentId ?? string.Empty, addMarker: rebuildMarkers);
            }
            else ContentRows.Add(new(item.Badge, TrimForRow(item.Title, 6000), TrimForRow(item.Detail, 6000), null, item.Kind));
            added++;

            if (added % batchSize == 0)
            {
                SessionStatsText = $"{added} rows";
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private void CancelSessionLoad()
    {
        _sessionLoadVersion++;
        _sessionLoadCancellation?.Cancel();
        _sessionLoadCancellation = null;
    }

    private void LoadBranchesPage()
    {
        RefreshBranches();
        PanelTitle = BranchesText;
        PanelSubtitle = string.IsNullOrWhiteSpace(_branchRepoRoot) ? BranchStatusText : _branchRepoRoot;
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = true;
        ContentRows.Clear();
        Suggestions.Clear();

        if (string.IsNullOrWhiteSpace(_branchRepoRoot))
        {
            ContentRows.Add(new("git", BranchStatusText, ActiveLocationPath));
            return;
        }

        ContentRows.Add(new("current", BranchStatusText, _branchRepoRoot));
        foreach (var branch in _allBranchItems.Take(80))
        {
            ContentRows.Add(new(branch.IsCurrent ? "current" : "branch", branch.Name, branch.Detail));
        }
    }

    private void LoadPluginsPage()
    {
        PanelTitle = L("插件", "Plugins");
        PanelSubtitle = $"{L("Pi package 管理", "Pi package management")} · {_pi.SettingsPath}";
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsConfigExpanded = true;
        SetPluginsPageMode(true);
        ContentRows.Clear();
        Suggestions.Clear();
        BuildPluginPackages();
        IsInspectorVisible = false;
    }

    private void BuildPluginPackages(string? preferredSource = null)
    {
        PluginPackages.Clear();
        foreach (var package in _packages)
        {
            PluginPackages.Add(new PluginPackageViewItem(package, IsEnglishUi));
        }

        var selected = PluginPackages.FirstOrDefault(item => item.Source.Equals(preferredSource ?? _selectedPluginPackage?.Source, StringComparison.OrdinalIgnoreCase))
            ?? PluginPackages.FirstOrDefault();
        SelectPluginPackage(selected);
        NotifyPluginPropertiesChanged();
    }

    public void SelectPluginPackage(PluginPackageViewItem? item)
    {
        _selectedPluginPackage = item;
        foreach (var package in PluginPackages) package.IsSelected = item is not null && package.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase);
        PluginResourceGroups.Clear();
        if (item is not null)
        {
            foreach (var group in item.Resources.GroupBy(resource => resource.Kind).OrderBy(group => PluginKindOrder(group.Key)))
            {
                PluginResourceGroups.Add(new PluginResourceGroupViewItem(PluginKindTitle(group.Key), group.Select(resource => new PluginResourceViewItem(resource.Name, resource.Path))));
            }
        }
        NotifyPluginPropertiesChanged();
    }

    public void RefreshPluginsPage()
    {
        _packages = _pi.ListPackages().ToList();
        BuildPluginPackages(_selectedPluginPackage?.Source);
        PluginActionStatus = L("插件列表已重新加载", "Plugin list reloaded");
        StatusText = $"loaded {_packages.Count} packages";
    }

    public void OpenAddPlugin()
    {
        PluginNewSource = string.IsNullOrWhiteSpace(PluginNewSource) ? "npm:" : PluginNewSource;
        PluginActionStatus = string.Empty;
        IsPluginAddOpen = true;
    }

    public void CancelAddPlugin()
    {
        IsPluginAddOpen = false;
        PluginActionStatus = string.Empty;
    }

    public async Task AddPluginAsync()
    {
        var source = PluginNewSource.Trim();
        if (string.IsNullOrWhiteSpace(source) || source == "npm:")
        {
            PluginActionStatus = L("请输入 package source，例如 npm:my-pi-plugin", "Enter a package source, for example npm:my-pi-plugin");
            return;
        }
        if (!source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(source))
        {
            PluginActionStatus = L("目前支持 npm:、file: 或本地目录。", "Use npm:, file:, or a local folder path.");
            return;
        }

        IsPluginActionRunning = true;
        try
        {
            if (source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                var packageName = source[4..].Trim();
                PluginActionStatus = $"npm install {packageName}";
                await RunNpmAsync("install", packageName);
            }
            var added = _pi.AddPackageSource(source);
            IsPluginAddOpen = false;
            RefreshPluginsPage();
            PluginActionStatus = added ? L("插件已添加", "Plugin added") : L("插件已存在", "Plugin already exists");
        }
        catch (Exception ex)
        {
            PluginActionStatus = ex.Message;
        }
        finally
        {
            IsPluginActionRunning = false;
        }
    }

    public async Task UpdateSelectedPluginAsync()
    {
        var selected = _selectedPluginPackage;
        if (selected is null) return;
        if (!selected.IsNpmPackage)
        {
            PluginActionStatus = L("只有 npm package 支持自动更新。", "Only npm packages can be updated automatically.");
            return;
        }

        IsPluginActionRunning = true;
        try
        {
            PluginActionStatus = $"npm install {selected.PackageName}@latest";
            await RunNpmAsync("install", $"{selected.PackageName}@latest");
            RefreshPluginsPage();
            PluginActionStatus = L("插件已更新", "Plugin updated");
        }
        catch (Exception ex)
        {
            PluginActionStatus = ex.Message;
        }
        finally
        {
            IsPluginActionRunning = false;
        }
    }

    public void ToggleSelectedPluginEnabled()
    {
        var selected = _selectedPluginPackage;
        if (selected is null || IsPluginActionRunning) return;
        IsPluginActionRunning = true;
        try
        {
            var nextDisabled = !selected.Disabled;
            var changed = _pi.SetPackageDisabled(selected.Source, nextDisabled);
            RefreshPluginsPage();
            PluginActionStatus = changed
                ? nextDisabled ? L("插件已禁用", "Plugin disabled") : L("插件已启用", "Plugin enabled")
                : L("settings.json 中未找到该插件。", "Plugin was not found in settings.json.");
        }
        catch (Exception ex)
        {
            PluginActionStatus = ex.Message;
        }
        finally
        {
            IsPluginActionRunning = false;
        }
    }

    public void RemoveSelectedPlugin()
    {
        var selected = _selectedPluginPackage;
        if (selected is null) return;
        var removed = _pi.RemovePackageSource(selected.Source);
        RefreshPluginsPage();
        PluginActionStatus = removed
            ? L("已从 settings.json 移除；安装目录未删除。", "Removed from settings.json; installed files were not deleted.")
            : L("settings.json 中未找到该插件。", "Plugin was not found in settings.json.");
    }

    private async Task RunNpmAsync(string command, string argument)
    {
        Directory.CreateDirectory(_pi.NpmPackagesDir);
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
            WorkingDirectory = _pi.NpmPackagesDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(command);
        psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start npm");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = (await stderrTask).Trim();
        _ = await stdoutTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"npm {command} failed" : stderr);
    }

    private static int PluginKindOrder(string kind) => kind switch
    {
        "extension" => 0,
        "skill" => 1,
        "prompt" => 2,
        "theme" => 3,
        _ => 9,
    };

    private string PluginKindTitle(string kind) => kind switch
    {
        "extension" => L("扩展", "EXTENSIONS"),
        "skill" => L("技能", "SKILLS"),
        "prompt" => L("提示", "PROMPTS"),
        "theme" => L("主题", "THEMES"),
        _ => kind.ToUpperInvariant(),
    };

    private string BuildPluginFooterText()
    {
        var extensions = _packages.Sum(package => package.Resources.Count(resource => resource.Kind == "extension"));
        var skills = _packages.Sum(package => package.Resources.Count(resource => resource.Kind == "skill"));
        var prompts = _packages.Sum(package => package.Resources.Count(resource => resource.Kind == "prompt"));
        var themes = _packages.Sum(package => package.Resources.Count(resource => resource.Kind == "theme"));
        return IsEnglish
            ? $"{extensions} ext · {skills} skills · {prompts} prompts · {themes} themes"
            : $"{extensions} 扩展 · {skills} 技能 · {prompts} 提示 · {themes} 主题";
    }

    private void NotifyPluginPropertiesChanged()
    {
        foreach (var name in new[]
        {
            nameof(PluginCountText), nameof(PluginFooterText), nameof(PluginPageDescription), nameof(PluginAddButtonText), nameof(PluginUpdateButtonText),
            nameof(PluginReloadButtonText), nameof(PluginRemoveButtonText), nameof(PluginCancelButtonText), nameof(PluginEmptyTitle), nameof(PluginEmptyDescription),
            nameof(PluginStatusLabel), nameof(PluginVersionLabel), nameof(PluginPackageLabel), nameof(PluginResourcesLabel), nameof(PluginInstalledPathLabel),
            nameof(PluginScopeLabel), nameof(PluginResolvedResourcesLabel), nameof(SelectedPluginTitle), nameof(SelectedPluginSource), nameof(SelectedPluginStatus), nameof(SelectedPluginEnabled), nameof(SelectedPluginStatusBrush),
            nameof(SelectedPluginStatusForeground), nameof(SelectedPluginVersion), nameof(SelectedPluginPackageName), nameof(SelectedPluginResources),
            nameof(SelectedPluginPath), nameof(SelectedPluginScope), nameof(PluginEmptyVisibility), nameof(PluginDetailVisibility), nameof(SelectedPluginNpmActionVisibility),
            nameof(PluginActionStatus), nameof(PluginActionStatusVisibility), nameof(PluginAddVisibility), nameof(PluginActionButtonEnabled)
        }) OnPropertyChanged(name);
    }

    private void LoadSkillsPage()
    {
        var enabledCount = _skills.Count(skill => skill.IsEnabled);
        PanelTitle = L("技能", "Skills");
        PanelSubtitle = L($"{enabledCount}/{_skills.Count} 个 skill 已启用 · 可追加其他 agent 的 skills 目录", $"{enabledCount}/{_skills.Count} skills enabled · add skill folders from other agents");
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsConfigExpanded = true;
        ContentRows.Clear();
        Suggestions.Clear();
        ContentRows.Add(new("+", L("添加 skill 目录", "Add skill folder"), L("选择其他 agent 根目录或 skills 文件夹。", "Choose another agent root or skills folder."), null, "skillAddPath"));
        foreach (var source in _skillSources)
        {
            var count = _skills.Count(skill => PathsEqual(skill.SourcePath, source.Path));
            var badge = source.IsEnabled ? "source:on" : "source:off";
            var state = source.IsEnabled ? L("已启用", "enabled") : L("已禁用", "disabled");
            var detail = $"{state} · {count} skills · {source.Path}";
            ContentRows.Add(new(badge, $"{source.Label}", detail, source.Path, "skillSourceToggle"));
        }
        foreach (var skill in _skills.Take(220))
        {
            var badge = skill.IsEnabled ? "skill:on" : "skill:off";
            var state = skill.IsEnabled ? L("启用", "enabled") : L("禁用", "disabled");
            var description = string.IsNullOrWhiteSpace(skill.Description) ? skill.Path : skill.Description;
            ContentRows.Add(new(badge, $"{skill.Source}: {skill.Name}", $"{state} · {description}", skill.Path, "skill"));
        }
        LoadInspector("Skills", new[]
        {
            new PanelRow("enabled", $"{enabledCount}/{_skills.Count}"),
            new PanelRow("sources", _skillSources.Count.ToString()),
            new PanelRow("settings", Path.Combine(IpiPathService.AppDataDir, "skills.json"))
        });
    }

    private void ExportSession(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var output = _pi.ExportSessionMarkdown(path);
        if (IsChatMode)
        {
            ContentRows.Add(new("system", "Session exported", output, output, "file"));
            StatusText = "session exported";
            return;
        }
        PanelTitle = "Session exported";
        PanelSubtitle = output;
        IsChatMode = false;
        CanReturnToChat = true;
        IsToolbarVisible = true;
        ContentRows.Clear();
        ContentRows.Add(new("export", "Markdown written", output, output, "file"));
        StatusText = "session exported";
    }

    private async void CompactCurrentSession()
    {
        if (string.IsNullOrWhiteSpace(_activeSessionFile) || !File.Exists(_activeSessionFile))
        {
            StatusText = "open a session before compacting";
            return;
        }
        if (IsAgentRunning)
        {
            StatusText = "agent is already running";
            return;
        }

        IsAgentRunning = true;
        IsChatMode = true;
        SessionStatsText = "compacting";
        ContentRows.Add(new("system", "Compacting context…", ""));
        try
        {
            var runCwd = ResolveRunCwd();
            var result = await _agentBridge.CompactAsync(runCwd, _pi.AgentDir, _activeSessionFile, AddBridgeEvent, ThinkingLevel);
            ContentRows.Add(new("system", string.IsNullOrWhiteSpace(result.FinalText) ? "Context compacted" : result.FinalText, ""));
            StatusText = "context compacted";
            SessionStatsText = L("就绪", "Ready");
            RefreshLocalData();
        }
        catch (Exception ex)
        {
            ContentRows.Add(new("error", "Compaction failed", ex.Message));
            StatusText = "compaction failed";
            SessionStatsText = "error";
        }
        finally
        {
            IsAgentRunning = false;
        }
    }

    private void LoadSystemPage()
    {
        var settings = _pi.ReadSettingsSummary();
        PanelTitle = SystemText;
        PanelSubtitle = L("本地 agent 上下文、工具、项目规则和技能清单。", "Local agent context, tools, project rules, and skills.");
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = true;
        SetSystemContextMode(true);
        ContentRows.Clear();
        Suggestions.Clear();
        PopulateSystemContextSections(settings);
        StatusText = L("system context loaded", "system context loaded");
    }

    private void PopulateSystemContextSections((string DefaultProvider, string DefaultModel, string DefaultThinking) settings)
    {
        var sections = new[]
        {
            new SystemContextSection(L("运行摘要", "Runtime summary"), L("模型、工具、批准策略和本地资源。", "Model, tools, approval policy, and local resources."), BuildRuntimeSummaryText(settings), true),
            new SystemContextSection(L("全局系统", "Global system"), L("基础 system prompt、工具和通用操作规则。", "Base system prompt, tools, and general operating rules."), BuildGlobalSystemText(), true),
            new SystemContextSection(L("项目上下文", "Project context"), L("当前 cwd 和命中的 AGENTS.md/project instructions。", "Current cwd and matched AGENTS.md/project instructions."), BuildProjectContextText(), true),
            new SystemContextSection(L("可用技能", "Available skills"), L($"{_skills.Count} skills · 按需读取 SKILL.md", $"{_skills.Count} skills · read SKILL.md when needed"), BuildSkillsContextText(), false),
            new SystemContextSection(L("记忆系统", "Memory system"), BuildMemorySectionDetail(), BuildMemoryContextText(), false),
        };

        SystemContextSections.Clear();
        foreach (var section in sections) SystemContextSections.Add(section);
        SystemContextText = string.Join("\n\n", sections.Select(section => $"## {section.Title}\n{section.Text}"));
    }

    private string BuildGlobalSystemText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.");
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        foreach (var line in DescribeExposedTools()) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("In addition to the tools above, you may have access to other custom tools depending on the project.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Use bash for file operations like ls, rg, find");
        sb.AppendLine("- Use read to examine files instead of cat or sed.");
        sb.AppendLine("- Use edit for precise changes (edits[].oldText must match exactly)");
        sb.AppendLine("- When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls");
        sb.AppendLine("- Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.");
        sb.AppendLine("- Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions.");
        sb.AppendLine("- Use write only for new files or complete rewrites.");
        sb.AppendLine("- Be concise in your responses");
        sb.AppendLine("- Show file paths clearly when working with files");
        sb.AppendLine();
        sb.AppendLine("Pi documentation (read only when the user asks about pi itself, its SDK, extensions, themes, skills, or TUI):");
        sb.AppendLine("- Main documentation: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent", "README.md"));
        sb.AppendLine("- Additional docs: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent", "docs"));
        sb.AppendLine("- Examples: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@agegr", "pi-web", "node_modules", "@earendil-works", "pi-coding-agent", "examples"));
        sb.AppendLine("- When reading pi docs or examples, resolve docs/... under Additional docs and examples/... under Examples, not the current working directory");
        sb.AppendLine("- When working on pi topics, read the docs and examples, and follow .md cross-references before implementing");
        return sb.ToString();
    }

    private string BuildProjectContextText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Current date: {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"Current working directory: {ResolveRunCwd()}");
        sb.AppendLine();
        sb.AppendLine("<project_context>");
        AppendInstructionFiles(sb);
        sb.AppendLine("</project_context>");
        return sb.ToString();
    }

    private string BuildSkillsContextText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("The following skills provide specialized instructions for specific tasks.");
        sb.AppendLine("Use the read tool to load a skill's file when the task matches its description.");
        sb.AppendLine("When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.");
        sb.AppendLine();
        sb.AppendLine("<available_skills>");
        foreach (var skill in _skills.Where(s => s.IsEnabled).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            if (!string.IsNullOrWhiteSpace(skill.Description)) sb.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{EscapeXml(skill.Path)}</location>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</available_skills>");
        return sb.ToString();
    }

    private string BuildMemorySectionDetail()
    {
        var systems = DetectInstalledMemorySystems();
        if (systems.Count > 0)
        {
            return string.Join(" · ", systems.Select(system => system.Name));
        }

        var fallbackCount = DiscoverMemoryContextSkills().Count;
        return fallbackCount == 0
            ? L("未检测到已安装的记忆系统。", "No installed memory system detected.")
            : L($"未检测到明确系统，发现 {fallbackCount} 个疑似记忆相关 skill。", $"No known system detected; found {fallbackCount} possible memory-related skills.");
    }

    private string BuildMemoryContextText()
    {
        var systems = DetectInstalledMemorySystems();
        var sb = new StringBuilder();
        sb.AppendLine("## Memory system");
        sb.AppendLine("ipi first detects known installed memory systems. It only falls back to skill keyword scanning when no known system is detected.");
        sb.AppendLine();

        if (systems.Count > 0)
        {
            sb.AppendLine("<detected_memory_systems>");
            foreach (var system in systems)
            {
                sb.AppendLine("  <memory_system>");
                sb.AppendLine($"    <name>{EscapeXml(system.Name)}</name>");
                sb.AppendLine($"    <status>{EscapeXml(system.Status)}</status>");
                sb.AppendLine($"    <evidence>{EscapeXml(system.Evidence)}</evidence>");
                if (system.Skills.Count > 0)
                {
                    sb.AppendLine("    <skills>");
                    foreach (var skill in system.Skills)
                    {
                        sb.AppendLine($"      <skill name=\"{EscapeXml(skill.Name)}\" location=\"{EscapeXml(skill.Path)}\" />");
                    }
                    sb.AppendLine("    </skills>");
                }
                sb.AppendLine("  </memory_system>");
            }
            sb.AppendLine("</detected_memory_systems>");
            return sb.ToString();
        }

        var memorySkills = DiscoverMemoryContextSkills();
        if (memorySkills.Count == 0)
        {
            sb.AppendLine("[No known memory system or memory-related skills detected.]");
            return sb.ToString();
        }

        sb.AppendLine("<fallback_memory_skill_scan>");
        foreach (var skill in memorySkills)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            if (!string.IsNullOrWhiteSpace(skill.Description)) sb.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{EscapeXml(skill.Path)}</location>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</fallback_memory_skill_scan>");
        return sb.ToString();
    }

    private List<MemorySystemInfo> DetectInstalledMemorySystems()
    {
        var systems = new List<MemorySystemInfo>();

        var nowledgeSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "read-working-memory", "search-memory", "save-thread", "distill-memory"
        };
        var nowledgeSkills = _skills
            .Where(skill => skill.IsEnabled &&
                (skill.Path.Contains("nowledge-mem", StringComparison.OrdinalIgnoreCase) ||
                skill.Description.Contains("Nowledge Mem", StringComparison.OrdinalIgnoreCase) ||
                nowledgeSkillNames.Contains(skill.Name)))
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nowledgePackage = Path.Combine(_pi.AgentDir, "npm", "node_modules", "nowledge-mem-pi");
        if (nowledgeSkills.Count > 0 || Directory.Exists(nowledgePackage))
        {
            var evidence = Directory.Exists(nowledgePackage)
                ? nowledgePackage
                : string.Join("; ", nowledgeSkills.Select(skill => skill.Path).Take(3));
            systems.Add(new MemorySystemInfo("Nowledge Mem", "installed", evidence, nowledgeSkills));
        }

        return systems;
    }

    private List<SkillRecord> DiscoverMemoryContextSkills()
    {
        var keywords = new[]
        {
            "memory", "mem", "nmem", "nowledge", "working-memory", "thread", "recall", "distill"
        };
        return _skills
            .Where(skill => skill.IsEnabled)
            .Where(skill => keywords.Any(keyword =>
                skill.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                skill.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                skill.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string BuildRuntimeSummaryText((string DefaultProvider, string DefaultModel, string DefaultThinking) settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ipi runtime summary");
        sb.AppendLine($"- provider: {settings.DefaultProvider}");
        sb.AppendLine($"- model: {settings.DefaultModel}");
        sb.AppendLine($"- configured thinking: {settings.DefaultThinking}");
        sb.AppendLine($"- active thinking: {ThinkingLevel}");
        sb.AppendLine($"- approval mode: {ApprovalLabel} ({ApprovalModeKey()})");
        sb.AppendLine($"- tool preset: {ToolLabel} — {CurrentToolPreset.Description}");
        sb.AppendLine($"- context window: {(_contextLimitTokens is > 0 ? FormatCompactNumber(_contextLimitTokens.Value) : "unknown")}");
        sb.AppendLine($"- sessions loaded: {_sessions.Count}");
        sb.AppendLine($"- skills loaded: {_skills.Count}");
        sb.AppendLine($"- packages loaded: {_packages.Count}");
        sb.AppendLine($"- active session: {_activeSessionFile ?? "new session"}");
        sb.AppendLine($"- active cwd: {_activeCwd ?? ProjectPath}");
        sb.AppendLine($"- runtime mode: {_pi.RuntimeInfo.RuntimeMode}");
        sb.AppendLine($"- bundled runtime: {(_pi.RuntimeInfo.IsBundled ? "yes" : "no")}");
        sb.AppendLine($"- initialized runtime: {(_pi.RuntimeInfo.IsInitialized ? "yes" : "no")}");
        sb.AppendLine($"- node: {(_pi.RuntimeInfo.NodePath ?? "PATH")}");
        sb.AppendLine($"- pi package root: {(_pi.RuntimeInfo.PiCodingAgentRoot ?? "not found")}");
        sb.AppendLine($"- agent dir: {_pi.AgentDir}");
        sb.AppendLine($"- sessions dir: {_pi.SessionsDir}");
        sb.AppendLine($"- settings: {_pi.SettingsPath}");
        sb.AppendLine($"- models: {_pi.ModelsPath}");
        return sb.ToString();
    }

    private IEnumerable<string> DescribeExposedTools()
    {
        if (CurrentToolPreset.NoTools == "all") yield break;
        var tools = CurrentToolPreset.Tools ?? new[] { "read", "bash", "edit", "write" };
        foreach (var tool in tools)
        {
            yield return tool switch
            {
                "read" => "- read: Read file contents",
                "bash" => "- bash: Execute bash commands (ls, grep, find, etc.)",
                "edit" => "- edit: Make precise file edits with exact text replacement, including multiple disjoint edits in one call",
                "write" => "- write: Create or overwrite files",
                "grep" => "- grep: Search file contents",
                "find" => "- find: Find files and directories",
                "ls" => "- ls: List directory contents",
                _ => $"- {tool}: enabled by current tool preset",
            };
        }
    }

    private void AppendInstructionFiles(StringBuilder sb)
    {
        var files = new List<string>();
        var global = Path.Combine(_pi.AgentDir, "AGENTS.md");
        if (File.Exists(global)) files.Add(global);
        foreach (var file in FindAncestorInstructionFiles(ResolveRunCwd()))
        {
            if (!files.Any(existing => PathsEqual(existing, file))) files.Add(file);
        }

        if (files.Count == 0)
        {
            sb.AppendLine("[No AGENTS.md instruction files found.]");
            return;
        }

        foreach (var file in files)
        {
            sb.AppendLine("<project_instructions path=\"" + EscapeXml(file) + "\">");
            sb.AppendLine(_pi.ReadTextPreview(file, 128 * 1024).TrimEnd());
            sb.AppendLine("</project_instructions>");
            sb.AppendLine();
        }
    }

    private static IEnumerable<string> FindAncestorInstructionFiles(string start)
    {
        var dir = File.Exists(start) ? Path.GetDirectoryName(start) : start;
        while (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            var candidate = Path.Combine(dir, "AGENTS.md");
            if (File.Exists(candidate)) yield return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || parent == dir) break;
            dir = parent;
        }
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private void OpenSkill(string? path, string title)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var skill = _skills.FirstOrDefault(item => PathsEqual(item.Path, path));
        var enabled = skill?.IsEnabled ?? true;
        var source = skill?.Source ?? (path.Contains(".codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "pi");
        PanelTitle = title;
        PanelSubtitle = path;
        IsChatMode = false;
        CanReturnToChat = true;
        IsToolbarVisible = false;
        ContentRows.Clear();
        Suggestions.Clear();
        var sourceEnabled = skill?.SourceEnabled ?? true;
        ContentRows.Add(new(enabled ? "on" : "off", sourceEnabled ? enabled ? L("禁用此 skill", "Disable this skill") : L("启用此 skill", "Enable this skill") : L("来源已禁用", "Source disabled"), sourceEnabled ? enabled ? L("当前会注入到 agent 可用技能列表。", "Currently included in the agent skills context.") : L("当前不会注入到 agent 可用技能列表。", "Currently excluded from the agent skills context.") : L("请先回到 Skills 页面启用该来源。", "Enable this source from the Skills page first."), path, "skillToggle"));
        var preview = _pi.ReadTextPreview(path, 96 * 1024);
        ContentRows.Add(new("SKILL.md", preview.Length > 12000 ? preview[..12000] + "…" : preview, "", path, "skillPreview"));
        LoadInspector("Skill", new[]
        {
            new PanelRow("state", enabled ? "enabled" : "disabled"),
            new PanelRow("source", source),
            new PanelRow("path", path)
        });
    }

    public bool AddSkillSourceFolder(string path, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            message = L("目录不存在。", "Directory does not exist.");
            return false;
        }
        var added = _pi.AddSkillSource(path);
        RefreshLocalData();
        LoadSkillsPage();
        message = added ? L("已添加 skill 来源。", "Skill source added.") : L("该 skill 来源已存在或无效。", "Skill source already exists or is invalid.");
        StatusText = message;
        return added;
    }

    private void ToggleSkillSourceEnabled(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var source = _skillSources.FirstOrDefault(item => PathsEqual(item.Path, path));
        if (source is null) return;
        _pi.SetSkillSourceEnabled(path, !source.IsEnabled);
        RefreshLocalData();
        LoadSkillsPage();
        StatusText = source.IsEnabled ? L("skill 来源已禁用", "skill source disabled") : L("skill 来源已启用", "skill source enabled");
    }

    private void ToggleSkillEnabled(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var skill = _skills.FirstOrDefault(item => PathsEqual(item.Path, path));
        if (skill is { SourceEnabled: false })
        {
            StatusText = L("先启用 skill 来源。", "Enable the skill source first.");
            return;
        }
        var enabled = skill?.IsEnabled ?? true;
        _pi.SetSkillEnabled(path, !enabled);
        RefreshLocalData();
        OpenSkill(path, skill?.Name ?? Path.GetFileName(Path.GetDirectoryName(path)) ?? "skill");
        StatusText = enabled ? L("skill 已禁用", "skill disabled") : L("skill 已启用", "skill enabled");
    }

    private void LoadModelsPage()
    {
        var settings = _pi.ReadSettingsSummary();
        PanelTitle = L("模型", "Models");
        PanelSubtitle = _pi.SettingsPath;
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsConfigExpanded = true;
        ContentRows.Clear();
        Suggestions.Clear();
        ContentRows.Add(new("default provider", settings.DefaultProvider, "from settings.json"));
        ContentRows.Add(new("default model", settings.DefaultModel, "from settings.json"));
        ContentRows.Add(new("thinking", settings.DefaultThinking, "from settings.json"));
        ContentRows.Add(new("models.json", File.Exists(_pi.ModelsPath) ? _pi.ModelsPath : "not found"));
        LoadInspector("Models", new[]
        {
            new PanelRow("settings", _pi.SettingsPath),
            new PanelRow("models", _pi.ModelsPath),
            new PanelRow("composer", "model/thinking menu selects reasoning for new turns")
        });
    }

    private void LoadToolsPage()
    {
        PanelTitle = L("工具", "Tools");
        PanelSubtitle = L("点击预设切换；发送时会传给本地 agent runtime。", "Click a preset to switch; it will be sent to the local agent runtime.");
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsConfigExpanded = true;
        ContentRows.Clear();
        Suggestions.Clear();
        foreach (var preset in _toolPresets)
        {
            ContentRows.Add(new(preset.Key == CurrentToolPreset.Key ? "active" : "preset", preset.Label, preset.Description, preset.Key, "toolPreset"));
        }
        LoadInspector("Tools", new[]
        {
            new PanelRow("current", CurrentToolPreset.Label, CurrentToolPreset.Description),
            new PanelRow("approval", ApprovalLabel, L("按 Codex 风格在实际工具调用时请求批准", "Ask for approval when tools are actually requested")),
            new PanelRow("runtime", "local agent session", "tool settings are passed into the local runtime")
        });
    }

    private void LoadRuntimeDiagnosticsPage()
    {
        var settings = _pi.ReadSettingsSummary();
        var diagnostics = BuildRuntimeDiagnostics(settings);
        var issueCount = diagnostics.Count(item => item.Level is "warn" or "error");

        PanelTitle = L("运行时诊断", "Runtime diagnostics");
        PanelSubtitle = issueCount == 0
            ? L($"全部基础检查通过 · {_pi.AgentDir}", $"All baseline checks passed · {_pi.AgentDir}")
            : L($"{issueCount} 项需要注意 · {_pi.AgentDir}", $"{issueCount} items need attention · {_pi.AgentDir}");
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsConfigExpanded = true;
        ContentRows.Clear();
        Suggestions.Clear();
        foreach (var item in diagnostics)
        {
            ContentRows.Add(new(DiagnosticBadge(item.Level), item.Title, item.Detail));
        }
        LoadInspector(L("诊断", "Diagnostics"), new[]
        {
            new PanelRow("checked", DateTime.Now.ToString("g")),
            new PanelRow("issues", issueCount.ToString()),
            new PanelRow("agent", _pi.AgentDir),
            new PanelRow("node", ResolveNodeLabel())
        });
        StatusText = L("运行时诊断已刷新", "runtime diagnostics refreshed");
    }

    private List<RuntimeDiagnosticItem> BuildRuntimeDiagnostics((string DefaultProvider, string DefaultModel, string DefaultThinking) settings)
    {
        var items = new List<RuntimeDiagnosticItem>();
        void Add(string level, string title, string detail) => items.Add(new RuntimeDiagnosticItem(level, title, detail));
        void AddPath(string title, string path, bool isDirectory)
        {
            var exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
            var detail = exists ? SafePathSummary(path, isDirectory) : L($"缺失 · {path}", $"missing · {path}");
            Add(exists ? "ok" : "error", title, detail);
        }

        var runtime = _pi.RuntimeInfo;
        Add("info", L("运行时模式", "Runtime mode"), L($"{runtime.RuntimeMode} · 内置: {YesNo(runtime.IsBundled)} · 已初始化: {YesNo(runtime.IsInitialized)}", $"{runtime.RuntimeMode} · bundled: {YesNo(runtime.IsBundled)} · initialized: {YesNo(runtime.IsInitialized)}"));
        AddPath(L("Agent 目录", "Agent directory"), _pi.AgentDir, true);
        AddPath("settings.json", _pi.SettingsPath, false);
        AddPath("models.json", _pi.ModelsPath, false);
        AddPath(L("Sessions 目录", "Sessions directory"), _pi.SessionsDir, true);
        foreach (var source in _skillSources)
        {
            AddPath(L($"Skill 来源 · {source.Label}", $"Skill source · {source.Label}"), source.Path, true);
        }
        AddPath(L("npm 目录", "npm directory"), runtime.NpmPackagesDir, true);

        var bridgePath = Path.Combine(AppContext.BaseDirectory, "agent-bridge.mjs");
        AddPath(L("Agent bridge", "Agent bridge"), bridgePath, false);

        var nodeProbe = ProbeNodeVersion();
        Add(nodeProbe.Level, L("Node 运行时", "Node runtime"), nodeProbe.Detail);

        var defaultProviderReady = IsConfiguredValue(settings.DefaultProvider);
        var defaultModelReady = IsConfiguredValue(settings.DefaultModel);
        Add(defaultProviderReady && defaultModelReady ? "ok" : "warn", L("默认模型", "Default model"),
            defaultProviderReady && defaultModelReady
                ? $"{settings.DefaultProvider}/{settings.DefaultModel} · thinking: {settings.DefaultThinking}"
                : L("未设置默认 provider/model；新环境需要先完成 provider onboarding。", "Default provider/model is not set; complete provider onboarding in a fresh environment."));

        var modelOptions = _pi.ReadModelOptions(settings);
        var modelsJsonOptions = modelOptions.Where(option => option.Source.Equals("models.json", StringComparison.OrdinalIgnoreCase)).ToList();
        var providerCount = modelsJsonOptions.Select(option => option.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Add(providerCount > 0 ? "ok" : "warn", L("Provider 配置", "Provider configuration"),
            providerCount > 0
                ? L($"models.json 中发现 {providerCount} 个 provider、{modelsJsonOptions.Count} 个 model。不会显示 API key。", $"Found {providerCount} provider(s) and {modelsJsonOptions.Count} model(s) in models.json. API keys are not displayed.")
                : L("models.json 中未发现 provider model；这不等于已连接 provider。", "No provider models found in models.json; this does not count as a connected provider."));
        Add("info", L("认证检查", "Authentication check"), L("未在诊断页读取或显示密钥；实际认证由本地 agent runtime 验证。", "Diagnostics does not read or display secrets; authentication is validated by the local agent runtime."));

        Add(_sessions.Count > 0 ? "ok" : "info", L("会话索引", "Session index"), L($"已加载 {_sessions.Count} 个会话 · {_pi.SessionsDir}", $"Loaded {_sessions.Count} session(s) · {_pi.SessionsDir}"));
        var enabledSkills = _skills.Count(skill => skill.IsEnabled);
        Add(_skills.Count > 0 ? "ok" : "warn", L("技能索引", "Skill index"), L($"已发现 {_skills.Count} 个 skill，启用 {enabledSkills} 个。", $"Discovered {_skills.Count} skill(s), {enabledSkills} enabled."));
        Add("info", L("插件包", "Plugin packages"), L($"已读取 {_packages.Count} 个 package source。", $"Read {_packages.Count} package source(s)."));

        var cwd = ResolveRunCwd();
        Add(Directory.Exists(cwd) ? "ok" : "error", L("当前工作目录", "Current working directory"), cwd);
        var gitRoot = ResolveGitRoot(cwd);
        if (string.IsNullOrWhiteSpace(gitRoot))
        {
            Add("info", "Git", L("当前工作目录不在 Git 仓库中。", "Current working directory is not inside a Git repository."));
        }
        else
        {
            var worktrees = RunGit(gitRoot, "worktree", "list", "--porcelain");
            var count = worktrees.ExitCode == 0 ? worktrees.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith("worktree ", StringComparison.OrdinalIgnoreCase)) : 0;
            Add("ok", "Git", count > 0 ? L($"root: {gitRoot} · worktrees: {count}", $"root: {gitRoot} · worktrees: {count}") : $"root: {gitRoot}");
        }

        return items;
    }

    private string RuntimeDiagnosticsSidebarValue()
    {
        var missing = 0;
        if (!Directory.Exists(_pi.AgentDir)) missing++;
        if (!File.Exists(_pi.SettingsPath)) missing++;
        if (!File.Exists(_pi.ModelsPath)) missing++;
        if (!Directory.Exists(_pi.SessionsDir)) missing++;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "agent-bridge.mjs"))) missing++;
        return missing == 0 ? L("基础路径就绪", "baseline ready") : L($"{missing} 项缺失", $"{missing} missing");
    }

    private (string Level, string Detail) ProbeNodeVersion()
    {
        var label = ResolveNodeLabel();
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
            if (process is null) return ("error", L($"无法启动 Node · {label}", $"Unable to start Node · {label}"));
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return ("error", L($"Node 检查超时 · {label}", $"Node check timed out · {label}"));
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            return process.ExitCode == 0
                ? ("ok", $"{label} · {output}")
                : ("error", $"{label} · {error}");
        }
        catch (Exception ex)
        {
            return ("error", $"{label} · {ex.Message}");
        }
    }

    private string ResolveNodeLabel() => _pi.RuntimeInfo.NodePath ?? "PATH: node";

    private string SafePathSummary(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var info = new DirectoryInfo(path);
                return L($"存在 · {path} · 修改于 {info.LastWriteTime:g}", $"exists · {path} · modified {info.LastWriteTime:g}");
            }
            else
            {
                var info = new FileInfo(path);
                return L($"存在 · {path} · {FormatSize(info.Length)} · 修改于 {info.LastWriteTime:g}", $"exists · {path} · {FormatSize(info.Length)} · modified {info.LastWriteTime:g}");
            }
        }
        catch
        {
            return path;
        }
    }

    private string YesNo(bool value) => value ? L("是", "yes") : L("否", "no");

    private string DiagnosticBadge(string level) => level switch
    {
        "ok" => L("通过", "ok"),
        "warn" => L("注意", "warn"),
        "error" => L("错误", "error"),
        _ => L("信息", "info"),
    };

    private static bool IsConfiguredValue(string value)
        => !string.IsNullOrWhiteSpace(value) && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private void LoadSettingsPage()
    {
        var settings = _pi.ReadSettingsSummary();
        PanelTitle = L("设置", "Settings");
        PanelSubtitle = L("本地 Pi 工作区、运行权限和资源目录。", "Local Pi workspace, runtime permissions, and resource directories.");
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsToolbarVisible = false;
        IsConfigExpanded = true;
        ContentRows.Clear();
        Suggestions.Clear();
        ContentRows.Add(new(L("工作模式", "mode"), IsGlobalChat ? L("默认聊天", "Default chat") : L("项目聊天", "Project chat"), IsGlobalChat ? GlobalChatRoot : ProjectPath));
        ContentRows.Add(new(L("策略", "policy"), ApprovalLabel, L("在实际工具调用时以内联卡片请求批准", "Inline approval cards appear when tools are actually requested")));
        ContentRows.Add(new(L("工具", "tools"), ToolLabel, CurrentToolPreset.Description));
        ContentRows.Add(new(L("模型", "model"), settings.DefaultModel, $"provider: {settings.DefaultProvider} · thinking: {settings.DefaultThinking}"));
        ContentRows.Add(new(L("目录", "dir"), "Pi agent", _pi.AgentDir));
        foreach (var source in _skillSources)
        {
            ContentRows.Add(new(source.IsEnabled ? "skill:on" : "skill:off", $"Skill source · {source.Label}", source.Path));
        }
        ContentRows.Add(new(L("目录", "dir"), "Sessions", _pi.SessionsDir));
        LoadInspector("Settings", new[]
        {
            new PanelRow("agent", _pi.AgentDir),
            new PanelRow("settings", _pi.SettingsPath),
            new PanelRow("models", _pi.ModelsPath)
        });
    }

    private void RunSlashCommand(string command)
    {
        IsStartActionsVisible = false;
        var cmd = command.Trim().ToLowerInvariant();
        if (cmd.StartsWith("/sessions")) LoadSessionsPage();
        else if (cmd.StartsWith("/files")) LoadFilesPage(ProjectPath);
        else if (cmd.StartsWith("/skills")) LoadSkillsPage();
        else if (cmd.StartsWith("/model")) LoadModelsPage();
        else if (cmd.StartsWith("/tools")) LoadToolsPage();
        else if (cmd.StartsWith("/settings")) LoadSettingsPage();
        else if (cmd.StartsWith("/search")) LoadSearchPage();
        else
        {
            PanelTitle = command;
            PanelSubtitle = L("未知 slash command", "Unknown slash command");
            ContentRows.Clear();
            ContentRows.Add(new("unknown", L("可用命令", "Available commands"), "/sessions · /files · /skills · /model · /tools · /settings · /search"));
            StatusText = $"unknown command: {command}";
        }
    }

    private void LoadSessionsPage()
    {
        PanelTitle = L("对话", "Chats");
        PanelSubtitle = _pi.SessionsDir;
        IsChatMode = false;
        CanReturnToChat = true;
        IsComposerVisible = false;
        IsSessionsExpanded = true;
        ContentRows.Clear();
        Suggestions.Clear();
        foreach (var session in _sessions.Take(100))
        {
            ContentRows.Add(new("session", session.Title, $"{ShortenPath(session.Cwd)} · {session.MessageCount} messages · {session.Modified:g}", session.FilePath, "session"));
        }
        LoadInspector("Sessions", new[]
        {
            new PanelRow("count", _sessions.Count.ToString()),
            new PanelRow("dir", _pi.SessionsDir),
            new PanelRow("supports", "open · read messages · branch metadata basic")
        });
    }

    private void LoadSidebarProjectGroups()
    {
        ProjectGroups.Clear();

        if (!IsGlobalCwd(ProjectPath))
        {
            var currentSessions = _sessions
                .Where(s => PathsEqual(s.Cwd, ProjectPath))
                .OrderByDescending(s => s.Modified)
                .Take(5)
                .Select(ToSidebarSession);
            ProjectGroups.Add(new(ProjectName, ProjectPathShort, ProjectPath, new ObservableCollection<SessionItem>(currentSessions), LooksLikeGitRepository(ProjectPath)));
        }

        var currentRoot = NormalizePath(ProjectPath);
        var otherGroups = _sessions
            .Where(s => !IsGlobalCwd(s.Cwd))
            .Where(s => NormalizePath(s.Cwd) != currentRoot)
            .GroupBy(s => NormalizePath(s.Cwd), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .OrderByDescending(g => g.Max(s => s.Modified))
            .Take(12);

        foreach (var group in otherGroups)
        {
            var first = group.OrderByDescending(s => s.Modified).First();
            var name = ProjectDisplayNameFor(first.Cwd);
            ProjectGroups.Add(new(name, ShortenPath(first.Cwd), first.Cwd, new ObservableCollection<SessionItem>(group.OrderByDescending(s => s.Modified).Take(4).Select(ToSidebarSession)), LooksLikeGitRepository(first.Cwd)));
        }

        UpdateProjectPickerItems();
    }

    private void UpdateProjectPickerItems()
    {
        ProjectPickerItems.Clear();
        var q = ProjectSearchText.Trim().ToLowerInvariant();
        foreach (var project in ProjectGroups.Where(p => q.Length == 0 || p.Name.ToLowerInvariant().Contains(q) || p.Path.ToLowerInvariant().Contains(q)).Take(12))
        {
            ProjectPickerItems.Add(project);
        }
    }

    private void LoadSidebarSessions()
    {
        Sessions.Clear();
        foreach (var session in _sessions.Where(s => IsGlobalCwd(s.Cwd)).OrderByDescending(s => s.Modified).Take(14))
        {
            Sessions.Add(ToSidebarSession(session));
        }
        // Empty state stays empty; do not add a fake session row.
    }

    private void LoadSidebarExplorer()
    {
        ExplorerItems.Clear();
        var topLevel = _files
            .Where(f => !f.RelativePath.Contains(Path.DirectorySeparatorChar) && !f.RelativePath.Contains(Path.AltDirectorySeparatorChar))
            .OrderBy(f => f.IsDirectory ? 0 : 1)
            .ThenBy(f => f.Name)
            .Take(24);
        foreach (var item in topLevel)
        {
            ExplorerItems.Add(new(item.IsDirectory ? "chevron-right" : "file", item.Name, item.Path, item.RelativePath, item.IsDirectory));
        }
    }

    public void RefreshExplorerTree()
    {
        _files = _pi.ListWorkspaceFiles(ProjectPath, 1800).ToList();
        LoadSidebarExplorer();
        LoadExplorerTree();
        StatusText = $"explorer refreshed · {_files.Count} items";
    }

    private void LoadExplorerTree()
    {
        ExplorerRoots.Clear();
        var nodes = new Dictionary<string, FileExplorerNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _files.OrderBy(f => DepthOf(f.RelativePath)).ThenBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = NormalizeRelativePath(file.RelativePath);
            var node = new FileExplorerNode(file.Name, file.Path, file.RelativePath, file.IsDirectory, file.IsDirectory ? "folder" : "file");
            nodes[key] = node;
            var parentKey = ParentRelativeKey(key);
            if (!string.IsNullOrWhiteSpace(parentKey) && nodes.TryGetValue(parentKey, out var parent)) parent.Children.Add(node);
            else ExplorerRoots.Add(node);
        }
    }

    public void OpenExplorerNode(FileExplorerNode node)
    {
        if (node.IsDirectory) return;
        OpenFileInRightPanel(node.Path, node.RelativePath);
    }

    public void ToggleRightPanel()
    {
        if (IsInspectorVisible && !IsRightPanelAutoHidden)
        {
            CloseRightPanel();
            return;
        }
        ShowRightActionsPanel();
    }

    private void SetRightPanelMode(string mode)
    {
        _rightPanelMode = mode;
        _isRightActionsMode = mode == "actions";
        _isFilePanelMode = mode == "file";
        OnPropertyChanged(nameof(InspectorRowsVisibility));
        OnPropertyChanged(nameof(FilePanelVisibility));
        OnPropertyChanged(nameof(RightPanelActionsVisibility));
        OnPropertyChanged(nameof(RightPanelTerminalVisibility));
        OnPropertyChanged(nameof(RightPanelBrowserVisibility));
        OnPropertyChanged(nameof(RightPanelFilesVisibility));
        OnPropertyChanged(nameof(RightPanelChatVisibility));
    }

    public void ShowRightActionsPanel()
    {
        SetRightPanelMode("actions");
        InspectorTitle = RightPanelText;
        PinRightPanelOpen();
    }

    public void CloseRightPanel()
    {
        IsRightPanelPeekOpen = false;
        IsInspectorVisible = false;
    }

    public void ExecuteRightPanelAction(string kind)
    {
        switch (kind)
        {
            case "review":
                OpenReviewPanel();
                break;
            case "terminal":
                OpenTerminalSidePanel();
                break;
            case "browser":
                OpenBrowserSidePanel();
                break;
            case "files":
                OpenFilesSidePanel();
                break;
            case "chat":
                OpenSideChatPanel();
                break;
        }
    }

    private void OpenTerminalSidePanel()
    {
        SetRightPanelMode("terminal");
        InspectorTitle = SideTerminalTitle;
        OnPropertyChanged(nameof(SideTerminalCwd));
        PinRightPanelOpen();
    }

    private void OpenBrowserSidePanel()
    {
        SetRightPanelMode("browser");
        InspectorTitle = SideBrowserTitle;
        PinRightPanelOpen();
    }

    private void OpenFilesSidePanel()
    {
        IsExplorerExpanded = true;
        RightPanelFileItems.Clear();
        foreach (var file in _files.Where(f => !f.IsDirectory).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Take(120))
        {
            RightPanelFileItems.Add(new FileExplorerNode(file.Name, file.Path, file.RelativePath, false, "file"));
        }
        SetRightPanelMode("files");
        InspectorTitle = SideFilesTitle;
        PinRightPanelOpen();
    }

    private void OpenSideChatPanel()
    {
        if (RightPanelChatRows.Count == 0)
        {
            RightPanelChatRows.Add(new PanelRow("side", L("临时侧边聊天", "Temporary side chat"), L("可直接提问，也可以从主对话消息带入上下文；不会打断当前主对话。", "Ask directly, or bring context from the main chat without interrupting it."), null, "message"));
        }
        SetRightPanelMode("chat");
        InspectorTitle = SideChatTitle;
        PinRightPanelOpen();
    }

    public void NewTemporarySideChat()
    {
        _sideChatCancellation?.Cancel();
        _sideChatSessionFile = null;
        SideChatContextText = string.Empty;
        SideChatInputText = string.Empty;
        RightPanelChatRows.Clear();
        RightPanelChatRows.Add(new PanelRow("side", L("新临时对话", "New temporary chat"), L("这个对话不会进入左侧列表。", "This chat will not be added to the sidebar."), null, "message"));
        OpenSideChatPanel();
    }

    public void CaptureSelectedChatText(string text)
    {
        var cleaned = text.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return;
        _selectedChatText = TrimForRow(cleaned, 6000);
        OnPropertyChanged(nameof(SelectedTextActionPreview));
        IsSelectedTextActionOpen = true;
    }

    public void AddSelectedTextToConversation()
    {
        if (string.IsNullOrWhiteSpace(_selectedChatText)) return;
        var quote = string.Join("\n", _selectedChatText.Split('\n').Select(line => $"> {line}"));
        PromptText = string.IsNullOrWhiteSpace(PromptText)
            ? quote + "\n\n"
            : PromptText.TrimEnd() + "\n\n" + quote + "\n\n";
        IsSelectedTextActionOpen = false;
    }

    public void AddRowToConversation(PanelRow row)
    {
        var text = RowContextText(row);
        if (string.IsNullOrWhiteSpace(text)) return;
        _selectedChatText = text;
        AddSelectedTextToConversation();
    }

    public void AskRowInSideChat(PanelRow row)
    {
        var text = RowContextText(row);
        if (string.IsNullOrWhiteSpace(text)) return;
        _selectedChatText = text;
        AskSelectedTextInSideChat();
    }

    public void EditFromHere(PanelRow row)
    {
        if (IsAgentRunning || row.Badge != "user" || row.Kind != "message") return;
        var index = IndexOfContentRow(row);
        if (index < 0) return;

        var text = RowContextText(row);
        if (string.IsNullOrWhiteSpace(text)) return;

        var branchFrom = row.Path;
        if (branchFrom is null && !string.IsNullOrWhiteSpace(_activeSessionFile))
        {
            var userOrdinal = UserOrdinalForRow(index);
            branchFrom = FindParentIdForNthUserMessage(_activeSessionFile, userOrdinal);
        }

        var ordinal = UserOrdinalForRow(index);
        _pendingEditFromRowIndex = index;
        _pendingEditUserOrdinal = ordinal;
        _pendingEditSessionFile = _activeSessionFile;
        _pendingBranchFromEntryId = branchFrom;
        PromptText = text;
        Suggestions.Clear();
        IsChatMode = true;
        IsComposerVisible = true;
        IsToolbarVisible = true;
        IsStartActionsVisible = false;
        CanReturnToChat = false;
        IsSelectedTextActionOpen = false;
        StatusText = L("正在编辑这条消息；发送后才会切换分支，清空输入可取消。", "Editing this message; the branch changes only after you send. Clear the composer to cancel.");
    }

    private void ClearPendingEditFromHere(bool updateStatus = true)
    {
        _pendingEditFromRowIndex = null;
        _pendingEditUserOrdinal = null;
        _pendingEditSessionFile = null;
        _pendingBranchFromEntryId = null;
        if (updateStatus) StatusText = L("已取消编辑", "edit cancelled");
    }

    public void NewSessionFromRow(PanelRow row)
    {
        if (IsAgentRunning || row.Kind != "message") return;
        var text = RowContextText(row);
        if (string.IsNullOrWhiteSpace(text)) return;
        CancelSessionLoad();
        _activeSessionFile = null;
        _pendingBranchFromEntryId = null;
        _pendingEditSessionFile = null;
        _pendingEditUserOrdinal = null;
        PromptText = text;
        ContentRows.Clear();
        ChatMarkers.Clear();
        Suggestions.Clear();
        IsChatMode = true;
        IsComposerVisible = true;
        IsToolbarVisible = true;
        IsStartActionsVisible = false;
        CanReturnToChat = false;
        StatusText = L("已复制到新会话输入框", "Copied into a new session composer");
    }

    public static string MessageTextForClipboard(PanelRow row) => RowContextText(row);

    private int IndexOfContentRow(PanelRow row)
    {
        for (var i = 0; i < ContentRows.Count; i++)
        {
            if (ReferenceEquals(ContentRows[i], row)) return i;
        }
        return ContentRows.IndexOf(row);
    }

    private int UserOrdinalForRow(int rowIndex)
    {
        var ordinal = 0;
        for (var i = 0; i <= rowIndex && i < ContentRows.Count; i++)
        {
            if (ContentRows[i].Badge == "user" && ContentRows[i].Kind == "message") ordinal++;
        }
        return ordinal;
    }

    private static string? FindParentIdForNthUserMessage(string sessionFile, int ordinal)
    {
        if (ordinal <= 0 || string.IsNullOrWhiteSpace(sessionFile) || !File.Exists(sessionFile)) return null;
        var seen = 0;
        foreach (var line in File.ReadLines(sessionFile))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"message\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var message)) continue;
                var role = TryGetJsonString(message, "role");
                if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) continue;
                seen++;
                if (seen == ordinal) return TryGetJsonString(root, "parentId") ?? string.Empty;
            }
            catch { }
        }
        return null;
    }

    private static string? TryGetJsonString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private void RebuildChatMarkersFromRows()
    {
        ChatMarkers.Clear();
        for (var i = 0; i < ContentRows.Count; i++)
        {
            var row = ContentRows[i];
            if (row.Badge != "user" || row.Kind != "message") continue;
            var markerTitle = row.DisplayTitle.Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(markerTitle)) continue;
            ChatMarkers.Add(new(ChatMarkers.Count + 1, TrimForRow(markerTitle, 220), i, IsEnglish));
        }
        RefreshChatMarkerDensity();
    }

    private static string RowContextText(PanelRow row)
    {
        var text = string.Join("\n\n", new[] { row.DisplayTitle, row.DisplayDetail }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        return TrimForRow(text, 6000);
    }

    public void AskSelectedTextInSideChat()
    {
        if (string.IsNullOrWhiteSpace(_selectedChatText)) return;
        _sideChatCancellation?.Cancel();
        _sideChatSessionFile = null;
        SideChatContextText = _selectedChatText;
        SideChatInputText = string.Empty;
        IsSelectedTextActionOpen = false;
        OpenSideChatPanel();
        RightPanelChatRows.Clear();
        RightPanelChatRows.Add(new PanelRow("context", SideChatContextLabel, TrimForRow(SideChatContextText, 900), null, "message"));
    }

    public async Task SendSideChatAsync()
    {
        if (IsSideChatRunning)
        {
            _sideChatCancellation?.Cancel();
            return;
        }
        var question = SideChatInputText.Trim();
        if (string.IsNullOrWhiteSpace(question)) return;

        SideChatInputText = string.Empty;
        RightPanelChatRows.Add(new PanelRow("user", question, "", null, "message"));
        IsSideChatRunning = true;
        _sideChatCancellation = new CancellationTokenSource();
        var token = _sideChatCancellation.Token;
        RightPanelChatRows.Add(new PanelRow("thinking", L("正在思考…", "Thinking…"), L("临时侧边聊天，不写入左侧列表", "Temporary side chat; not added to the sidebar"), "__side_chat_live__", "thinking"));

        try
        {
            var prompt = BuildSideChatPrompt(question);
            var result = await _agentBridge.RunPromptAsync(ResolveRunCwd(), _pi.AgentDir, prompt, AddSideChatBridgeEvent, _sideChatSessionFile, ThinkingLevel, null, "all", null, "default", null, provider: _activeProvider, model: _activeModel, cancellationToken: token);
            RemoveSideChatLiveRow();
            var answer = string.IsNullOrWhiteSpace(result.FinalText)
                ? L("没有返回文本。", "No text response.")
                : TrimForRow(result.FinalText, 6000);
            RightPanelChatRows.Add(new PanelRow("assistant", answer, "", null, "message"));
            if (!string.IsNullOrWhiteSpace(result.SessionFile)) _sideChatSessionFile = result.SessionFile;
            ArchiveTemporarySideChatSession(_sideChatSessionFile, question);
        }
        catch (OperationCanceledException)
        {
            RemoveSideChatLiveRow();
            RightPanelChatRows.Add(new PanelRow("state", L("侧边聊天已停止", "Side chat stopped"), "", null, "message"));
        }
        catch (Exception ex)
        {
            RemoveSideChatLiveRow();
            RightPanelChatRows.Add(new PanelRow("error", L("侧边聊天失败", "Side chat failed"), ex.Message, null, "message"));
        }
        finally
        {
            IsSideChatRunning = false;
            _sideChatCancellation?.Dispose();
            _sideChatCancellation = null;
        }
    }

    private string BuildSideChatPrompt(string question)
    {
        var context = string.IsNullOrWhiteSpace(SideChatContextText)
            ? string.Empty
            : L($"\n\n参考上下文：\n```text\n{SideChatContextText}\n```", $"\n\nReference context:\n```text\n{SideChatContextText}\n```");
        return L(
            $"你是 ipi 的临时侧边聊天子代理。不要修改文件，不要执行工具，不要影响主对话。用自然的人话简洁回答。{context}\n\n用户问题：\n{question}",
            $"You are ipi's temporary side-chat agent. Do not edit files, do not run tools, and do not affect the main conversation. Answer concisely in natural language.{context}\n\nUser question:\n{question}");
    }

    private void AddSideChatBridgeEvent(PiBridgeEvent evt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var detail = string.IsNullOrWhiteSpace(evt.Detail) ? evt.Label : evt.Detail;
            var index = RightPanelChatRows.ToList().FindIndex(r => r.Path == "__side_chat_live__");
            var row = new PanelRow("thinking", L("正在思考…", "Thinking…"), TrimForRow(detail, 180), "__side_chat_live__", "thinking");
            if (index >= 0) RightPanelChatRows[index] = row;
            else RightPanelChatRows.Add(row);
        });
    }

    private void RemoveSideChatLiveRow()
    {
        for (var i = RightPanelChatRows.Count - 1; i >= 0; i--)
        {
            if (RightPanelChatRows[i].Path == "__side_chat_live__") RightPanelChatRows.RemoveAt(i);
        }
    }

    private void ArchiveTemporarySideChatSession(string? sessionFile, string question)
    {
        if (string.IsNullOrWhiteSpace(sessionFile) || !File.Exists(sessionFile)) return;
        try
        {
            var info = new FileInfo(sessionFile);
            _archive.Archive(new PiSessionRecord(
                Path.GetFileNameWithoutExtension(sessionFile),
                sessionFile,
                ResolveRunCwd(),
                L("临时侧边聊天", "Temporary side chat"),
                info.CreationTime,
                info.LastWriteTime,
                0,
                question,
                _activeSessionFile));
        }
        catch { }
    }

    private void OpenReviewPanel()
    {
        var root = ResolveGitRoot(ActiveLocationPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            LoadInspector(L("审查", "Review"), new[]
            {
                new PanelRow("git", L("未找到 Git 仓库", "No git repository"), ActiveLocationPath),
            });
            return;
        }

        var branch = RunGit(root, "branch", "--show-current").Output.Trim();
        if (string.IsNullOrWhiteSpace(branch)) branch = RunGit(root, "rev-parse", "--short", "HEAD").Output.Trim();
        var status = RunGit(root, "status", "--short");
        var rows = new List<PanelRow>
        {
            new("repo", ShortenPath(root)),
            new("branch", string.IsNullOrWhiteSpace(branch) ? "detached" : branch),
        };
        var changed = status.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(24).ToList();
        rows.Add(new("changes", changed.Count == 0 ? L("工作区干净", "Clean working tree") : L($"{changed.Count} 个变更", $"{changed.Count} changes")));
        foreach (var line in changed)
        {
            rows.Add(new("file", line.Length > 3 ? line[3..] : line, line.Length >= 2 ? line[..2].Trim() : ""));
        }
        LoadInspector(L("审查", "Review"), rows);
    }

    public async Task RunSideTerminalCommandAsync()
    {
        var command = SideTerminalCommand.Trim();
        if (string.IsNullOrWhiteSpace(command)) return;
        IsSideTerminalRunning = true;
        SideTerminalOutput = L("正在运行…", "Running…");
        try
        {
            var output = await Task.Run(() => RunShellForSidePanel(CurrentRunDirectory, command));
            SideTerminalOutput = output;
        }
        catch (Exception ex)
        {
            SideTerminalOutput = ex.Message;
        }
        finally
        {
            IsSideTerminalRunning = false;
        }
    }

    private static string RunShellForSidePanel(string cwd, string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start shell");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "Command timed out after 30s.";
        }
        var result = new StringBuilder();
        result.AppendLine($"> {command}");
        result.AppendLine($"exit {process.ExitCode}");
        if (!string.IsNullOrWhiteSpace(stdout)) result.AppendLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) result.AppendLine(stderr.TrimEnd());
        return result.ToString().TrimEnd();
    }

    public string NormalizeSideBrowserUrl()
    {
        var url = SideBrowserUrl.Trim();
        if (string.IsNullOrWhiteSpace(url)) return "about:blank";
        if (url.Contains("://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return url;
        return "https://" + url;
    }

    public void NavigateSideBrowser(string? url = null)
    {
        if (!string.IsNullOrWhiteSpace(url)) SideBrowserUrl = url.Trim();
        var normalized = NormalizeSideBrowserUrl();
        SideBrowserUrl = normalized;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) SideBrowserUri = uri;
        OpenBrowserSidePanel();
        StatusText = $"browser · {normalized}";
    }

    public void OpenMessageTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        var value = target.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            NavigateSideBrowser(value);
            return;
        }

        var path = ResolveMessageTargetPath(value);
        if (path is not null && File.Exists(path))
        {
            OpenFileInRightPanel(path, MakeDisplayPath(path));
            return;
        }

        if (path is not null && Directory.Exists(path))
        {
            LoadFilesPage(path);
            SetRightPanelMode("files");
            PinRightPanelOpen();
            StatusText = $"opened folder · {MakeDisplayPath(path)}";
            return;
        }

        StatusText = L($"找不到链接目标 · {value}", $"target not found · {value}");
    }

    private string? ResolveMessageTargetPath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"', '\'', '`'));
        if (Path.IsPathRooted(expanded)) return Path.GetFullPath(expanded);
        var bases = new[] { ActiveLocationPath, ProjectPath, CurrentRunDirectory };
        foreach (var root in bases.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.GetFullPath(Path.Combine(root, expanded.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string MakeDisplayPath(string path)
    {
        foreach (var root in new[] { ActiveLocationPath, ProjectPath }.Where(Directory.Exists))
        {
            if (IsUnderDirectory(path, root)) return NormalizeRelativePath(Path.GetRelativePath(root, path));
        }
        return path;
    }

    private static bool IsUnderDirectory(string path, string root)
    {
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

    public void SetFilePanelMode(bool raw)
    {
        IsFilePanelRawMode = raw;
    }

    public void SaveFilePanel()
    {
        if (string.IsNullOrWhiteSpace(_selectedExplorerFile) || !CanEditFilePanel) return;
        try
        {
            File.WriteAllText(_selectedExplorerFile, FilePanelText);
            var info = new FileInfo(_selectedExplorerFile);
            FilePanelMeta = BuildFileMeta(info);
            FilePanelStatus = L("已保存", "saved");
            RefreshExplorerTree();
        }
        catch (Exception ex)
        {
            FilePanelStatus = ex.Message;
        }
    }

    private void OpenFileInRightPanel(string path, string relativePath)
    {
        _selectedExplorerFile = path;
        SetRightPanelMode("file");
        PinRightPanelOpen();
        FilePanelTitle = Path.GetFileName(path);
        FilePanelPath = relativePath;
        FilePanelStatus = File.Exists(path) ? "live" : L("缺失", "missing");
        CanEditFilePanel = File.Exists(path) && !IsSensitivePath(path) && IsTextLikeFile(path) && new FileInfo(path).Length <= 512 * 1024;
        IsFilePanelRawMode = false;
        if (!File.Exists(path))
        {
            FilePanelText = L("文件不存在。", "File not found.");
            FilePanelMeta = "missing";
            return;
        }

        var info = new FileInfo(path);
        FilePanelMeta = BuildFileMeta(info);
        if (CanEditFilePanel)
        {
            try { FilePanelText = File.ReadAllText(path); }
            catch (Exception ex) { FilePanelText = ex.Message; CanEditFilePanel = false; }
        }
        else
        {
            FilePanelText = _pi.ReadTextPreview(path);
        }
        StatusText = $"opened file · {relativePath}";
    }

    private string BuildFileMeta(FileInfo info)
    {
        var ext = string.IsNullOrWhiteSpace(info.Extension) ? L("文件", "file") : info.Extension.TrimStart('.');
        var lines = 0;
        if (IsTextLikeFile(info.FullName) && info.Length <= 512 * 1024)
        {
            try { lines = File.ReadLines(info.FullName).Count(); } catch { }
        }
        return lines > 0 ? $"{ext} · {lines} lines · {FormatSize(info.Length)}" : $"{ext} · {FormatSize(info.Length)}";
    }

    private static int DepthOf(string relativePath)
        => NormalizeRelativePath(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');

    private static string ParentRelativeKey(string key)
    {
        var index = key.LastIndexOf('/');
        return index <= 0 ? string.Empty : key[..index];
    }

    private static bool IsSensitivePath(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (name.StartsWith(".env")) return true;
        if (name.Contains("mnemonic") || name.Contains("seed") || name.Contains("wallet")) return true;
        if (name is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519") return true;
        return ext is ".pem" or ".key" or ".p12" or ".pfx";
    }

    private static bool IsTextLikeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) return true;
        return ext is ".txt" or ".md" or ".markdown" or ".json" or ".jsonl" or ".cs" or ".xaml" or ".xml" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".css" or ".scss" or ".html" or ".yml" or ".yaml" or ".toml" or ".ini" or ".ps1" or ".bat" or ".cmd" or ".sh" or ".py" or ".rs" or ".go" or ".java" or ".cpp" or ".c" or ".h" or ".hpp" or ".gitignore";
    }

    private SessionItem ToSidebarSession(PiSessionRecord session)
        => new("message-square", session.Title, false, session.FilePath, FormatAge(session.Modified), session.MessageCount);

    private void LoadSidebarConfig((string DefaultProvider, string DefaultModel, string DefaultThinking) settings)
    {
        ConfigItems.Clear();
    }

    private void LoadHomeInspector()
    {
        LoadInspector("Local Pi", new[]
        {
            new PanelRow("agent dir", _pi.AgentDir),
            new PanelRow("sessions", _sessions.Count.ToString()),
            new PanelRow("skills", _skills.Count.ToString()),
            new PanelRow("packages", _packages.Count.ToString()),
            new PanelRow("workspace files", _files.Count.ToString()),
            new PanelRow("next", "connect local agent prompt/events")
        });
    }

    private static Task SwitchToUiAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) return Task.CompletedTask;
        return dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task;
    }

    private void AddOrUpdateLiveStatus(string title, string detail = "")
    {
        _liveStatusTitle = title;
        _liveStatusDetail = TrimForRow(detail.Replace("\r", " ").Replace("\n", " ").Trim(), 260);
        UpdateLiveStatusRow();
    }

    private void UpdateLiveStatusRow()
    {
        if (string.IsNullOrWhiteSpace(_liveStatusTitle)) return;
        var title = "thinking";
        var detail = IsAgentRunning && !string.IsNullOrWhiteSpace(_liveStatusDetail)
            ? $"{FormatRunElapsed()} · {_liveStatusDetail}"
            : _liveStatusDetail;
        var row = new PanelRow("thinking", title, detail, LiveStatusPath, "thinking");
        var index = ContentRows.ToList().FindIndex(r => r.Path == LiveStatusPath);
        if (index >= 0) ContentRows[index] = row;
        else ContentRows.Add(row);
        RequestScrollChatToLatest?.Invoke();
    }

    private string FormatRunElapsed()
    {
        var elapsed = DateTime.Now - _runStartedAt;
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours:0}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private async Task RevealAssistantMessageAsync(string text, CancellationToken cancellationToken)
    {
        var index = ContentRows.Count;
        ContentRows.Add(new("assistant", "", "", null, "message"));
        RequestScrollChatToLatest?.Invoke();

        var visible = new StringBuilder();
        var cursor = 0;
        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = NextRevealChunk(text, cursor);
            visible.Append(text.AsSpan(cursor, chunk));
            cursor += chunk;
            if (index < ContentRows.Count) ContentRows[index] = new PanelRow("assistant", visible.ToString(), "", null, "message");
            await Task.Delay(RevealDelayFor(text.Length), cancellationToken);
        }
        RequestScrollChatToLatest?.Invoke();
    }

    private static int NextRevealChunk(string text, int cursor)
    {
        var remaining = text.Length - cursor;
        var baseSize = text.Length switch
        {
            <= 600 => 1,
            <= 1800 => 3,
            <= 4200 => 5,
            _ => 8,
        };
        var size = Math.Min(baseSize, remaining);

        // Let short phrases finish cleanly, but avoid dumping a whole paragraph in one UI frame.
        var phraseLimit = Math.Min(18, remaining);
        for (var i = 0; i < phraseLimit; i++)
        {
            var ch = text[cursor + i];
            if (ch is '\n' or '。' or '！' or '？' or '.' or '!' or '?' or ';' or '；' or ',' or '，') return Math.Max(size, i + 1);
        }
        return size;
    }

    private static int RevealDelayFor(int length) => length switch
    {
        <= 600 => 42,
        <= 1800 => 34,
        <= 4200 => 28,
        _ => 22,
    };

    private void RemoveLiveStatusRows()
    {
        _liveStatusTitle = string.Empty;
        _liveStatusDetail = string.Empty;
        for (var i = ContentRows.Count - 1; i >= 0; i--)
        {
            if (ContentRows[i].Path == LiveStatusPath) ContentRows.RemoveAt(i);
        }
    }

    private (string Title, string Detail) FormatBridgeStatus(PiBridgeEvent evt)
    {
        if (evt.Kind == "tool")
        {
            var action = string.IsNullOrWhiteSpace(evt.Detail) ? evt.Label : $"{evt.Label} · {evt.Detail}";
            return (L("正在思考…", "Thinking…"), action);
        }

        return evt.EventType switch
        {
            "ready" => (L("正在准备回复…", "Preparing reply…"), evt.Detail),
            "agent_start" => (L("正在准备回复…", "Preparing reply…"), ""),
            "agent_end" => (L("正在整理回答…", "Preparing final answer…"), evt.Detail),
            "auto_retry_start" => (L("正在重试…", "Retrying…"), evt.Detail),
            "compaction_start" => (L("正在压缩上下文…", "Compacting context…"), evt.Detail),
            "compaction_end" => (L("上下文压缩完成", "Context compacted"), evt.Detail),
            _ => (L("正在思考…", "Thinking…"), string.IsNullOrWhiteSpace(evt.Detail) ? evt.Label : evt.Detail),
        };
    }

    private void AddBridgeEvent(PiBridgeEvent evt) => AddBridgeEvent(_runVersion, evt);

    private void AddBridgeEvent(int runVersion, PiBridgeEvent evt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!IsCurrentRun(runVersion)) return;
            var (title, detail) = FormatBridgeStatus(evt);
            if (evt.Kind == "error")
            {
                RemoveLiveStatusRows();
                ContentRows.Add(new("error", title, detail, null, "error"));
            }
            else if (evt.Kind == "tool")
            {
                AddOrUpdateLiveStatus(title, detail);
            }
            else
            {
                AddOrUpdateLiveStatus(title, detail);
            }

            StatusText = title;
        });
    }

    private void LoadRunInspector(string state)
    {
        LoadInspector("Run", new[]
        {
            new PanelRow("state", state),
            new PanelRow("model", ModelLabel),
            new PanelRow("tools", ToolLabel),
            new PanelRow("approval", ApprovalLabel)
        });
    }

    private void LoadInspector(string title, IEnumerable<PanelRow> rows)
    {
        SetRightPanelMode("inspector");
        InspectorTitle = title;
        PinRightPanelOpen();
        InspectorRows.Clear();
        foreach (var row in rows) InspectorRows.Add(row);
    }

    private static string FindWorkspacePath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("IPI_WORKSPACE_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

        var configured = LoadLastWorkspacePath();
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        var dir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json")) && Directory.Exists(Path.Combine(current.FullName, "packages"))) return current.FullName;
            current = current.Parent;
        }

        Directory.CreateDirectory(GlobalChatRoot);
        return GlobalChatRoot;
    }

    private static string? LoadLastWorkspacePath()
    {
        try
        {
            if (!File.Exists(WorkspaceConfigPath)) return null;
            var settings = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(WorkspaceConfigPath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return settings?.LastProjectPath;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLastWorkspacePath(string path)
    {
        try
        {
            Directory.CreateDirectory(AppDataRoot);
            var json = JsonSerializer.Serialize(new WorkspaceSettings(path), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(WorkspaceConfigPath, json, Encoding.UTF8);
        }
        catch
        {
            // Ignore local UI preference persistence failures.
        }
    }

    private void LoadUiSettings()
    {
        try
        {
            if (!File.Exists(UiSettingsPath)) return;
            var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiSettingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var approvalIndex = Array.FindIndex(_approvalModeKeys, key => key.Equals(settings?.ApprovalModeKey, StringComparison.OrdinalIgnoreCase));
            if (approvalIndex >= 0) _approvalModeIndex = approvalIndex;
            var toolIndex = Array.FindIndex(_toolPresets, preset => preset.Key.Equals(settings?.ToolPresetKey, StringComparison.OrdinalIgnoreCase));
            if (toolIndex >= 0) _toolPresetIndex = toolIndex;
        }
        catch
        {
            // Ignore local UI preference persistence failures.
        }
    }

    private void SaveUiSettings()
    {
        try
        {
            Directory.CreateDirectory(AppDataRoot);
            var settings = new UiSettings(_approvalModeKeys[Math.Clamp(_approvalModeIndex, 0, _approvalModeKeys.Length - 1)], CurrentToolPreset.Key);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(UiSettingsPath, json, Encoding.UTF8);
        }
        catch
        {
            // Ignore local UI preference persistence failures.
        }
    }

    private sealed record WorkspaceSettings(string? LastProjectPath);
    private sealed record UiSettings(string? ApprovalModeKey, string? ToolPresetKey);

    private string ResolveRunCwd()
    {
        if (!string.IsNullOrWhiteSpace(_activeCwd) && Directory.Exists(_activeCwd)) return _activeCwd;
        if (IsGlobalChat)
        {
            Directory.CreateDirectory(GlobalChatRoot);
            return GlobalChatRoot;
        }
        return Directory.Exists(ProjectPath) ? ProjectPath : GlobalChatRoot;
    }

    private static bool IsGlobalCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return true;
        var normalized = NormalizePath(cwd);
        if (string.IsNullOrWhiteSpace(normalized)) return true;
        if (PathsEqual(normalized, GlobalChatRoot)) return true;
        var home = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (PathsEqual(normalized, home)) return true;
        var leaf = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return leaf.StartsWith("pi-cwd-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var l = NormalizePath(left);
        var r = NormalizePath(right);
        return !string.IsNullOrWhiteSpace(l) && !string.IsNullOrWhiteSpace(r) && string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    private static bool IsSameOrChildPath(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return c.Equals(r, StringComparison.OrdinalIgnoreCase) || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ShortenPath(string path) => string.IsNullOrWhiteSpace(path) ? "" : path.Length <= 42 ? path : $"...{path[^39..]}";
    private static string TrimForRow(string text, int max) => string.IsNullOrWhiteSpace(text) || text.Length <= max ? text : text[..max] + "…";
    private static string NormalizeThinking(string thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking)) return "medium";
        return thinking.Equals("xhigh", StringComparison.OrdinalIgnoreCase) ? "xhigh" : thinking.ToLowerInvariant();
    }
    private static string ShortModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "model";
        var compact = model.Replace("gpt-", "", StringComparison.OrdinalIgnoreCase).Replace("claude-", "", StringComparison.OrdinalIgnoreCase);
        return compact.Length > 12 ? compact[..12] : compact;
    }

    private string FormatThinking(string thinking) => thinking switch
    {
        "low" => L("低", "low"),
        "high" => L("高", "high"),
        "xhigh" => L("超高", "ultra"),
        _ => L("中", "med")
    };

    private static string PrettyModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "Model";
        return model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            ? "GPT-" + model[4..]
            : model;
    }

    private SessionUsageSummary AggregateUsage(IEnumerable<PiSessionRecord> sessions)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0;
        double cost = 0;
        foreach (var session in sessions)
        {
            var usage = _pi.ReadSessionUsageSummary(session.FilePath);
            input += usage.Input;
            output += usage.Output;
            cacheRead += usage.CacheRead;
            cacheWrite += usage.CacheWrite;
            if (totalTokens == 0 && usage.TotalTokens > 0) totalTokens = usage.TotalTokens;
            cost += usage.Cost;
        }
        return new SessionUsageSummary(input, output, cacheRead, cacheWrite, totalTokens, cost);
    }

    private void SetTopUsage(SessionUsageSummary usage)
    {
        _currentUsage = usage;
        TopStatsText = FormatTopUsage(usage);
        TopInputText = FormatCompactNumber(usage.Input);
        TopOutputText = FormatCompactNumber(usage.Output);
        TopCacheText = FormatCompactNumber(usage.CacheRead);
        TopCostText = $"${usage.Cost:F2}";
        var contextTokens = usage.TotalTokens > 0 ? usage.TotalTokens : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
        if (_contextLimitTokens is > 0)
        {
            var percent = Math.Min(999, (int)Math.Round(contextTokens * 100.0 / _contextLimitTokens.Value));
            TopContextText = $"{percent}% / {FormatCompactNumber(_contextLimitTokens.Value)}";
            TopContextFillWidth = Math.Clamp(percent, 0, 100) * 0.3;
        }
        else
        {
            TopContextText = $"{FormatCompactNumber(contextTokens)} tok";
            TopContextFillWidth = 0;
        }
        OnPropertyChanged(nameof(TopInputText));
        OnPropertyChanged(nameof(TopOutputText));
        OnPropertyChanged(nameof(TopCacheText));
        OnPropertyChanged(nameof(TopCostText));
        OnPropertyChanged(nameof(TopContextText));
        OnPropertyChanged(nameof(TopContextFillWidth));
    }

    private string FormatTopUsage(SessionUsageSummary usage)
    {
        if (usage.TotalTokens <= 0 && usage.Input <= 0 && usage.Output <= 0 && usage.CacheRead <= 0)
        {
            return _contextLimitTokens is > 0 ? $"in 0  out 0  cache 0  $0.00  0% / {FormatCompactNumber(_contextLimitTokens.Value)}" : "in 0  out 0  cache 0  $0.00  0 tok";
        }
        var contextTokens = usage.TotalTokens > 0 ? usage.TotalTokens : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
        var context = _contextLimitTokens is > 0
            ? $"{Math.Min(999, (int)Math.Round(contextTokens * 100.0 / _contextLimitTokens.Value))}% / {FormatCompactNumber(_contextLimitTokens.Value)}"
            : $"{FormatCompactNumber(contextTokens)} tok";
        return $"in {FormatCompactNumber(usage.Input)}  out {FormatCompactNumber(usage.Output)}  cache {FormatCompactNumber(usage.CacheRead)}  ${usage.Cost:F2}  {context}";
    }

    private static bool LooksLikeGitRepository(string path)
    {
        try
        {
            var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            while (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) return true;
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private string? ResolveGitRoot(string path)
    {
        var start = path;
        if (string.IsNullOrWhiteSpace(start)) start = ProjectPath;
        if (File.Exists(start)) start = Path.GetDirectoryName(start) ?? ProjectPath;
        if (!Directory.Exists(start)) start = ProjectPath;
        if (!Directory.Exists(start)) return null;

        var result = RunGit(start, "rev-parse", "--show-toplevel");
        if (result.ExitCode != 0) return null;
        var root = result.Output.Trim();
        return Directory.Exists(root) ? root : null;
    }

    private static GitCommandResult RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return new GitCommandResult(-1, "", "git failed to start");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new GitCommandResult(-1, output, "git command timed out");
            }
            return new GitCommandResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new GitCommandResult(-1, "", ex.Message);
        }
    }

    private static int CountNonEmptyLines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string FormatCompactNumber(long value)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_000_000) return $"{value / 1_000_000.0:F1}M";
        if (abs >= 1_000) return $"{value / 1_000.0:F0}k";
        return value.ToString("N0");
    }

    private string FormatAge(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return L("刚刚", "now");
        if (span.TotalHours < 1)
        {
            var minutes = Math.Max(1, (int)span.TotalMinutes);
            return IsEnglish ? $"{minutes}m" : $"{minutes} 分钟";
        }
        if (span.TotalDays < 1)
        {
            var hours = Math.Max(1, (int)span.TotalHours);
            return IsEnglish ? $"{hours}h" : $"{hours} 小时";
        }
        if (span.TotalDays < 14)
        {
            var days = Math.Max(1, (int)span.TotalDays);
            return IsEnglish ? $"{days}d" : $"{days} 天";
        }
        return time.ToString("M/d");
    }
    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes / 1024.0 / 1024.0:F1} MB";
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record NavItem(string Icon, string Label, string Kind = "");
public sealed record ProjectItem(string Name, string Path, string Detail, string RootPath);
public sealed record AttachmentItem(string Name, string Path, string Detail)
{
    public bool IsImage => AttachmentImageHelper.IsImageFile(Path);
    public string PreviewPath => Path;
    public Visibility ImageVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileVisibility => IsImage ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record ChatAttachmentPreviewItem(string Name, string Path, string Detail)
{
    public bool IsImage => AttachmentImageHelper.IsImageFile(Path) && File.Exists(Path);
    public string PreviewPath => Path;
    public Visibility ImageVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileVisibility => IsImage ? Visibility.Collapsed : Visibility.Visible;
}

public static class AttachmentImageHelper
{
    public static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
    }
}
public sealed record SessionLoadSnapshot(IReadOnlyList<PiTimelineRecord> Timeline, SessionUsageSummary Usage, int MessageCount, DateTime Modified);
public sealed record MemorySystemInfo(string Name, string Status, string Evidence, IReadOnlyList<SkillRecord> Skills);

public sealed class SystemContextSection
{
    public SystemContextSection(string title, string detail, string text, bool isExpanded)
    {
        Title = title;
        Detail = detail;
        Text = text;
        IsExpanded = isExpanded;
    }

    public string Title { get; }
    public string Detail { get; }
    public string Text { get; }
    public bool IsExpanded { get; set; }
}
public sealed record BranchItem(string Name, string Detail, bool IsCurrent)
{
    public string SelectedMark => IsCurrent ? "✓" : "";
}
public sealed record GitCommandResult(int ExitCode, string Output, string Error);
public sealed record VoiceOptionItem(string Key, string Title, string Detail, string Badge, bool IsAvailable);
public sealed record ApprovalOptionItem(string Icon, string Title, string Detail, string SelectedMark, bool IsEnabled = true, bool RequiresConfigChoice = false);
public sealed record ReasoningOptionItem(string Key, string Label, string SelectedMark);
public sealed record ModelOptionItem(string Provider, string Model, string Label, string ProviderLabel, string Source, string SelectedMark)
{
    public string Detail => string.IsNullOrWhiteSpace(ProviderLabel) ? Source : $"{ProviderLabel} · {Source}";
}
public sealed class ChatMarkerItem : INotifyPropertyChanged
{
    private double _visualWidth = 10;
    private double _visualOpacity = 0.38;
    private double _slotHeight = 42;
    private Brush _visualBrush = new SolidColorBrush(Color.FromRgb(112, 116, 123));

    public ChatMarkerItem(int number, string title, int rowIndex, bool english = false)
    {
        Number = number;
        Title = title;
        RowIndex = rowIndex;
        NumberText = english ? $"User message {number}" : $"第 {number} 次用户发言";
    }

    public int Number { get; }
    public string Title { get; }
    public string NumberText { get; }
    public int RowIndex { get; }

    public double VisualWidth { get => _visualWidth; set { _visualWidth = value; OnPropertyChanged(); } }
    public double VisualOpacity { get => _visualOpacity; set { _visualOpacity = value; OnPropertyChanged(); } }
    public double SlotHeight { get => _slotHeight; set { _slotHeight = value; OnPropertyChanged(); } }
    public Brush VisualBrush { get => _visualBrush; set { _visualBrush = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
public enum ToolApprovalDecisionKind
{
    Deny,
    Allow,
    AlwaysAllow,
}

public sealed class ToolApprovalRequestItem : INotifyPropertyChanged
{
    private string _guidance = string.Empty;

    public ToolApprovalRequestItem(
        string approvalId,
        string toolName,
        string intent,
        string summary,
        string detail,
        string requestLabel,
        string allowText,
        string alwaysAllowText,
        string denyText,
        string guidancePlaceholder,
        TaskCompletionSource<PiToolApprovalDecision> decision)
    {
        ApprovalId = approvalId;
        ToolName = toolName;
        Intent = intent;
        Summary = summary;
        Detail = detail;
        RequestLabel = requestLabel;
        AllowText = allowText;
        AlwaysAllowText = alwaysAllowText;
        DenyText = denyText;
        GuidancePlaceholder = guidancePlaceholder;
        Decision = decision;
    }

    public string ApprovalId { get; }
    public string ToolName { get; }
    public string Intent { get; }
    public string Summary { get; }
    public string Detail { get; }
    public string RequestLabel { get; }
    public string AllowText { get; }
    public string AlwaysAllowText { get; }
    public string DenyText { get; }
    public string GuidancePlaceholder { get; }
    public TaskCompletionSource<PiToolApprovalDecision> Decision { get; }
    public string Guidance { get => _guidance; set { _guidance = value; OnPropertyChanged(); OnPropertyChanged(nameof(GuidancePlaceholderVisibility)); } }
    public Visibility GuidancePlaceholderVisibility => string.IsNullOrWhiteSpace(Guidance) ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
public sealed class PluginPackageViewItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public PluginPackageViewItem(PluginPackageRecord package, bool english)
    {
        Source = package.Source;
        Scope = package.Scope;
        Disabled = package.Disabled;
        PackageName = package.PackageName;
        Version = package.Version;
        InstalledPath = package.InstalledPath;
        Resources = package.Resources;
        Title = string.IsNullOrWhiteSpace(package.PackageName) ? package.Source : package.PackageName;
        IsInstalled = !string.IsNullOrWhiteSpace(package.InstalledPath) && Directory.Exists(package.InstalledPath) && File.Exists(Path.Combine(package.InstalledPath, "package.json"));
        HasResolvedResources = package.Resources.Count > 0;
        if (package.Disabled)
        {
            StatusText = english ? "Disabled" : "已禁用";
            StatusBrush = StatusBackground(142, 151, 166);
            StatusForeground = StatusForegroundBrush(126, 137, 154);
        }
        else if (!IsInstalled)
        {
            StatusText = english ? "Missing" : "未安装";
            StatusBrush = StatusBackground(196, 128, 86);
            StatusForeground = StatusForegroundBrush(179, 111, 71);
        }
        else if (!HasResolvedResources)
        {
            StatusText = english ? "Installed" : "已安装";
            StatusBrush = StatusBackground(102, 113, 132);
            StatusForeground = StatusForegroundBrush(102, 113, 132);
        }
        else
        {
            StatusText = english ? "Loaded" : "已加载";
            StatusBrush = StatusBackground(75, 111, 234);
            StatusForeground = StatusForegroundBrush(75, 111, 234);
        }
        VersionText = !IsInstalled ? (english ? "not installed" : "未安装") : string.IsNullOrWhiteSpace(package.Version) ? (english ? "installed" : "已安装") : english ? $"installed {package.Version}" : $"已安装 {package.Version}";
        var extensions = package.Resources.Count(resource => resource.Kind == "extension");
        var skills = package.Resources.Count(resource => resource.Kind == "skill");
        var prompts = package.Resources.Count(resource => resource.Kind == "prompt");
        var themes = package.Resources.Count(resource => resource.Kind == "theme");
        ResourceSummary = english
            ? $"{extensions} ext · {skills} skills" + (prompts > 0 || themes > 0 ? $" · {prompts} prompts · {themes} themes" : "")
            : $"{extensions} 扩展 · {skills} 技能" + (prompts > 0 || themes > 0 ? $" · {prompts} 提示 · {themes} 主题" : "");
    }

    public string Source { get; }
    public string Scope { get; }
    public bool Disabled { get; }
    public bool IsNpmPackage => Source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase);
    public string PackageName { get; }
    public string Version { get; }
    public string VersionText { get; }
    public string InstalledPath { get; }
    public bool IsInstalled { get; }
    public bool HasResolvedResources { get; }
    public IReadOnlyList<PluginResourceRecord> Resources { get; }
    public string Title { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public Brush StatusForeground { get; }
    public string ResourceSummary { get; }
    private static Brush StatusBackground(byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(28, r, g, b));
    private static Brush StatusForegroundBrush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
    public string ScopeText => Scope;
    public string Detail => $"{ScopeText} · {ResourceSummary} · {VersionText}";
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PluginResourceGroupViewItem
{
    public PluginResourceGroupViewItem(string title, IEnumerable<PluginResourceViewItem> items)
    {
        Title = title;
        Items = new ObservableCollection<PluginResourceViewItem>(items);
    }
    public string Title { get; }
    public ObservableCollection<PluginResourceViewItem> Items { get; }
}

public sealed record PluginResourceViewItem(string Name, string Path);

public sealed record ToolRunConfig(IReadOnlyList<string>? Tools, string? NoTools, string Description);
public sealed record ToolPresetOption(string Key, string Label, string Description, IReadOnlyList<string>? Tools, string? NoTools);
public sealed record ProjectGroupItem(string Name, string Path, string RootPath, ObservableCollection<SessionItem> Sessions, bool CanCreateWorktree = false);
public sealed record SessionItem(string Icon, string Title, bool Active, string? FilePath, string Detail, int MessageCount);

public sealed class FileExplorerNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    public FileExplorerNode(string name, string path, string relativePath, bool isDirectory, string icon)
    {
        Name = name;
        Path = path;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        Icon = icon;
    }

    public string Name { get; }
    public string Path { get; }
    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public string Icon { get; }
    public ObservableCollection<FileExplorerNode> Children { get; } = new();
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ExplorerItem(string Icon, string Name, string Path, string RelativePath, bool IsDirectory);
public sealed record BottomNavItem(string Icon, string Label, string Kind);
public sealed record ToolbarActionItem(string Icon, string Label, string Kind);
public sealed record QuickActionItem(string Label, string Kind);
public sealed record RightPanelActionItem(string Icon, string Label, string Shortcut, string Kind)
{
    public Visibility ShortcutVisibility => string.IsNullOrWhiteSpace(Shortcut) ? Visibility.Collapsed : Visibility.Visible;
}
public sealed record ConfigItem(string Name, string Value, string Kind = "info");
public sealed record RuntimeDiagnosticItem(string Level, string Title, string Detail);
public sealed record SuggestionItem(string Label, string Detail, string Command);
public sealed record ChatMessageRunItem(string Text, string? Target = null)
{
    public bool IsLink => !string.IsNullOrWhiteSpace(Target);
    public Visibility TextVisibility => IsLink ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LinkVisibility => IsLink ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record ChatMessageLineItem(string Text, bool IsBullet)
{
    private static readonly Regex LinkCandidateRegex = new(@"https?://[^\s)\]>]+|[A-Za-z]:\\[^\s)\]>]+|(?:\.?\.?[/\\])?[-\w .()@]+[/\\][-\w .()@/\\]+|[-\w .()@]+\.(?:md|txt|json|jsonl|cs|xaml|js|ts|tsx|jsx|py|mjs|css|html|toml|yaml|yml|xml|csproj)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Visibility BulletVisibility => IsBullet ? Visibility.Visible : Visibility.Collapsed;
    public Thickness TextMargin => IsBullet ? new Thickness(8, 0, 0, 0) : new Thickness(0);
    public IReadOnlyList<ChatMessageRunItem> Runs => BuildRuns(Text);

    private static IReadOnlyList<ChatMessageRunItem> BuildRuns(string text)
    {
        if (string.IsNullOrEmpty(text)) return new[] { new ChatMessageRunItem(text) };
        var runs = new List<ChatMessageRunItem>();
        var index = 0;
        foreach (Match match in LinkCandidateRegex.Matches(text))
        {
            if (match.Index > index) runs.Add(new ChatMessageRunItem(text[index..match.Index]));
            var raw = match.Value;
            var trimmed = raw.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            var trailing = raw[trimmed.Length..];
            if (!string.IsNullOrWhiteSpace(trimmed)) runs.Add(new ChatMessageRunItem(trimmed, trimmed));
            if (!string.IsNullOrEmpty(trailing)) runs.Add(new ChatMessageRunItem(trailing));
            index = match.Index + match.Length;
        }
        if (index < text.Length) runs.Add(new ChatMessageRunItem(text[index..]));
        return runs.Count == 0 ? new[] { new ChatMessageRunItem(text) } : runs;
    }
}

public sealed record PanelRow(string Badge, string Title, string Detail = "", string? Path = null, string Kind = "info", IReadOnlyList<ChatAttachmentPreviewItem>? Attachments = null)
{
    public bool IsUsage => Kind == "usage";
    public bool IsExpandable => Kind is "tool" or "system" or "error";
    public Visibility MessageVisibility => !IsExpandable && !IsUsage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UsageVisibility => IsUsage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpandableVisibility => IsExpandable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainVisibility => MessageVisibility;
    public Visibility AssistantHeaderVisibility => Kind == "message" && Badge == "assistant" && !string.IsNullOrWhiteSpace(DisplayTitle) ? Visibility.Visible : Visibility.Collapsed;
    public string AssistantHeaderText => "GPT-5.5";
    public Thickness RowMargin => Kind == "message" ? Badge == "assistant" ? new Thickness(0, 14, 0, 8) : new Thickness(0, 4, 0, 14) : Kind == "usage" ? new Thickness(0, 0, 0, 10) : IsExpandable ? new Thickness(0, 6, 0, 12) : new Thickness(0, 0, 0, 8);
    public bool IsChatTurn => Kind == "message" && Badge is "assistant" or "user";
    public bool CanUseAsSideContext => IsChatTurn;
    public bool CanEditFromHere => Kind == "message" && Badge == "user";
    public Visibility EditFromHereVisibility => CanEditFromHere ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SideContextActionVisibility => CanEditFromHere ? Visibility.Collapsed : CanUseAsSideContext ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UserMessageActionsVisibility => CanEditFromHere ? Visibility.Visible : Visibility.Collapsed;
    public GridLength BadgeColumnWidth => IsChatTurn || Kind == "thinking" ? new GridLength(0) : new GridLength(72);
    public GridLength ActionColumnWidth => CanEditFromHere ? new GridLength(0) : CanUseAsSideContext ? new GridLength(54) : new GridLength(0);
    public Thickness ChatContentMargin => IsChatTurn || Kind == "thinking" ? new Thickness(44, 0, 0, 0) : new Thickness(0);
    public string DisplayTitle => Badge == "assistant" && Kind == "message" ? HumanizeAssistantText(Title) : Badge == "user" && Kind == "message" ? StripAttachmentBlock(Title) : Title;
    public Visibility AssistantMessageTextVisibility => Badge == "assistant" && Kind == "message" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainMessageTextVisibility => Badge == "assistant" && Kind == "message" ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<ChatMessageLineItem> AssistantLineItems => BuildAssistantLineItems(DisplayTitle, DisplayDetail);
    public IReadOnlyList<ChatAttachmentPreviewItem> AttachmentItems => Attachments ?? Array.Empty<ChatAttachmentPreviewItem>();
    public Visibility AttachmentsVisibility => AttachmentItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string DisplayDetail => Badge == "assistant" && Kind == "message" ? HumanizeAssistantText(Detail) : LooksLikeAttachmentSummary(Detail) ? "" : Badge == "user" && Kind == "message" ? StripAttachmentBlock(Detail) : Detail;
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(DisplayDetail) ? Visibility.Collapsed : Visibility.Visible;

    private static bool LooksLikeAttachmentSummary(string text)
        => !string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text.Trim(), @"^\d+\s+attachment\(s\):", RegexOptions.IgnoreCase);

    private static IReadOnlyList<ChatMessageLineItem> BuildAssistantLineItems(string title, string detail)
    {
        var text = string.Join("\n", new[] { title, detail }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ChatMessageLineItem>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line =>
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("• ")) return new ChatMessageLineItem(trimmed[2..], true);
                return new ChatMessageLineItem(line, false);
            })
            .ToList();
    }

    private static string StripAttachmentBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"(?im)^\[image attachment\]\s*$", "");
        var match = Regex.Match(normalized, @"(?im)^Attached local files:\s*$");
        return match.Success ? normalized[..match.Index].TrimEnd() : normalized.TrimEnd();
    }

    private static string HumanizeAssistantText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var output = new List<string>();
        var previousBlank = false;
        foreach (var raw in lines)
        {
            var line = CleanMarkdownLine(raw);
            var blank = string.IsNullOrWhiteSpace(line);
            if (blank && previousBlank) continue;
            output.Add(line);
            previousBlank = blank;
        }
        return string.Join("\n", output).Trim();
    }

    private static string CleanMarkdownLine(string raw)
    {
        var indentLength = raw.Length - raw.TrimStart().Length;
        var indent = raw[..indentLength];
        var line = raw.TrimEnd();
        var trimmed = line.TrimStart();
        trimmed = Regex.Replace(trimmed, "^#{1,6}\\s+", "");
        trimmed = Regex.Replace(trimmed, "^>\\s*", "");
        trimmed = Regex.Replace(trimmed, "^[-*]\\s+", "• ");
        trimmed = trimmed.Replace("**", "").Replace("__", "").Replace("`", "");
        return string.IsNullOrWhiteSpace(trimmed) ? string.Empty : indent + trimmed;
    }
}
