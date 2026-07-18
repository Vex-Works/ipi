using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ipi.Desktop.Services;

namespace Ipi.Desktop;

public partial class MainWindow
{
    private sealed record ProjectSessionDragData(ProjectGroupItem Owner, SessionItem Session);

    private const byte VkLeftWindows = 0x5B;
    private const byte VkH = 0x48;
    private const uint KeyEventUp = 0x0002;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int DwmRound = 2;
    private const int DwmBackdropAcrylic = 3;
    private bool _isFullScreen;
    private bool _isDraggingChatRail;
    private bool _isDraggingChatExternalScroll;
    private bool _chatAutoFollowEnabled = true;
    private bool _forceNextChatScrollToLatest;
    private bool _isProgrammaticChatScroll;
    private bool _chatScrollToLatestPending;
    private bool _suppressNextChatMarkerClick;
    private Point _chatRailDragStart;
    private Point _chatExternalScrollDragStart;
    private double _chatRailDragStartOffset;
    private double _chatExternalScrollDragStartOffset;
    private double _sidebarResizeRequestedWidth;
    private bool _isResizingSidebar;
    private bool _isSidebarResizeCursorVisible;
    private double _rightPanelResizeRequestedWidth;
    private bool _isResizingRightPanel;
    private bool _isRightPanelResizeCursorVisible;
    private readonly DispatcherTimer _sidebarPeekCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(680) };
    private readonly DispatcherTimer _rightPanelPeekCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(680) };
    private readonly ArchiveStoreService _archiveStore = new();
    private readonly SoundPlayer _completionSound = new(CreateCompletionSoundStream());
    private readonly SettingsWindowViewModel _settingsViewModel;
    private SettingsWindow? _settingsWindow;
    private double _zoomScale = 1.0;
    private string _lastBrowserUrl = "about:blank";
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private ScrollViewer? _chatScrollViewer;
    private Point _projectDragStart;
    private ProjectGroupItem? _projectDragSource;
    private FrameworkElement? _projectDragElement;
    private bool _isProjectDragging;
    private Point _projectSessionDragStart;
    private SessionItem? _projectSessionDragSource;
    private ProjectGroupItem? _projectSessionDragOwner;
    private FrameworkElement? _projectSessionDragElement;
    private bool _isProjectSessionDragging;

    private ScrollViewer? TryGetChatScrollViewer()
    {
        if (_chatScrollViewer is not null) return _chatScrollViewer;
        _chatScrollViewer = FindVisualChild<ScrollViewer>(ChatItemsControl);
        return _chatScrollViewer;
    }

    private ScrollViewer ChatScrollViewer => TryGetChatScrollViewer() ?? throw new InvalidOperationException("Chat scroll viewer is not ready.");

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private string T(string zh, string en) => ViewModel.IsEnglishUi ? en : zh;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel(_archiveStore);
        viewModel.RequestWindowsDictationToggle += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            FocusPromptBox();
            SendWindowsDictationHotkey();
        }), DispatcherPriority.ApplicationIdle);
        viewModel.RequestScrollChatToLatest += ScheduleChatScrollToLatest;
        viewModel.RequestCompletionSound += PlayCompletionSound;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = viewModel;
        _settingsViewModel = new SettingsWindowViewModel(new AppearanceSettingsService(), _archiveStore);
        _settingsViewModel.SettingsChanged += settings => Dispatcher.BeginInvoke((Action)(() => ApplyAppearanceSettings(settings)), DispatcherPriority.Render);
        _archiveStore.Changed += () => Dispatcher.BeginInvoke((Action)(() => ViewModel.RefreshAfterArchiveChange()), DispatcherPriority.Background);
        _sidebarPeekCloseTimer.Tick += (_, _) =>
        {
            _sidebarPeekCloseTimer.Stop();
            if (_isResizingSidebar || SidebarPanel.IsMouseOver || SidebarResizeThumb.IsMouseOver) return;
            ViewModel.CloseSidebarPeek();
        };
        _rightPanelPeekCloseTimer.Tick += (_, _) =>
        {
            _rightPanelPeekCloseTimer.Stop();
            if (_isResizingRightPanel || RightPanel.IsMouseOver || RightPanelResizeThumb.IsMouseOver) return;
            ViewModel.CloseRightPanelPeek();
        };
        Loaded += (_, _) =>
        {
            ApplyAppearanceSettings(_settingsViewModel.CurrentSettings);
            UpdateNotificationSoundButton();
            ApplySidebarPanelVisualState(false);
            ApplyRightPanelVisualState(false);
            Dispatcher.BeginInvoke((Action)FocusPromptBox, DispatcherPriority.ApplicationIdle);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplySmallWindowCorners();
        ApplyDwmAcrylicBackdrop(_settingsViewModel.CurrentSettings);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsSidebarAutoHidden)
            or nameof(MainWindowViewModel.IsSidebarPeekOpen)
            or nameof(MainWindowViewModel.SidebarPanelWidth))
        {
            Dispatcher.BeginInvoke((Action)(() => ApplySidebarPanelVisualState(true)), DispatcherPriority.Render);
        }
        if (e.PropertyName is nameof(MainWindowViewModel.IsInspectorVisible)
            or nameof(MainWindowViewModel.IsRightPanelAutoHidden)
            or nameof(MainWindowViewModel.IsRightPanelPeekOpen)
            or nameof(MainWindowViewModel.RightPanelPanelWidth))
        {
            Dispatcher.BeginInvoke((Action)(() => ApplyRightPanelVisualState(true)), DispatcherPriority.Render);
        }
    }

    private void ApplySidebarPanelVisualState(bool animated)
    {
        if (!IsLoaded) return;
        var isClosedPeek = ViewModel.IsSidebarAutoHidden && !ViewModel.IsSidebarPeekOpen;
        var targetX = isClosedPeek ? -Math.Max(1, ViewModel.SidebarPanelWidth + 2) : 0;
        var targetOpacity = isClosedPeek ? 0.0 : 1.0;

        if (!animated)
        {
            SidebarPanelTranslate.X = targetX;
            SidebarPanel.Opacity = targetOpacity;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(isClosedPeek ? 210 : 170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SidebarPanelTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            To = targetX,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        });
        SidebarPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(isClosedPeek ? 180 : 120),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    private void ApplyRightPanelVisualState(bool animated)
    {
        if (!IsLoaded) return;
        var isClosedPeek = ViewModel.IsRightPanelAutoHidden && !ViewModel.IsRightPanelPeekOpen;
        var targetX = isClosedPeek ? Math.Max(1, ViewModel.RightPanelPanelWidth + 2) : 0;
        var targetOpacity = isClosedPeek ? 0.0 : 1.0;

        if (!animated)
        {
            RightPanelTranslate.X = targetX;
            RightPanel.Opacity = targetOpacity;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(isClosedPeek ? 210 : 170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RightPanelTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            To = targetX,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        });
        RightPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(isClosedPeek ? 180 : 120),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    private void ApplySmallWindowCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var preference = DwmRound;
            _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Older Windows builds may not support DWMWA_WINDOW_CORNER_PREFERENCE.
        }
    }

    private void ApplyDwmAcrylicBackdrop(AppearanceSettings? settings = null)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            {
                target.BackgroundColor = Colors.Transparent;
            }

            var normalized = (settings ?? AppearanceSettings.Default).Normalize();
            var darkMode = normalized.EffectiveMode() == "light" ? 0 : 1;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int));

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var backdrop = DwmBackdropAcrylic;
            _ = DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch
        {
            // System backdrop is best-effort. Unsupported builds keep the semi-transparent shell.
        }
    }

    private void ApplyAppearanceSettings(AppearanceSettings settings)
    {
        settings = settings.Normalize();
        ViewModel.SetLanguage(settings.Language);
        ViewModel.SetAppearanceMode(settings.Mode);
        ApplyTypographySettings(settings);
        ApplyDwmAcrylicBackdrop(settings);
        Dispatcher.BeginInvoke((Action)RestartHeroLogoAnimation, DispatcherPriority.Render);
        var mode = settings.EffectiveMode();
        var alpha = (byte)Math.Clamp(0xF0 - settings.WindowTransparency * 2.6, 0x78, 0xF0);
        var sidebarAlpha = (byte)Math.Clamp(0xF2 - settings.WindowTransparency * 2.35, 0x82, 0xF2);
        var titleAlpha = (byte)Math.Clamp(0xF3 - settings.WindowTransparency * 2.45, 0x80, 0xF3);

        if (settings.Theme == "broadsheet")
        {
            if (mode == "dark")
            {
                SetBrush("AppBg", Color.FromRgb(17, 25, 103));
                SetBrush("GlassShellBg", Color.FromArgb(alpha, 17, 25, 103));
                SetBrush("GlassTitleBg", Color.FromArgb(titleAlpha, 13, 19, 85));
                SetBrush("SidebarBg", Color.FromRgb(232, 230, 224));
                SetBrush("SidebarHover", Color.FromArgb(38, 25, 37, 170));
                SetBrush("ControlHoverBg", Color.FromRgb(25, 37, 170));
                SetBrush("MenuHoverBg", Color.FromRgb(25, 37, 170));
                SetBrush("SendButtonHoverBg", Color.FromRgb(232, 230, 224));
                SetBrush("Surface", Color.FromRgb(17, 25, 103));
                SetBrush("ComposerBg", Color.FromRgb(232, 230, 224));
                SetBrush("ComposerBorder", Color.FromRgb(232, 230, 224));
                SetBrush("ComposerChipBg", Color.FromRgb(232, 230, 224));
                SetBrush("ComposerChipBorder", Color.FromArgb(84, 25, 37, 170));
                SetBrush("UserBubbleBg", Color.FromArgb(22, 232, 230, 224));
                SetBrush("UserBubbleBorder", Color.FromArgb(46, 232, 230, 224));
                SetBrush("PopupBg", Color.FromRgb(13, 19, 85));
                SetBrush("PopupText", Color.FromRgb(232, 230, 224));
                SetBrush("PopupBorder", Color.FromArgb(170, 232, 230, 224));
                SetBrush("PopupDivider", Color.FromArgb(64, 232, 230, 224));
                SetBrush("SendButtonIdleBg", Color.FromRgb(25, 37, 170));
                SetBrush("SendButtonIdleIcon", Color.FromRgb(232, 230, 224));
                SetBrush("SendButtonRunningBg", Color.FromRgb(232, 230, 224));
                SetBrush("SendButtonRunningIcon", Color.FromRgb(25, 37, 170));
                SetBrush("RowBg", Color.FromRgb(17, 25, 103));
                SetBrush("FeedbackRowBg", Color.FromRgb(25, 37, 170));
                SetBrush("FeedbackRowBorder", Color.FromRgb(232, 230, 224));
                SetBrush("FeedbackAccent", Color.FromRgb(232, 230, 224));
                SetBrush("FeedbackSoftBg", Color.FromRgb(25, 37, 170));
                SetBrush("TextPrimary", Color.FromRgb(232, 230, 224));
                SetBrush("TextMuted", Color.FromRgb(232, 230, 224));
                SetBrush("TextDim", Color.FromRgb(198, 202, 255));
                SetBrush("ChatRailLine", Color.FromArgb(68, 232, 230, 224));
                SetBrush("ChatScrollThumb", Color.FromRgb(232, 230, 224));
                SetBrush("Line", Color.FromArgb(72, 232, 230, 224));
                SetBrush("CaretBrush", Color.FromRgb(232, 230, 224));
                SetBrush("SelectionBrush", Color.FromArgb(110, 232, 230, 224));
            }
            else
            {
                SetBrush("AppBg", Color.FromRgb(232, 230, 224));
                SetBrush("GlassShellBg", Color.FromArgb(alpha, 232, 230, 224));
                SetBrush("GlassTitleBg", Color.FromArgb(titleAlpha, 232, 230, 224));
                SetBrush("SidebarBg", Color.FromRgb(25, 37, 170));
                SetBrush("SidebarHover", Color.FromRgb(42, 55, 190));
                SetBrush("ControlHoverBg", Color.FromRgb(232, 230, 224));
                SetBrush("MenuHoverBg", Color.FromRgb(232, 230, 224));
                SetBrush("SendButtonHoverBg", Color.FromRgb(13, 19, 85));
                SetBrush("Surface", Color.FromRgb(232, 230, 224));
                SetBrush("ComposerBg", Color.FromRgb(25, 37, 170));
                SetBrush("ComposerBorder", Color.FromRgb(232, 230, 224));
                SetBrush("ComposerChipBg", Color.FromRgb(25, 37, 170));
                SetBrush("ComposerChipBorder", Color.FromArgb(80, 25, 37, 170));
                SetBrush("UserBubbleBg", Color.FromArgb(14, 25, 37, 170));
                SetBrush("UserBubbleBorder", Color.FromArgb(34, 25, 37, 170));
                SetBrush("PopupBg", Color.FromRgb(232, 230, 224));
                SetBrush("PopupText", Color.FromRgb(25, 37, 170));
                SetBrush("PopupBorder", Color.FromRgb(25, 37, 170));
                SetBrush("PopupDivider", Color.FromArgb(58, 25, 37, 170));
                SetBrush("SendButtonIdleBg", Color.FromRgb(232, 230, 224));
                SetBrush("SendButtonIdleIcon", Color.FromRgb(25, 37, 170));
                SetBrush("SendButtonRunningBg", Color.FromRgb(13, 19, 85));
                SetBrush("SendButtonRunningIcon", Color.FromRgb(232, 230, 224));
                SetBrush("RowBg", Color.FromRgb(232, 230, 224));
                SetBrush("FeedbackRowBg", Color.FromRgb(232, 230, 224));
                SetBrush("FeedbackRowBorder", Color.FromRgb(25, 37, 170));
                SetBrush("FeedbackAccent", Color.FromRgb(25, 37, 170));
                SetBrush("FeedbackSoftBg", Color.FromRgb(232, 230, 224));
                SetBrush("TextPrimary", Color.FromRgb(25, 37, 170));
                SetBrush("TextMuted", Color.FromRgb(25, 37, 170));
                SetBrush("TextDim", Color.FromRgb(75, 85, 170));
                SetBrush("ChatRailLine", Color.FromArgb(55, 25, 37, 170));
                SetBrush("ChatScrollThumb", Color.FromRgb(25, 37, 170));
                SetBrush("Line", Color.FromArgb(55, 25, 37, 170));
                SetBrush("CaretBrush", Color.FromRgb(25, 37, 170));
                SetBrush("SelectionBrush", Color.FromArgb(92, 25, 37, 170));
            }
        }
        else if (settings.Theme == "candy-block")
        {
            if (mode == "dark")
            {
                SetBrush("AppBg", Color.FromRgb(22, 22, 22));
                SetBrush("GlassShellBg", Color.FromRgb(22, 22, 22));
                SetBrush("GlassTitleBg", Color.FromRgb(255, 197, 238));
                SetBrush("SidebarBg", Color.FromRgb(22, 22, 22));
                SetBrush("SidebarHover", Color.FromRgb(45, 32, 43));
                SetBrush("ControlHoverBg", Color.FromRgb(45, 32, 43));
                SetBrush("MenuHoverBg", Color.FromRgb(45, 32, 43));
                SetBrush("SendButtonHoverBg", Color.FromRgb(255, 197, 238));
                SetBrush("Surface", Color.FromRgb(22, 22, 22));
                SetBrush("ComposerBg", Color.FromRgb(255, 252, 246));
                SetBrush("ComposerBorder", Color.FromRgb(255, 131, 218));
                SetBrush("ComposerChipBg", Color.FromRgb(255, 252, 246));
                SetBrush("ComposerChipBorder", Color.FromRgb(196, 201, 221));
                SetBrush("UserBubbleBg", Color.FromArgb(18, 255, 131, 218));
                SetBrush("UserBubbleBorder", Color.FromArgb(48, 255, 131, 218));
                SetBrush("PopupBg", Color.FromRgb(22, 22, 22));
                SetBrush("PopupText", Color.FromRgb(255, 252, 246));
                SetBrush("PopupBorder", Color.FromRgb(196, 201, 221));
                SetBrush("PopupDivider", Color.FromRgb(80, 80, 80));
                SetBrush("SendButtonIdleBg", Color.FromRgb(255, 131, 218));
                SetBrush("SendButtonIdleIcon", Color.FromRgb(255, 255, 255));
                SetBrush("SendButtonRunningBg", Color.FromRgb(255, 197, 238));
                SetBrush("SendButtonRunningIcon", Color.FromRgb(0, 0, 0));
                SetBrush("RowBg", Color.FromRgb(22, 22, 22));
                SetBrush("FeedbackRowBg", Color.FromRgb(45, 32, 43));
                SetBrush("FeedbackRowBorder", Color.FromRgb(255, 131, 218));
                SetBrush("FeedbackAccent", Color.FromRgb(255, 131, 218));
                SetBrush("FeedbackSoftBg", Color.FromRgb(22, 22, 22));
                SetBrush("TextPrimary", Color.FromRgb(255, 252, 246));
                SetBrush("TextMuted", Color.FromRgb(204, 204, 204));
                SetBrush("TextDim", Color.FromRgb(96, 96, 96));
                SetBrush("ChatRailLine", Color.FromArgb(72, 204, 204, 204));
                SetBrush("ChatScrollThumb", Color.FromRgb(255, 131, 218));
                SetBrush("Line", Color.FromRgb(51, 51, 51));
                SetBrush("CaretBrush", Color.FromRgb(255, 252, 246));
                SetBrush("SelectionBrush", Color.FromRgb(255, 197, 238));
            }
            else
            {
                SetBrush("AppBg", Color.FromRgb(255, 255, 255));
                SetBrush("GlassShellBg", Color.FromArgb(alpha, 255, 255, 255));
                SetBrush("GlassTitleBg", Color.FromArgb(titleAlpha, 255, 197, 238));
                SetBrush("SidebarBg", Color.FromRgb(22, 22, 22));
                SetBrush("SidebarHover", Color.FromRgb(51, 51, 51));
                SetBrush("ControlHoverBg", Color.FromRgb(247, 225, 247));
                SetBrush("MenuHoverBg", Color.FromRgb(247, 225, 247));
                SetBrush("SendButtonHoverBg", Color.FromRgb(255, 197, 238));
                SetBrush("Surface", Color.FromRgb(255, 255, 255));
                SetBrush("ComposerBg", Color.FromRgb(255, 252, 246));
                SetBrush("ComposerBorder", Color.FromRgb(196, 201, 221));
                SetBrush("ComposerChipBg", Color.FromRgb(255, 252, 246));
                SetBrush("ComposerChipBorder", Color.FromRgb(196, 201, 221));
                SetBrush("UserBubbleBg", Color.FromArgb(132, 255, 252, 246));
                SetBrush("UserBubbleBorder", Color.FromArgb(72, 196, 201, 221));
                SetBrush("PopupBg", Color.FromRgb(255, 252, 246));
                SetBrush("PopupText", Color.FromRgb(0, 0, 0));
                SetBrush("PopupBorder", Color.FromRgb(196, 201, 221));
                SetBrush("PopupDivider", Color.FromRgb(204, 204, 204));
                SetBrush("SendButtonIdleBg", Color.FromRgb(255, 131, 218));
                SetBrush("SendButtonIdleIcon", Color.FromRgb(255, 255, 255));
                SetBrush("SendButtonRunningBg", Color.FromRgb(255, 197, 238));
                SetBrush("SendButtonRunningIcon", Color.FromRgb(0, 0, 0));
                SetBrush("RowBg", Color.FromRgb(255, 252, 246));
                SetBrush("FeedbackRowBg", Color.FromRgb(247, 225, 247));
                SetBrush("FeedbackRowBorder", Color.FromRgb(255, 131, 218));
                SetBrush("FeedbackAccent", Color.FromRgb(255, 131, 218));
                SetBrush("FeedbackSoftBg", Color.FromRgb(255, 252, 246));
                SetBrush("TextPrimary", Color.FromRgb(0, 0, 0));
                SetBrush("TextMuted", Color.FromRgb(51, 51, 51));
                SetBrush("TextDim", Color.FromRgb(140, 140, 140));
                SetBrush("ChatRailLine", Color.FromRgb(204, 204, 204));
                SetBrush("ChatScrollThumb", Color.FromRgb(255, 131, 218));
                SetBrush("Line", Color.FromRgb(204, 204, 204));
                SetBrush("CaretBrush", Color.FromRgb(0, 0, 0));
                SetBrush("SelectionBrush", Color.FromRgb(255, 197, 238));
            }
        }
        else if (mode == "light")
        {
            SetBrush("AppBg", Color.FromRgb(238, 241, 247));
            SetBrush("GlassShellBg", Color.FromArgb(alpha, 238, 241, 247));
            SetBrush("GlassTitleBg", Color.FromArgb(titleAlpha, 232, 237, 247));
            SetBrush("SidebarBg", Color.FromArgb(sidebarAlpha, 226, 232, 243));
            SetBrush("SidebarHover", Color.FromRgb(219, 226, 241));
            SetBrush("ControlHoverBg", Color.FromRgb(221, 228, 242));
            SetBrush("MenuHoverBg", Color.FromRgb(222, 229, 243));
            SetBrush("SendButtonHoverBg", Color.FromRgb(48, 52, 64));
            SetBrush("Surface", Color.FromRgb(243, 246, 252));
            SetBrush("ComposerBg", Color.FromRgb(245, 247, 252));
            SetBrush("ComposerBorder", Color.FromRgb(190, 199, 218));
            SetBrush("ComposerChipBg", Color.FromRgb(235, 239, 248));
            SetBrush("ComposerChipBorder", Color.FromRgb(205, 213, 229));
            SetBrush("UserBubbleBg", Color.FromArgb(22, 255, 255, 255));
            SetBrush("UserBubbleBorder", Color.FromArgb(0, 255, 255, 255));
            SetBrush("PopupBg", Color.FromRgb(247, 249, 254));
            SetBrush("PopupText", Color.FromRgb(36, 39, 51));
            SetBrush("PopupBorder", Color.FromRgb(198, 207, 225));
            SetBrush("PopupDivider", Color.FromRgb(215, 222, 235));
            SetBrush("SendButtonIdleBg", Color.FromRgb(36, 39, 51));
            SetBrush("SendButtonIdleIcon", Color.FromRgb(255, 255, 255));
            SetBrush("SendButtonRunningBg", Color.FromRgb(36, 39, 51));
            SetBrush("SendButtonRunningIcon", Color.FromRgb(255, 255, 255));
            SetBrush("RowBg", Color.FromRgb(244, 246, 251));
            SetBrush("FeedbackRowBg", Color.FromRgb(242, 252, 246));
            SetBrush("FeedbackRowBorder", Color.FromRgb(179, 230, 199));
            SetBrush("FeedbackAccent", Color.FromRgb(17, 145, 72));
            SetBrush("FeedbackSoftBg", Color.FromRgb(238, 241, 247));
            SetBrush("TextPrimary", Color.FromRgb(36, 39, 51));
            SetBrush("TextMuted", Color.FromRgb(98, 106, 122));
            SetBrush("TextDim", Color.FromRgb(101, 109, 125));
            SetBrush("ChatRailLine", Color.FromArgb(46, 142, 151, 170));
            SetBrush("ChatScrollThumb", Color.FromRgb(182, 190, 206));
            SetBrush("Line", Color.FromArgb(110, 190, 199, 218));
            SetBrush("CaretBrush", Color.FromRgb(36, 45, 65));
            SetBrush("SelectionBrush", Color.FromRgb(185, 200, 246));
        }
        else
        {
            SetBrush("AppBg", Color.FromRgb(17, 18, 20));
            SetBrush("GlassShellBg", Color.FromArgb(alpha, 17, 18, 20));
            SetBrush("GlassTitleBg", Color.FromArgb(titleAlpha, 23, 26, 32));
            SetBrush("SidebarBg", Color.FromArgb(sidebarAlpha, 29, 32, 39));
            SetBrush("SidebarHover", Color.FromRgb(44, 47, 55));
            SetBrush("ControlHoverBg", Color.FromRgb(58, 58, 59));
            SetBrush("MenuHoverBg", Color.FromRgb(55, 55, 56));
            SetBrush("SendButtonHoverBg", Color.FromRgb(255, 255, 255));
            SetBrush("Surface", Color.FromRgb(24, 26, 31));
            SetBrush("ComposerBg", Color.FromRgb(43, 44, 46));
            SetBrush("ComposerBorder", Color.FromRgb(63, 68, 77));
            SetBrush("ComposerChipBg", Color.FromRgb(58, 58, 59));
            SetBrush("ComposerChipBorder", Color.FromRgb(76, 76, 79));
            SetBrush("UserBubbleBg", Color.FromArgb(16, 255, 255, 255));
            SetBrush("UserBubbleBorder", Color.FromArgb(0, 255, 255, 255));
            SetBrush("PopupBg", Color.FromRgb(43, 43, 44));
            SetBrush("PopupText", Color.FromRgb(244, 246, 250));
            SetBrush("PopupBorder", Color.FromRgb(72, 72, 74));
            SetBrush("PopupDivider", Color.FromRgb(61, 61, 64));
            SetBrush("SendButtonIdleBg", Color.FromRgb(244, 246, 250));
            SetBrush("SendButtonIdleIcon", Color.FromRgb(20, 22, 28));
            SetBrush("SendButtonRunningBg", Color.FromRgb(56, 61, 74));
            SetBrush("SendButtonRunningIcon", Color.FromRgb(235, 238, 245));
            SetBrush("RowBg", Color.FromRgb(22, 24, 29));
            SetBrush("FeedbackRowBg", Color.FromRgb(20, 32, 25));
            SetBrush("FeedbackRowBorder", Color.FromRgb(49, 94, 68));
            SetBrush("FeedbackAccent", Color.FromRgb(84, 199, 122));
            SetBrush("FeedbackSoftBg", Color.FromRgb(32, 34, 38));
            SetBrush("TextPrimary", Color.FromRgb(243, 244, 246));
            SetBrush("TextMuted", Color.FromRgb(169, 173, 183));
            SetBrush("TextDim", Color.FromRgb(138, 145, 158));
            SetBrush("ChatRailLine", Color.FromArgb(72, 116, 121, 134));
            SetBrush("ChatScrollThumb", Color.FromRgb(106, 112, 126));
            SetBrush("Line", Color.FromArgb(96, 63, 68, 77));
            SetBrush("CaretBrush", Color.FromRgb(243, 244, 246));
            SetBrush("SelectionBrush", Color.FromRgb(75, 111, 234));
        }

        ApplyThinkingAccent(settings, mode);

        if (settings.Theme == "broadsheet" && mode == "dark")
        {
            SetBrush("ComposerTextPrimary", Color.FromRgb(25, 37, 170));
            SetBrush("ComposerTextMuted", Color.FromRgb(25, 37, 170));
            SetBrush("ComposerCaretBrush", Color.FromRgb(25, 37, 170));
            SetBrush("ComposerHoverBg", Color.FromRgb(25, 37, 170));
            SetBrush("ComposerHoverText", Color.FromRgb(232, 230, 224));
        }
        else if (settings.Theme == "broadsheet" && mode == "light")
        {
            SetBrush("ComposerTextPrimary", Color.FromRgb(232, 230, 224));
            SetBrush("ComposerTextMuted", Color.FromRgb(232, 230, 224));
            SetBrush("ComposerCaretBrush", Color.FromRgb(232, 230, 224));
            SetBrush("ComposerHoverBg", Color.FromRgb(232, 230, 224));
            SetBrush("ComposerHoverText", Color.FromRgb(25, 37, 170));
        }
        else if (settings.Theme == "candy-block")
        {
            SetBrush("ComposerTextPrimary", Color.FromRgb(0, 0, 0));
            SetBrush("ComposerTextMuted", Color.FromRgb(51, 51, 51));
            SetBrush("ComposerCaretBrush", Color.FromRgb(0, 0, 0));
            SetBrush("ComposerHoverBg", Color.FromRgb(255, 197, 238));
            SetBrush("ComposerHoverText", Color.FromRgb(0, 0, 0));
        }
        else
        {
            SetBrush("ComposerTextPrimary", ResourceColor("TextPrimary", Color.FromRgb(243, 244, 246)));
            SetBrush("ComposerTextMuted", ResourceColor("TextMuted", Color.FromRgb(169, 173, 183)));
            SetBrush("ComposerCaretBrush", ResourceColor("CaretBrush", Color.FromRgb(243, 244, 246)));
            SetBrush("ComposerHoverBg", ResourceColor("ControlHoverBg", Color.FromRgb(58, 58, 59)));
            SetBrush("ComposerHoverText", ResourceColor("TextPrimary", Color.FromRgb(243, 244, 246)));
        }

        ApplySidebarScopedResources(settings, mode);
        ApplyTitleBarScopedResources(settings);
        ApplyBottomStatusScopedResources(settings);
    }

    private void ApplyThinkingAccent(AppearanceSettings settings, string mode)
    {
        if (settings.Theme == "broadsheet")
        {
            if (mode == "dark")
            {
                SetBrush("ThinkingAccent", Color.FromRgb(232, 230, 224));
                SetBrush("ThinkingEnergyAccent", Color.FromRgb(255, 255, 255));
            }
            else
            {
                SetBrush("ThinkingAccent", Color.FromRgb(25, 37, 170));
                SetBrush("ThinkingEnergyAccent", Color.FromRgb(109, 119, 222));
            }
            return;
        }

        if (settings.Theme == "candy-block")
        {
            SetBrush("ThinkingAccent", Color.FromRgb(255, 131, 218));
            SetBrush("ThinkingEnergyAccent", Color.FromRgb(255, 197, 238));
            return;
        }

        if (mode == "dark")
        {
            SetBrush("ThinkingAccent", Color.FromRgb(104, 128, 255));
            SetBrush("ThinkingEnergyAccent", Color.FromRgb(229, 234, 255));
        }
        else
        {
            SetBrush("ThinkingAccent", Color.FromRgb(82, 112, 255));
            SetBrush("ThinkingEnergyAccent", Color.FromRgb(220, 228, 255));
        }
    }

    private void ApplyBottomStatusScopedResources(AppearanceSettings settings)
    {
        if (settings.Theme == "candy-block")
        {
            SetScopedBrush(BottomStatusChrome.Resources, "TextPrimary", Color.FromRgb(0, 0, 0));
            SetScopedBrush(BottomStatusChrome.Resources, "TextMuted", Color.FromRgb(51, 51, 51));
            SetScopedBrush(BottomStatusChrome.Resources, "TextDim", Color.FromRgb(51, 51, 51));
            SetScopedBrush(BottomStatusChrome.Resources, "Line", Color.FromRgb(0, 0, 0));
            SetScopedBrush(BottomStatusChrome.Resources, "PopupDivider", Color.FromRgb(140, 140, 140));
            SetScopedBrush(BottomStatusChrome.Resources, "SidebarHover", Color.FromRgb(247, 225, 247));
            SetScopedBrush(BottomStatusChrome.Resources, "ControlHoverBg", Color.FromRgb(247, 225, 247));
            SetScopedBrush(BottomStatusChrome.Resources, "MenuHoverBg", Color.FromRgb(255, 197, 238));
        }
        else
        {
            var hover = ResourceColor("ControlHoverBg", Color.FromRgb(58, 58, 59));
            SetScopedBrush(BottomStatusChrome.Resources, "TextPrimary", ResourceColor("TextPrimary", Color.FromRgb(243, 244, 246)));
            SetScopedBrush(BottomStatusChrome.Resources, "TextMuted", ResourceColor("TextMuted", Color.FromRgb(169, 173, 183)));
            SetScopedBrush(BottomStatusChrome.Resources, "TextDim", ResourceColor("TextDim", Color.FromRgb(116, 121, 134)));
            SetScopedBrush(BottomStatusChrome.Resources, "Line", ResourceColor("Line", Color.FromRgb(54, 59, 69)));
            SetScopedBrush(BottomStatusChrome.Resources, "PopupDivider", ResourceColor("PopupDivider", Color.FromRgb(61, 61, 64)));
            SetScopedBrush(BottomStatusChrome.Resources, "SidebarHover", hover);
            SetScopedBrush(BottomStatusChrome.Resources, "ControlHoverBg", hover);
            SetScopedBrush(BottomStatusChrome.Resources, "MenuHoverBg", ResourceColor("MenuHoverBg", hover));
        }
    }

    private void ApplyTitleBarScopedResources(AppearanceSettings settings)
    {
        if (settings.Theme == "candy-block")
        {
            SetScopedBrush(TitleBarChrome.Resources, "TextPrimary", Color.FromRgb(0, 0, 0));
            SetScopedBrush(TitleBarChrome.Resources, "TextMuted", Color.FromRgb(0, 0, 0));
            SetScopedBrush(TitleBarChrome.Resources, "TextDim", Color.FromRgb(51, 51, 51));
            SetScopedBrush(TitleBarChrome.Resources, "SidebarHover", Color.FromRgb(247, 225, 247));
            SetScopedBrush(TitleBarChrome.Resources, "ControlHoverBg", Color.FromRgb(247, 225, 247));
            SetScopedBrush(TitleBarChrome.Resources, "MenuHoverBg", Color.FromRgb(255, 197, 238));
        }
        else
        {
            var hover = ResourceColor("ControlHoverBg", Color.FromRgb(58, 58, 59));
            SetScopedBrush(TitleBarChrome.Resources, "TextPrimary", ResourceColor("TextPrimary", Color.FromRgb(243, 244, 246)));
            SetScopedBrush(TitleBarChrome.Resources, "TextMuted", ResourceColor("TextMuted", Color.FromRgb(169, 173, 183)));
            SetScopedBrush(TitleBarChrome.Resources, "TextDim", ResourceColor("TextDim", Color.FromRgb(116, 121, 134)));
            SetScopedBrush(TitleBarChrome.Resources, "SidebarHover", hover);
            SetScopedBrush(TitleBarChrome.Resources, "ControlHoverBg", hover);
            SetScopedBrush(TitleBarChrome.Resources, "MenuHoverBg", ResourceColor("MenuHoverBg", hover));
        }
    }

    private void ApplySidebarScopedResources(AppearanceSettings settings, string mode)
    {
        if (settings.Theme == "broadsheet" && mode == "dark")
        {
            SetScopedBrush(SidebarPanel.Resources, "TextPrimary", Color.FromRgb(25, 37, 170));
            SetScopedBrush(SidebarPanel.Resources, "TextMuted", Color.FromRgb(25, 37, 170));
            SetScopedBrush(SidebarPanel.Resources, "TextDim", Color.FromRgb(94, 105, 196));
            SetScopedBrush(SidebarPanel.Resources, "Line", Color.FromArgb(90, 25, 37, 170));
        }
        else if (settings.Theme == "broadsheet" && mode == "light")
        {
            SetScopedBrush(SidebarPanel.Resources, "TextPrimary", Color.FromRgb(232, 230, 224));
            SetScopedBrush(SidebarPanel.Resources, "TextMuted", Color.FromRgb(232, 230, 224));
            SetScopedBrush(SidebarPanel.Resources, "TextDim", Color.FromArgb(170, 232, 230, 224));
            SetScopedBrush(SidebarPanel.Resources, "Line", Color.FromArgb(110, 232, 230, 224));
        }
        else if (settings.Theme == "candy-block")
        {
            SetScopedBrush(SidebarPanel.Resources, "TextPrimary", Color.FromRgb(255, 252, 246));
            SetScopedBrush(SidebarPanel.Resources, "TextMuted", Color.FromRgb(255, 252, 246));
            SetScopedBrush(SidebarPanel.Resources, "TextDim", Color.FromRgb(204, 204, 204));
            SetScopedBrush(SidebarPanel.Resources, "Line", Color.FromRgb(51, 51, 51));
        }
        else
        {
            SetScopedBrush(SidebarPanel.Resources, "TextPrimary", ResourceColor("TextPrimary", Color.FromRgb(243, 244, 246)));
            SetScopedBrush(SidebarPanel.Resources, "TextMuted", ResourceColor("TextMuted", Color.FromRgb(169, 173, 183)));
            SetScopedBrush(SidebarPanel.Resources, "TextDim", ResourceColor("TextDim", Color.FromRgb(116, 121, 134)));
            SetScopedBrush(SidebarPanel.Resources, "Line", ResourceColor("Line", Color.FromRgb(54, 59, 69)));
        }
    }

    private static void SetScopedBrush(ResourceDictionary resources, string key, Color color)
    {
        resources[key] = new SolidColorBrush(color);
    }

    private void SetBrush(string key, Color color)
    {
        Resources[key] = new SolidColorBrush(color);
    }

    private void SetFontFamily(string key, string family)
    {
        Resources[key] = new FontFamily(family);
    }

    private void ApplyTypographySettings(AppearanceSettings settings)
    {
        var english = settings.Language == "en-US";
        var googleSansCodeNf = TryCreateGoogleSansCodeNfFontFamily();

        Resources["IpiTextFontFamily"] = english && googleSansCodeNf is not null
            ? googleSansCodeNf
            : new FontFamily("Microsoft YaHei UI, Microsoft YaHei, DengXian, Segoe UI Variable Text, Segoe UI");
        Resources["IpiCodeFontFamily"] = english && googleSansCodeNf is not null
            ? googleSansCodeNf
            : new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI, Segoe UI Mono");
        Resources["IpiIconFontFamily"] = new FontFamily("Segoe UI Symbol");
        Resources["UseNerdFontIcons"] = false;
        InvalidateLucideIcons(this);
    }

    private static FontFamily? TryCreateGoogleSansCodeNfFontFamily()
    {
        var userFontDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        var regular = Path.Combine(userFontDir, "GoogleSansCodeNF-Regular.ttf");
        if (!File.Exists(regular)) return null;
        return new FontFamily(new Uri(userFontDir + Path.DirectorySeparatorChar, UriKind.Absolute), "./#Google Sans Code NF");
    }

    private static void InvalidateLucideIcons(DependencyObject root)
    {
        if (root is LucideIcon icon) icon.InvalidateVisual();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) InvalidateLucideIcons(VisualTreeHelper.GetChild(root, i));
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        EnableChatAutoFollowForNewPrompt();
        ViewModel.SendPrompt();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key == Key.F11)
        {
            ViewToggleFullScreen_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.G)
        {
            ViewModel.ExecuteRightPanelAction("review");
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N)
        {
            FileNewWindow_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.E)
        {
            ViewModel.IsExplorerExpanded = !ViewModel.IsExplorerExpanded;
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.OemOpenBrackets)
        {
            ViewModel.OpenAdjacentSession(-1);
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.OemCloseBrackets)
        {
            ViewModel.OpenAdjacentSession(1);
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && (key == Key.OemPlus || key == Key.Add))
        {
            SetZoom(_zoomScale + 0.1);
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.N)
        {
            ViewModel.NewConversation();
            FocusPromptBox();
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.B)
        {
            ViewModel.ToggleRightPanel();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.T)
        {
            OpenSideBrowser(focusAddress: true);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.P)
        {
            ViewModel.ExecuteRightPanelAction("files");
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.S)
        {
            ViewModel.ExecuteRightPanelAction("chat");
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.N)
        {
            ViewModel.NewConversation();
            FocusPromptBox();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.O)
        {
            OpenActiveLocation_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.B)
        {
            ViewModel.ToggleSidebar();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.J)
        {
            ViewModel.ToggleBottomPanel();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.Oem3)
        {
            OpenTerminalForWorkspace();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.R)
        {
            ReloadSideBrowser();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.F)
        {
            ViewModel.SelectNav(new NavItem("search", "Search", "search"));
            FocusPromptBox();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.OemOpenBrackets)
        {
            ViewModel.ReturnToChat();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && key == Key.OemCloseBrackets)
        {
            ViewModel.SelectNav(new QuickActionItem("Recent", "recent"));
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && (key == Key.OemMinus || key == Key.Subtract))
        {
            SetZoom(_zoomScale - 0.1);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && (key == Key.D0 || key == Key.NumPad0))
        {
            SetZoom(1.0);
            e.Handled = true;
        }
    }

    private void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && TryPasteClipboardImageAttachment())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            EnableChatAutoFollowForNewPrompt();
            ViewModel.SendPrompt();
            e.Handled = true;
        }
    }

    private void PromptBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (TryAttachClipboardFiles(e.DataObject))
        {
            e.CancelCommand();
            return;
        }

        var hasText = e.DataObject.GetDataPresent(DataFormats.UnicodeText, true) || e.DataObject.GetDataPresent(DataFormats.Text, true);
        if (hasText) return;

        if (TryPasteClipboardImageAttachment()) e.CancelCommand();
    }

    private bool TryPasteClipboardImageAttachment()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(IsAttachableClipboardImageFile).ToArray();
                if (files.Length > 0)
                {
                    ViewModel.AddFiles(files);
                    return true;
                }
            }

            if (!Clipboard.ContainsImage() || Clipboard.ContainsText()) return false;
            var image = Clipboard.GetImage();
            if (image is null) return false;
            var path = SaveClipboardImage(image);
            ViewModel.AddFiles(new[] { path });
            ViewModel.StatusText = $"attached clipboard image · {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"clipboard image paste failed · {ex.Message}";
            return false;
        }
    }

    private bool TryAttachClipboardFiles(IDataObject dataObject)
    {
        if (!dataObject.GetDataPresent(DataFormats.FileDrop, true)) return false;
        if (dataObject.GetData(DataFormats.FileDrop, true) is not string[] paths || paths.Length == 0) return false;
        var imageFiles = paths.Where(IsAttachableClipboardImageFile).ToArray();
        if (imageFiles.Length == 0) return false;
        ViewModel.AddFiles(imageFiles);
        return true;
    }

    private static bool IsAttachableClipboardImageFile(string path)
    {
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
    }

    private static string SaveClipboardImage(BitmapSource image)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipi", "clipboard");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"clipboard-image-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.DataContext is ConfigItem config && config.Name.Contains(T("设置", "Settings"), StringComparison.OrdinalIgnoreCase))
        {
            OpenSettingsWindow();
            return;
        }

        ViewModel.SelectNav(button.DataContext);
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SuggestionItem item }) ViewModel.UseSuggestion(item);
    }

    private void PluginPackage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PluginPackageViewItem item }) ViewModel.SelectPluginPackage(item);
    }

    private void PluginRefresh_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshPluginsPage();
    private async void PluginUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowPluginPackageInstallWarning(
                T("更新插件包", "Update plugin package"),
                ViewModel.SelectedPluginPackageName,
                ViewModel.SelectedPluginSource,
                T("这会运行 npm install，并可能执行该第三方 package 的安装脚本。只更新你信任的 package。", "This will run npm install and may execute install scripts from this third-party package. Only update packages you trust."))) return;
        await ViewModel.UpdateSelectedPluginAsync();
    }
    private void PluginToggleEnabled_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleSelectedPluginEnabled();
    private void PluginRemove_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveSelectedPlugin();
    private void PluginAddOpen_Click(object sender, RoutedEventArgs e) => ViewModel.OpenAddPlugin();
    private async void PluginAddConfirm_Click(object sender, RoutedEventArgs e)
    {
        var source = ViewModel.PluginNewSource.Trim();
        var packageName = source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) ? source[4..].Trim() : source;
        if (!ShowPluginPackageInstallWarning(
                T("安装插件包", "Install plugin package"),
                packageName,
                source,
                T("这会安装第三方 Pi package。npm/git/https 来源可能下载代码并执行 package 作者提供的安装脚本。", "This will install a third-party Pi package. npm/git/https sources may download code and execute install scripts from the package author."))) return;
        await ViewModel.AddPluginAsync();
    }
    private void PluginAddCancel_Click(object sender, RoutedEventArgs e) => ViewModel.CancelAddPlugin();

    private bool ShowPluginPackageInstallWarning(string title, string packageName, string source, string message)
    {
        var body = $"{message}\n\n{T("Package", "Package")}: {packageName}\n{T("Source", "Source")}: {source}\n\n{T("只在信任该来源时继续。", "Only continue if you trust this source.")}";
        return MessageBox.Show(this, body, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private void PanelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PanelRow row }) return;
        if (row.Kind == "skillAddPath")
        {
            AddSkillSourceFolder();
            return;
        }
        ViewModel.OpenRow(row);
    }

    private void AddSkillSourceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = T("选择其他 agent 根目录或 skills 文件夹", "Choose an agent root or skills folder"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) != true) return;
        ViewModel.AddSkillSourceFolder(dialog.FolderName, out _);
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => ViewModel.ConnectLocal();

    private void New_Click(object sender, RoutedEventArgs e) => ViewModel.NewConversation();

    private void TopNew_Click(object sender, RoutedEventArgs e) => ViewModel.NewConversation();

    private void TopSearch_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new NavItem("search", "Search", "search"));

    private void TopBack_Click(object sender, RoutedEventArgs e) => ViewModel.ReturnToChat();

    private void TopForward_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new QuickActionItem("Recent", "recent"));

    private void TopFile_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    private void TopEdit_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    private void TopView_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    private void TopHelp_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    private static void OpenButtonMenu(object sender)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void FileNewWindow_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
        Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
    }

    private void FileNewChat_Click(object sender, RoutedEventArgs e) => ViewModel.NewConversation();

    private void LogoNewChat_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewConversation();
        FocusPromptBox();
    }

    private void FileQuickChat_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewConversation();
        FocusPromptBox();
    }


    private void FileSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void NotificationSound_Click(object sender, RoutedEventArgs e)
    {
        _settingsViewModel.SetNotificationSoundsEnabled(!_settingsViewModel.NotificationSoundsEnabled);
        UpdateNotificationSoundButton();
    }

    private void UpdateNotificationSoundButton()
    {
        var enabled = _settingsViewModel.NotificationSoundsEnabled;
        NotificationSoundIcon.Icon = enabled ? "volume-2" : "volume-x";
        var label = enabled ? T("关闭回复完成提示音", "Mute reply completion sound") : T("开启回复完成提示音", "Enable reply completion sound");
        NotificationSoundButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(NotificationSoundButton, label);
        NotificationSoundIcon.InvalidateVisual();
    }

    private void PlayCompletionSound()
    {
        if (!_settingsViewModel.NotificationSoundsEnabled) return;
        _completionSound.Play();
    }

    private static Stream CreateCompletionSoundStream()
    {
        const int sampleRate = 44100;
        const double durationSeconds = 0.18;
        const double frequency = 1046.5;
        const double peakAmplitude = 0.13;
        const double fadeInSeconds = 0.012;
        const double fadeOutSeconds = 0.065;
        var sampleCount = (int)(sampleRate * durationSeconds);
        var stream = new MemoryStream(44 + sampleCount * sizeof(short));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + sampleCount * sizeof(short));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(sampleCount * sizeof(short));
            for (var i = 0; i < sampleCount; i++)
            {
                var time = i / (double)sampleRate;
                var envelope = Math.Min(1, time / fadeInSeconds) * Math.Min(1, (durationSeconds - time) / fadeOutSeconds);
                var sample = Math.Sin(2 * Math.PI * frequency * time) * envelope * peakAmplitude;
                writer.Write((short)(sample * short.MaxValue));
            }
        }
        stream.Position = 0;
        return stream;
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsViewModel) { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }


    private void ViewToggleSidebar_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleSidebar();

    private void ViewToggleBottomPanel_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleBottomPanel();

    private void ViewTogglePinnedSummary_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePinnedSummary();

    private void ViewOpenTerminal_Click(object sender, RoutedEventArgs e) => OpenTerminalForWorkspace();

    private void ViewToggleFileTree_Click(object sender, RoutedEventArgs e) => ViewModel.IsExplorerExpanded = !ViewModel.IsExplorerExpanded;

    private void ViewOpenBrowserTab_Click(object sender, RoutedEventArgs e) => OpenSideBrowser(focusAddress: false);

    private void ViewFocusBrowserAddressBar_Click(object sender, RoutedEventArgs e) => OpenSideBrowser(focusAddress: true);

    private void ViewReloadBrowserPage_Click(object sender, RoutedEventArgs e) => ReloadSideBrowser();

    private void OpenSideBrowser(bool focusAddress)
    {
        ViewModel.ExecuteRightPanelAction("browser");
        if (!focusAddress) return;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            SideBrowserAddressBox.Focus();
            SideBrowserAddressBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void ReloadSideBrowser()
    {
        ViewModel.ExecuteRightPanelAction("browser");
        if (SideBrowserWebView.CoreWebView2 is not null)
        {
            SideBrowserWebView.CoreWebView2.Reload();
            ViewModel.StatusText = ViewModel.IsEnglishUi ? "browser reloading" : "正在重新加载浏览器";
            return;
        }
        OpenSideBrowser(focusAddress: true);
    }

    private void ViewToggleSidePanel_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleRightPanel();

    private void ViewFind_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new NavItem("search", "Search", "search"));

    private void ViewPreviousChat_Click(object sender, RoutedEventArgs e) => ViewModel.OpenAdjacentSession(-1);

    private void ViewNextChat_Click(object sender, RoutedEventArgs e) => ViewModel.OpenAdjacentSession(1);

    private void ViewBack_Click(object sender, RoutedEventArgs e) => ViewModel.ReturnToChat();

    private void ViewForward_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new QuickActionItem("Recent", "recent"));

    private void ViewZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomScale + 0.1);

    private void ViewZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomScale - 0.1);

    private void ViewActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void ViewToggleFullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            WindowState = _windowStateBeforeFullScreen;
            _isFullScreen = false;
        }
        else
        {
            _windowStateBeforeFullScreen = WindowState;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
        }
    }

    private void SetZoom(double value)
    {
        _zoomScale = Math.Clamp(Math.Round(value, 2), 0.75, 1.5);
        AppRoot.LayoutTransform = Math.Abs(_zoomScale - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(_zoomScale, _zoomScale);
        ViewModel.StatusText = $"zoom {Math.Round(_zoomScale * 100)}%";
    }

    private void OpenTerminalForWorkspace()
    {
        var cwd = ViewModel.CurrentRunDirectory;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wt.exe",
                UseShellExecute = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(cwd);
            Process.Start(psi);
            ViewModel.StatusText = $"terminal opened · {cwd}";
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = true,
                };
                psi.ArgumentList.Add("-NoExit");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add($"Set-Location -LiteralPath '{cwd.Replace("'", "''")}'");
                Process.Start(psi);
                ViewModel.StatusText = $"terminal opened · {cwd}";
            }
            catch (Exception ex)
            {
                ViewModel.StatusText = $"terminal failed · {ex.Message}";
            }
        }
    }

    private void OpenExternalBrowser(string url)
    {
        var target = string.IsNullOrWhiteSpace(url) ? "about:blank" : url;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            _lastBrowserUrl = target;
            ViewModel.StatusText = $"browser opened · {target}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"browser failed · {ex.Message}";
        }
    }

    private void ViewSessions_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new QuickActionItem("Sessions", "sessions"));

    private void ViewModels_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new QuickActionItem("Models", "models"));

    private void ViewSkills_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new QuickActionItem("Skills", "skills"));

    private void ViewPlugins_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new QuickActionItem("Plugins", "plugins"));

    private void ViewSystem_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new QuickActionItem("System", "system"));

    private void HelpAbout_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "ipi\nNative local agent workspace", "About ipi", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        _settingsViewModel.ToggleLightDarkMode();
    }

    private void TopExport_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new ToolbarActionItem("⇩", "Export", "export"));

    private void TopBranches_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleBranchPicker();

    private void BranchItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BranchItem branch }) ViewModel.CheckoutBranch(branch);
    }

    private void BranchCreate_Click(object sender, RoutedEventArgs e) => ViewModel.CreateAndCheckoutBranch();

    private void TopSystem_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNav(new ToolbarActionItem("▣", "System", "system"));

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleSidebar();

    private void ToggleRightPanel_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleRightPanel();

    private void CloseRightPanel_Click(object sender, RoutedEventArgs e) => ViewModel.CloseRightPanel();

    private void RightPanelAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RightPanelActionItem action }) ViewModel.ExecuteRightPanelAction(action.Kind);
    }

    private async void SideTerminalRun_Click(object sender, RoutedEventArgs e) => await ViewModel.RunSideTerminalCommandAsync();

    private void SideBrowserOpen_Click(object sender, RoutedEventArgs e) => OpenExternalBrowser(ViewModel.NormalizeSideBrowserUrl());

    private void SideBrowserAddress_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel.NavigateSideBrowser();
        e.Handled = true;
    }

    private void SideBrowserWebView_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Uri)) ViewModel.SideBrowserUrl = e.Uri;
    }

    private void SideBrowserWebView_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) ViewModel.StatusText = $"browser failed · {ViewModel.SideBrowserUrl}";
    }

    private void RightPanelFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileExplorerNode node }) ViewModel.OpenExplorerNode(node);
    }

    private void ExplorerRefresh_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshExplorerTree();

    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileExplorerNode node) ViewModel.OpenExplorerNode(node);
    }

    private void FilePreviewMode_Click(object sender, RoutedEventArgs e) => ViewModel.SetFilePanelMode(false);

    private void FileRawMode_Click(object sender, RoutedEventArgs e) => ViewModel.SetFilePanelMode(true);

    private void FileSave_Click(object sender, RoutedEventArgs e) => ViewModel.SaveFilePanel();

    private void SidebarResizeThumb_MouseEnter(object sender, MouseEventArgs e)
    {
        SidebarPanel_MouseEnter(sender, e);
        UpdateSidebarResizeCursorPosition();
        SetSidebarResizeCursor(true);
    }

    private void SidebarResizeThumb_MouseMove(object sender, MouseEventArgs e)
    {
        SidebarPanel_MouseEnter(sender, e);
        UpdateSidebarResizeCursorPosition();
        SetSidebarResizeCursor(true);
    }

    private void SidebarResizeThumb_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isResizingSidebar) SetSidebarResizeCursor(false);
        SidebarPanel_MouseLeave(sender, e);
    }

    private void SidebarResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isResizingSidebar = true;
        _sidebarPeekCloseTimer.Stop();
        UpdateSidebarResizeCursorPosition();
        SetSidebarResizeCursor(true);
        _sidebarResizeRequestedWidth = ViewModel.SidebarResizeStartWidth;
        ViewModel.BeginSidebarResize();
    }

    private void SidebarResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _sidebarResizeRequestedWidth += e.HorizontalChange;
        ViewModel.PreviewSidebarResize(_sidebarResizeRequestedWidth);
        UpdateSidebarResizeCursorPosition();
    }

    private void SidebarResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ViewModel.CompleteSidebarResize(_sidebarResizeRequestedWidth);
        _isResizingSidebar = false;
        SetSidebarResizeCursor(SidebarResizeThumb.IsMouseOver);
        ScheduleSidebarPeekClose();
    }

    private void SidebarEdge_MouseEnter(object sender, MouseEventArgs e)
    {
        _sidebarPeekCloseTimer.Stop();
        ViewModel.OpenSidebarPeek();
    }

    private void SidebarPanel_MouseEnter(object sender, MouseEventArgs e) => _sidebarPeekCloseTimer.Stop();

    private void SidebarPanel_MouseLeave(object sender, MouseEventArgs e) => ScheduleSidebarPeekClose();

    private void ScheduleSidebarPeekClose()
    {
        if (_isResizingSidebar) return;
        _sidebarPeekCloseTimer.Stop();
        _sidebarPeekCloseTimer.Start();
    }

    private void UpdateSidebarResizeCursorPosition()
    {
        var pointer = Mouse.GetPosition(AppRoot);
        var x = Math.Max(0, pointer.X - 4.5);
        var y = Math.Clamp(pointer.Y - 17, 42, Math.Max(42, ActualHeight - 26 - 34));
        SidebarResizeCursor.Margin = new Thickness(x, y - 42, 0, 0);
    }

    private void SetSidebarResizeCursor(bool visible)
    {
        if (visible) UpdateSidebarResizeCursorPosition();
        if (_isSidebarResizeCursorVisible == visible) return;
        _isSidebarResizeCursorVisible = visible;
        SidebarResizeCursor.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = visible ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(visible ? 90 : 120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void RightPanelResizeThumb_MouseEnter(object sender, MouseEventArgs e)
    {
        RightPanel_MouseEnter(sender, e);
        UpdateRightPanelResizeCursorPosition();
        SetRightPanelResizeCursor(true);
    }

    private void RightPanelResizeThumb_MouseMove(object sender, MouseEventArgs e)
    {
        RightPanel_MouseEnter(sender, e);
        UpdateRightPanelResizeCursorPosition();
        SetRightPanelResizeCursor(true);
    }

    private void RightPanelResizeThumb_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isResizingRightPanel) SetRightPanelResizeCursor(false);
        RightPanel_MouseLeave(sender, e);
    }

    private void RightPanelResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isResizingRightPanel = true;
        _rightPanelPeekCloseTimer.Stop();
        UpdateRightPanelResizeCursorPosition();
        SetRightPanelResizeCursor(true);
        _rightPanelResizeRequestedWidth = ViewModel.RightPanelResizeStartWidth;
        ViewModel.BeginRightPanelResize();
    }

    private void RightPanelResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _rightPanelResizeRequestedWidth -= e.HorizontalChange;
        ViewModel.PreviewRightPanelResize(_rightPanelResizeRequestedWidth);
        UpdateRightPanelResizeCursorPosition();
    }

    private void RightPanelResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ViewModel.CompleteRightPanelResize(_rightPanelResizeRequestedWidth);
        _isResizingRightPanel = false;
        SetRightPanelResizeCursor(RightPanelResizeThumb.IsMouseOver);
        ScheduleRightPanelPeekClose();
    }

    private void RightPanelEdge_MouseEnter(object sender, MouseEventArgs e)
    {
        _rightPanelPeekCloseTimer.Stop();
        ViewModel.OpenRightPanelPeek();
    }

    private void RightPanel_MouseEnter(object sender, MouseEventArgs e) => _rightPanelPeekCloseTimer.Stop();

    private void RightPanel_MouseLeave(object sender, MouseEventArgs e) => ScheduleRightPanelPeekClose();

    private void ScheduleRightPanelPeekClose()
    {
        if (_isResizingRightPanel) return;
        _rightPanelPeekCloseTimer.Stop();
        _rightPanelPeekCloseTimer.Start();
    }

    private void UpdateRightPanelResizeCursorPosition()
    {
        var pointer = Mouse.GetPosition(AppRoot);
        var x = Math.Clamp(pointer.X - 4.5, 0, Math.Max(0, ActualWidth - 9));
        var y = Math.Clamp(pointer.Y - 17, 42, Math.Max(42, ActualHeight - 26 - 34));
        RightPanelResizeCursor.Margin = new Thickness(x, y - 42, 0, 0);
    }

    private void SetRightPanelResizeCursor(bool visible)
    {
        if (visible) UpdateRightPanelResizeCursorPosition();
        if (_isRightPanelResizeCursorVisible == visible) return;
        _isRightPanelResizeCursorVisible = visible;
        RightPanelResizeCursor.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = visible ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(visible ? 90 : 120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ReturnToChat_Click(object sender, RoutedEventArgs e) => ViewModel.ReturnToChat();

    private void UpdateIndicator_Click(object sender, RoutedEventArgs e) => ViewModel.IsUpdatePopupOpen = true;

    private async void UpdateNow_Click(object sender, RoutedEventArgs e) => await ViewModel.ApplyAppUpdateAsync();

    private async void UpdateRefresh_Click(object sender, RoutedEventArgs e) => await ViewModel.CheckForAppUpdateAsync();

    private void ProjectPath_Click(object sender, RoutedEventArgs e) => ViewModel.OpenCurrentProject();

    private void ProjectToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProjectGroupItem project, Tag: Border host }) return;
        var expand = !project.IsExpanded;
        var currentHeight = host.ActualHeight;
        ViewModel.SetProjectExpanded(project, expand);
        host.IsHitTestVisible = expand;
        var existingTranslate = host.RenderTransform as TranslateTransform;
        var translate = existingTranslate is { IsFrozen: false }
            ? existingTranslate
            : new TranslateTransform(existingTranslate?.X ?? 0, existingTranslate?.Y ?? 0);
        if (!ReferenceEquals(existingTranslate, translate)) host.RenderTransform = translate;
        if (expand)
        {
            host.Measure(new Size(host.ActualWidth > 0 ? host.ActualWidth : SidebarPanel.ActualWidth, double.PositiveInfinity));
            var targetHeight = Math.Max(1, host.DesiredSize.Height);
            host.MaxHeight = 0;
            host.Opacity = 0;
            translate.Y = -5;
            var heightAnimation = new DoubleAnimation(0, targetHeight, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            heightAnimation.Completed += (_, _) =>
            {
                host.ClearValue(MaxHeightProperty);
                host.ClearValue(OpacityProperty);
            };
            host.BeginAnimation(MaxHeightProperty, heightAnimation);
            host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(145)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-5, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        else
        {
            host.MaxHeight = Math.Max(1, currentHeight);
            host.Opacity = 1;
            var heightAnimation = new DoubleAnimation(host.MaxHeight, 0, TimeSpan.FromMilliseconds(155)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
            heightAnimation.Completed += (_, _) =>
            {
                host.ClearValue(MaxHeightProperty);
                host.ClearValue(OpacityProperty);
            };
            host.BeginAnimation(MaxHeightProperty, heightAnimation);
            host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -4, TimeSpan.FromMilliseconds(145)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop });
        }
    }

    private void ProjectDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _projectDragStart = e.GetPosition(this);
        _projectDragSource = (sender as FrameworkElement)?.DataContext as ProjectGroupItem;
        _projectDragElement = sender as FrameworkElement;
        _isProjectDragging = false;
    }

    private void ProjectDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _projectDragSource is null || _projectDragElement is null) return;
        var position = e.GetPosition(this);
        if (!_isProjectDragging)
        {
            if (Math.Abs(position.X - _projectDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _projectDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _isProjectDragging = _projectDragElement.CaptureMouse();
            if (!_isProjectDragging) return;
            _projectDragElement.Opacity = 0.52;
        }
        var targetElement = FindVisualAncestorByName(InputHitTest(position) as DependencyObject, "ProjectRow");
        if (targetElement?.DataContext is not ProjectGroupItem target) return;
        var items = FindVisualAncestor<ItemsControl>(targetElement);
        if (items is not null) AnimateItemsControlReorder(items, () => ViewModel.PreviewMoveProject(_projectDragSource, target, position.Y > targetElement.TranslatePoint(new Point(0, 0), this).Y + targetElement.ActualHeight / 2));
        e.Handled = true;
    }

    private void ProjectDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isProjectDragging)
        {
            FinishSidebarDrag(_projectDragElement);
            e.Handled = true;
        }
        _projectDragSource = null;
        _projectDragElement = null;
    }

    private void ProjectSessionDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _projectSessionDragStart = e.GetPosition(this);
        _projectSessionDragSource = (sender as FrameworkElement)?.DataContext as SessionItem;
        _projectSessionDragOwner = (sender as FrameworkElement)?.Tag as ProjectGroupItem;
        _projectSessionDragElement = sender as FrameworkElement;
        _isProjectSessionDragging = false;
    }

    private void ProjectSessionDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _projectSessionDragSource is null || _projectSessionDragOwner is null || _projectSessionDragElement is null) return;
        var position = e.GetPosition(this);
        if (!_isProjectSessionDragging)
        {
            if (Math.Abs(position.X - _projectSessionDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _projectSessionDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _isProjectSessionDragging = _projectSessionDragElement.CaptureMouse();
            if (!_isProjectSessionDragging) return;
            _projectSessionDragElement.Opacity = 0.52;
        }
        var targetElement = FindVisualAncestorByName(InputHitTest(position) as DependencyObject, "NestedSessionRow");
        if (targetElement?.DataContext is not SessionItem target
            || targetElement.Tag is not ProjectGroupItem owner
            || !ReferenceEquals(owner, _projectSessionDragOwner)) return;
        var items = FindVisualAncestor<ItemsControl>(targetElement);
        if (items is not null) AnimateItemsControlReorder(items, () => ViewModel.PreviewMoveProjectSession(owner, _projectSessionDragSource, target, position.Y > targetElement.TranslatePoint(new Point(0, 0), this).Y + targetElement.ActualHeight / 2));
        e.Handled = true;
    }

    private void ProjectSessionDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isProjectSessionDragging)
        {
            FinishSidebarDrag(_projectSessionDragElement);
            e.Handled = true;
        }
        _projectSessionDragSource = null;
        _projectSessionDragOwner = null;
        _projectSessionDragElement = null;
    }

    private void FinishSidebarDrag(FrameworkElement? element)
    {
        if (element is null) return;
        if (element.IsMouseCaptured) element.ReleaseMouseCapture();
        ViewModel.CommitSidebarOrder();
        var restore = new DoubleAnimation(element.Opacity, 1, TimeSpan.FromMilliseconds(110)) { FillBehavior = FillBehavior.Stop };
        restore.Completed += (_, _) => element.ClearValue(OpacityProperty);
        element.BeginAnimation(OpacityProperty, restore);
        _isProjectDragging = false;
        _isProjectSessionDragging = false;
    }

    private static void AnimateItemsControlReorder(ItemsControl items, Func<bool> reorder)
    {
        var before = new Dictionary<object, double>();
        for (var i = 0; i < items.Items.Count; i++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                before[items.Items[i]] = container.TranslatePoint(new Point(0, 0), items).Y;
            }
        }
        if (!reorder()) return;
        items.UpdateLayout();
        for (var i = 0; i < items.Items.Count; i++)
        {
            if (!before.TryGetValue(items.Items[i], out var oldY)
                || items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var newY = container.TranslatePoint(new Point(0, 0), items).Y;
            var delta = oldY - newY;
            if (Math.Abs(delta) < 0.5) continue;
            var transform = new TranslateTransform(0, delta);
            container.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(110)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private static FrameworkElement? FindVisualAncestorByName(DependencyObject? source, string name)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { Name: var currentName } element && currentName == name) return element;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OpenActiveLocation_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.ActiveLocationPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var location = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location)) return;
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{location}\"", UseShellExecute = true });
    }

    private void BottomProjectPath_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleBottomProjectPicker();

    private void ProjectChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectGroupItem project }) ViewModel.SelectProject(project);
    }

    private void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void ProjectNewChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectGroupItem project }) ViewModel.StartNewChatForProject(project);
        e.Handled = true;
    }

    private void ProjectPin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectGroupItem project) ViewModel.PinProject(project);
    }

    private void ProjectOpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectGroupItem project) ViewModel.OpenProjectInExplorer(project);
    }

    private void ProjectCreateWorktree_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProjectGroupItem project) return;
        var projectNameParts = project.Name.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var defaultName = $"{(projectNameParts.Length == 0 ? "worktree" : string.Join("-", projectNameParts))}-worktree";
        var name = PromptForProjectName(
            defaultName,
            T("创建永久工作树", "Create permanent worktree"),
            T("会在当前 Git 仓库旁创建一个新的 worktree 文件夹，并检出同名分支。", "Creates a new worktree folder next to the current Git repository and checks out a branch with the same name."),
            T("创建", "Create"));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!ViewModel.CreatePermanentWorktree(project, name, out var error))
        {
            MessageBox.Show(this, error, T("无法创建工作树", "Unable to create worktree"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ProjectRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProjectGroupItem project) return;
        var name = PromptForProjectName(
            project.Name,
            T("重命名项目", "Rename project"),
            T("只修改 ipi 侧栏显示名，不会重命名磁盘文件夹。", "Only changes the display name in ipi. The folder on disk is not renamed."),
            T("保存", "Save"));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!ViewModel.RenameProject(project, name, out var error))
        {
            MessageBox.Show(this, error, T("无法重命名项目", "Unable to rename project"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ProjectArchive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectGroupItem project) ViewModel.ArchiveProject(project);
    }

    private void SessionPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SessionItem session }) ViewModel.PinSession(session);
        e.Handled = true;
    }

    private void SessionArchive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SessionItem session }) ViewModel.ArchiveSession(session);
        e.Handled = true;
    }

    private void ProjectNewMenu_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleNewProjectMenu();

    private void SidebarProjectsMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenButtonMenu(sender);
    }

    private void SidebarConversationsMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenButtonMenu(sender);
    }

    private void HeaderArchiveAllChats_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (MessageBox.Show(this, T("归档当前侧边栏中的所有聊天？", "Archive all chats currently shown in the sidebar?"), T("归档所有聊天", "Archive all chats"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            ViewModel.ArchiveAllVisibleSessions();
        }
    }

    private void HeaderOrganizeByProject_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ViewModel.OrganizeSidebarByProject();
    }

    private void HeaderRecentProjects_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ViewModel.ShowRecentProjectsFirst();
    }

    private void HeaderSortChronological_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ViewModel.SortSidebarSessionsChronological();
    }

    private void HeaderSortRecent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ViewModel.SortSidebarSessionsRecent();
    }

    private void SidebarProjectNew_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ProjectCreateNamed_Click(sender, e);
    }

    private void SidebarDefaultChatNew_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ViewModel.ClearProject();
        FocusPromptBox();
    }

    private void ProjectNewBlank_Click(object sender, RoutedEventArgs e) => ProjectCreateNamed_Click(sender, e);

    private void ProjectCreateNamed_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsNewProjectMenuOpen = false;
        var name = PromptForProjectName("New project");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!ViewModel.CreateNamedProject(name, out var error))
        {
            MessageBox.Show(this, error, T("无法创建项目", "Unable to create project"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ProjectUseFolder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsNewProjectMenuOpen = false;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = T("选择项目文件夹", "Choose project folder"),
            InitialDirectory = ViewModel.IsGlobalChat ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : ViewModel.ProjectPath,
        };
        if (dialog.ShowDialog(this) == true) ViewModel.SelectProjectFolder(dialog.FolderName);
    }

    private string? PromptForProjectName(string defaultName, string? titleText = null, string? hintText = null, string? confirmText = null)
    {
        var popupBg = (Brush)(TryFindResource("PopupBg") ?? new SolidColorBrush(Color.FromRgb(43, 43, 44)));
        var popupBorder = (Brush)(TryFindResource("PopupBorder") ?? new SolidColorBrush(Color.FromRgb(72, 72, 74)));
        var rowBg = (Brush)(TryFindResource("RowBg") ?? new SolidColorBrush(Color.FromRgb(22, 24, 29)));
        var controlHover = (Brush)(TryFindResource("ControlHoverBg") ?? new SolidColorBrush(Color.FromRgb(55, 55, 56)));
        var textPrimary = (Brush)(TryFindResource("TextPrimary") ?? Brushes.White);
        var textMuted = (Brush)(TryFindResource("TextMuted") ?? new SolidColorBrush(Color.FromRgb(169, 173, 183)));
        var textDim = (Brush)(TryFindResource("TextDim") ?? new SolidColorBrush(Color.FromRgb(116, 121, 134)));
        var accent = new SolidColorBrush(Color.FromRgb(109, 141, 255));
        var dialog = new Window
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            FontFamily = TryFindResource("IpiTextFontFamily") as FontFamily ?? FontFamily,
        };

        var host = new Grid { Background = Brushes.Transparent, Margin = new Thickness(18), SnapsToDevicePixels = true };
        var shell = new Border
        {
            Width = 640,
            Background = popupBg,
            BorderBrush = popupBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24),
            SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.16 },
        };
        host.Children.Add(shell);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Child = root;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) dialog.DragMove(); };
        header.Children.Add(new TextBlock { Text = titleText ?? T("创建项目", "Create project"), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = textPrimary });
        var close = CreateProjectDialogAction("×", textMuted, Brushes.Transparent, Brushes.Transparent, 28, 28, 15);
        close.MouseLeftButtonDown += (_, _) => dialog.DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var typeBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(typeBlock, 1);
        typeBlock.Children.Add(new TextBlock { Text = T("项目类型", "Project type"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = textPrimary, Margin = new Thickness(0, 0, 0, 9) });
        var localCard = new Border { Width = 300, Height = 104, HorizontalAlignment = HorizontalAlignment.Left, Background = controlHover, BorderBrush = popupBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(15) };
        var localGrid = new Grid();
        localGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        localGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        localGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        localGrid.Children.Add(new LucideIcon { Icon = "monitor", Size = 18, Stroke = textMuted, HorizontalAlignment = HorizontalAlignment.Left });
        localGrid.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderBrush = accent, BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Child = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var localCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(localCopy, 2);
        localCopy.Children.Add(new TextBlock { Text = T("本地", "Local"), Foreground = textPrimary, FontSize = 13, FontWeight = FontWeights.SemiBold });
        localCopy.Children.Add(new TextBlock { Text = T("在你的电脑上编辑、运行和测试文件", "Edit, run, and test files on your computer"), Foreground = textDim, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        localGrid.Children.Add(localCopy);
        localCard.Child = localGrid;
        typeBlock.Children.Add(localCard);
        root.Children.Add(typeBlock);

        var nameBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        Grid.SetRow(nameBlock, 2);
        var hint = new TextBlock { Text = hintText ?? T(@"保持简短且易识别。项目会创建在 Documents\ipi-projects。", @"Keep it short and recognizable. The project will be created in Documents\ipi-projects."), FontSize = 13, Foreground = textMuted, Margin = new Thickness(0, 0, 0, 7), TextWrapping = TextWrapping.Wrap };
        nameBlock.Children.Add(hint);
        var inputShell = new Border { Background = rowBg, BorderBrush = popupBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 0, 11, 0), Height = 34 };
        var input = new TextBox { Text = defaultName, FontSize = 13, Foreground = textPrimary, Background = Brushes.Transparent, BorderThickness = new Thickness(0), CaretBrush = textPrimary, SelectionBrush = accent, VerticalContentAlignment = VerticalAlignment.Center };
        inputShell.Child = input;
        nameBlock.Children.Add(inputShell);
        root.Children.Add(nameBlock);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(actions, 3);
        var useFolder = CreateProjectDialogAction(T("使用现有文件夹", "Use existing folder"), textPrimary, controlHover, Brushes.Transparent, 104, 28, 12);
        var next = CreateProjectDialogAction(confirmText ?? T("创建", "Create"), Brushes.White, accent, accent, 78, 28, 12);
        useFolder.Margin = new Thickness(0, 0, 12, 0);
        useFolder.MouseLeftButtonDown += (_, _) => { dialog.DialogResult = false; ProjectUseFolder_Click(this, new RoutedEventArgs()); };
        next.MouseLeftButtonDown += (_, _) => TrySave();
        actions.Children.Add(useFolder);
        actions.Children.Add(next);
        root.Children.Add(actions);

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.DialogResult = false;
            else if (e.Key == Key.Enter) TrySave();
        };

        void TrySave()
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                inputShell.BorderBrush = new SolidColorBrush(Color.FromRgb(232, 99, 99));
                hint.Text = T("请输入一个项目名称。", "Enter a project name.");
                hint.Foreground = new SolidColorBrush(Color.FromRgb(232, 99, 99));
                input.Focus();
                return;
            }
            dialog.DialogResult = true;
        }

        Border CreateProjectDialogAction(string text, Brush foreground, Brush background, Brush borderBrush, double minWidth, double height, double fontSize)
        {
            return new Border
            {
                MinWidth = minWidth,
                Height = height,
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = borderBrush == Brushes.Transparent ? new Thickness(0) : new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 0, 13, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, Foreground = foreground, FontSize = fontSize, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        }

        dialog.Content = host;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void ProjectNoProject_Click(object sender, RoutedEventArgs e) => ViewModel.ClearProject();

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = !ViewModel.IsGlobalChat && Directory.Exists(ViewModel.ProjectPath)
            ? ViewModel.ProjectPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("选择要附加到本轮对话的文件", "Choose files to attach"),
            InitialDirectory = initialDirectory,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) ViewModel.AddFiles(dialog.FileNames);
    }

    private void AttachmentRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AttachmentItem item }) ViewModel.RemoveAttachment(item);
    }

    private void ChatAttachmentContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { Items.Count: >= 4 } menu) return;
        if (menu.Items[0] is MenuItem copyImage) copyImage.Header = T("复制图片", "Copy image");
        if (menu.Items[1] is MenuItem copyFile) copyFile.Header = T("复制图片文件", "Copy image file");
        if (menu.Items[3] is MenuItem openLocation) openLocation.Header = T("打开所在位置", "Open file location");
    }

    private void ChatAttachmentCopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ChatAttachmentPreviewItem item } || !File.Exists(item.Path)) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(item.Path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            Clipboard.SetImage(image);
            ViewModel.StatusText = T("图片已复制", "image copied");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = T($"复制图片失败 · {ex.Message}", $"image copy failed · {ex.Message}");
        }
    }

    private void ChatAttachmentCopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ChatAttachmentPreviewItem item } || !File.Exists(item.Path)) return;
        try
        {
            Clipboard.SetFileDropList(new StringCollection { item.Path });
            ViewModel.StatusText = T("图片文件已复制", "image file copied");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = T($"复制图片文件失败 · {ex.Message}", $"image file copy failed · {ex.Message}");
        }
    }

    private void ChatAttachmentOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ChatAttachmentPreviewItem item } || !File.Exists(item.Path)) return;
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{item.Path}\"", UseShellExecute = true });
    }

    private void MainApproval_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleApprovalPicker(false);

    private void ChatApproval_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleApprovalPicker(true);

    private void BottomApproval_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleBottomApprovalPicker();

    private void BottomUsage_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSessionInfoPopup();
        Dispatcher.BeginInvoke((Action)ConstrainSessionInfoPopupToWindow, DispatcherPriority.Loaded);
    }

    private void ConstrainSessionInfoPopupToWindow()
    {
        if (!ViewModel.IsSessionInfoPopupOpen) return;
        var maxWidth = Math.Max(420, ActualWidth - 32);
        var popupWidth = Math.Min(720, maxWidth);
        SessionInfoPopupBorder.Width = popupWidth;

        var targetLeft = BottomUsageInfoHost.TransformToAncestor(this).Transform(new Point(0, 0)).X;
        var minLeft = 12.0;
        var maxLeft = Math.Max(minLeft, ActualWidth - popupWidth - 12);
        var desiredLeft = Math.Clamp(targetLeft, minLeft, maxLeft);
        SessionInfoPopup.HorizontalOffset = desiredLeft - targetLeft;
    }

    private void ApprovalOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ApprovalOptionItem option }) return;
        if (option.RequiresConfigChoice)
        {
            var rules = PromptForApprovalRules();
            if (rules is null) return;
            var result = ViewModel.CreateWorkspaceApprovalConfigTemplate(rules);
            ViewModel.StatusText = result.Message;
            return;
        }

        ViewModel.SelectApprovalOption(option);
    }

    private IReadOnlyDictionary<string, string>? PromptForApprovalRules()
    {
        var popupBg = (Brush)(TryFindResource("PopupBg") ?? new SolidColorBrush(Color.FromRgb(43, 43, 44)));
        var popupBorder = (Brush)(TryFindResource("PopupBorder") ?? new SolidColorBrush(Color.FromRgb(72, 72, 74)));
        var rowBg = (Brush)(TryFindResource("RowBg") ?? new SolidColorBrush(Color.FromRgb(22, 24, 29)));
        var chipBg = (Brush)(TryFindResource("ComposerChipBg") ?? rowBg);
        var textPrimary = (Brush)(TryFindResource("TextPrimary") ?? Brushes.White);
        var textMuted = (Brush)(TryFindResource("TextMuted") ?? new SolidColorBrush(Color.FromRgb(169, 173, 183)));
        var textDim = (Brush)(TryFindResource("TextDim") ?? new SolidColorBrush(Color.FromRgb(116, 121, 134)));
        var accent = new SolidColorBrush(Color.FromRgb(109, 141, 255));
        var selectedFill = new SolidColorBrush(Color.FromArgb(34, 109, 141, 255));
        var selectedBorder = new SolidColorBrush(Color.FromArgb(150, 109, 141, 255));
        string T(string zh, string en) => ViewModel.IsEnglishUi ? en : zh;
        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bash"] = "ask",
            ["edit"] = "ask",
            ["write"] = "ask",
            ["read_outside_workspace"] = "ask",
        };

        var dialog = new Window
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
            ShowInTaskbar = false,
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.DialogResult = false;
        };

        var host = new Grid
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(18),
            SnapsToDevicePixels = true,
        };

        var shell = new Border
        {
            Width = 560,
            Background = popupBg,
            BorderBrush = popupBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18),
            SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 26,
                ShadowDepth = 0,
                Opacity = 0.18,
            },
        };
        var root = new StackPanel();
        shell.Child = root;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new StackPanel();
        headerText.Children.Add(new TextBlock
        {
            Text = T("自定义审批规则", "Custom approval rules"),
            FontSize = 19,
            FontWeight = FontWeights.Medium,
            Foreground = textPrimary,
        });
        headerText.Children.Add(new TextBlock
        {
            Text = T("按工具类型细分，不再复用三档预设。保存到当前工作区 .ipi/config.toml。", "Configure each tool type separately. Saves to the current workspace .ipi/config.toml."),
            FontSize = 12.5,
            Foreground = textMuted,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(headerText);
        var close = CreateTextAction("×", textMuted, Brushes.Transparent, Brushes.Transparent, 30, 30);
        close.MouseLeftButtonDown += (_, _) => dialog.DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        void AddRuleRow(string key, string title, string detail)
        {
            var row = new Border
            {
                Background = rowBg,
                BorderBrush = popupBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(13, 11, 12, 11),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new StackPanel();
            copy.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.Medium, Foreground = textPrimary });
            copy.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = textDim, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(copy);

            var segmentShell = new Border
            {
                Background = chipBg,
                BorderBrush = popupBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18, 0, 0, 0),
            };
            var segment = new StackPanel { Orientation = Orientation.Horizontal };
            segmentShell.Child = segment;
            var ask = CreateSegmentChip(T("询问", "Ask"));
            var allow = CreateSegmentChip(T("允许", "Allow"));
            segment.Children.Add(ask.Chip);
            segment.Children.Add(allow.Chip);
            Grid.SetColumn(segmentShell, 1);
            grid.Children.Add(segmentShell);
            row.Child = grid;

            void Refresh()
            {
                var isAsk = rules[key] == "ask";
                PaintSegment(ask.Chip, ask.Label, isAsk);
                PaintSegment(allow.Chip, allow.Label, !isAsk);
            }

            ask.Chip.MouseLeftButtonDown += (_, _) => { rules[key] = "ask"; Refresh(); };
            allow.Chip.MouseLeftButtonDown += (_, _) => { rules[key] = "allow"; Refresh(); };
            Refresh();
            root.Children.Add(row);
        }

        (Border Chip, TextBlock Label) CreateSegmentChip(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var chip = new Border
            {
                MinWidth = 56,
                Height = 28,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 0, 12, 0),
                Cursor = Cursors.Hand,
                Child = label,
            };
            return (chip, label);
        }

        void PaintSegment(Border chip, TextBlock label, bool selected)
        {
            chip.Background = selected ? selectedFill : Brushes.Transparent;
            chip.BorderBrush = selected ? selectedBorder : Brushes.Transparent;
            chip.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            label.Foreground = selected ? accent : textMuted;
        }

        Border CreateTextAction(string text, Brush foreground, Brush background, Brush borderBrush, double minWidth, double height)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = text == "×" ? 18 : 13,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return new Border
            {
                MinWidth = minWidth,
                Height = height,
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = borderBrush == Brushes.Transparent ? new Thickness(0) : new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(13, 0, 13, 0),
                Cursor = Cursors.Hand,
                Child = label,
            };
        }

        AddRuleRow("bash", T("Shell 命令", "Shell commands"), T("bash / shell 命令是否需要批准。", "Whether bash / shell commands need approval."));
        AddRuleRow("edit", T("编辑文件", "Edit files"), T("对已有文件做精确替换时是否需要批准。", "Whether exact replacements in existing files need approval."));
        AddRuleRow("write", T("写入/创建文件", "Write/create files"), T("创建或覆盖文件时是否需要批准。", "Whether creating or overwriting files needs approval."));
        AddRuleRow("read_outside_workspace", T("读取工作区外文件", "Read outside workspace"), T("读取当前项目目录外路径时是否需要批准。", "Whether reading paths outside the current project needs approval."));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = CreateTextAction(T("取消", "Cancel"), textMuted, Brushes.Transparent, popupBorder, 84, 32);
        var save = CreateTextAction(T("保存", "Save"), Brushes.White, accent, accent, 88, 32);
        cancel.MouseLeftButtonDown += (_, _) => dialog.DialogResult = false;
        save.MouseLeftButtonDown += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        root.Children.Add(actions);

        host.Children.Add(shell);
        dialog.Content = host;
        return dialog.ShowDialog() == true ? rules : null;
    }

    private void Tools_Click(object sender, RoutedEventArgs e) => ViewModel.CycleToolPreset();

    private void MainThinking_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleThinkingPicker(false);

    private void ChatThinking_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleThinkingPicker(true);

    private void ReasoningOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ReasoningOptionItem option }) ViewModel.SelectReasoningOption(option);
    }

    private void ThinkingSpectrumSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => ViewModel.SetThinkingSliderInteracting(true);

    private void ThinkingSpectrumSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => ViewModel.SetThinkingSliderInteracting(false);

    private void ThinkingSpectrumSlider_LostMouseCapture(object sender, MouseEventArgs e)
        => ViewModel.SetThinkingSliderInteracting(false);

    private void HeroLogo_Loaded(object sender, RoutedEventArgs e) => RestartHeroLogoAnimation();

    private void RestartHeroLogoAnimation()
    {
        if (!IsLoaded || HeroLogoPaint is null || HeroLogoHost is null || HeroLogoScale is null) return;
        var baseColor = ResourceColor("TextPrimary", Color.FromRgb(36, 39, 51));
        var shineColor = ResourceColor("TextMuted", Color.FromRgb(98, 106, 122));
        var dim = Color.FromArgb(235, baseColor.R, baseColor.G, baseColor.B);
        var shine = Color.FromArgb(215, shineColor.R, shineColor.G, shineColor.B);
        var translate = new TranslateTransform(-1.18, 0);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            RelativeTransform = translate,
        };
        brush.GradientStops.Add(new GradientStop(dim, 0.00));
        brush.GradientStops.Add(new GradientStop(dim, 0.34));
        brush.GradientStops.Add(new GradientStop(shine, 0.50));
        brush.GradientStops.Add(new GradientStop(dim, 0.66));
        brush.GradientStops.Add(new GradientStop(dim, 1.00));
        HeroLogoPaint.Fill = brush;

        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = -1.18,
            To = 1.18,
            Duration = TimeSpan.FromSeconds(3.35),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        HeroLogoHost.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.84,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(2.4),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        var scaleAnimation = new DoubleAnimation
        {
            From = 0.992,
            To = 1.012,
            Duration = TimeSpan.FromSeconds(3.15),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        HeroLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        HeroLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());
    }

    private void ChatMessageText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock textBlock || textBlock.DataContext is not PanelRow { Kind: "thinking" }) return;
        textBlock.Foreground = CreateThinkingTextBrush(textBlock.FontSize, out var translate);
        StartThinkingTextBrushAnimation(translate, textBlock.FontSize);
    }

    private void ChatSelectableTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not PanelRow { Kind: "thinking" }) return;
        textBox.Foreground = CreateThinkingTextBrush(textBox.FontSize, out var translate);
        StartThinkingTextBrushAnimation(translate, textBox.FontSize);
    }

    private LinearGradientBrush CreateThinkingTextBrush(double fontSize, out TranslateTransform translate)
    {
        var muted = ResourceColor(fontSize >= 13 ? "TextMuted" : "TextDim", Color.FromRgb(132, 141, 158));
        var bright = ResourceColor(fontSize >= 13 ? "TextPrimary" : "TextMuted", Color.FromRgb(210, 214, 224));
        var dim = Color.FromArgb(fontSize >= 13 ? (byte)170 : (byte)125, muted.R, muted.G, muted.B);
        var highlight = Color.FromArgb(fontSize >= 13 ? (byte)230 : (byte)165, bright.R, bright.G, bright.B);
        translate = new TranslateTransform(-1.15, 0);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            RelativeTransform = translate,
        };
        brush.GradientStops.Add(new GradientStop(dim, 0.00));
        brush.GradientStops.Add(new GradientStop(dim, 0.38));
        brush.GradientStops.Add(new GradientStop(highlight, 0.50));
        brush.GradientStops.Add(new GradientStop(dim, 0.62));
        brush.GradientStops.Add(new GradientStop(dim, 1.00));
        return brush;
    }

    private static void StartThinkingTextBrushAnimation(TranslateTransform translate, double fontSize)
    {
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = -1.15,
            To = 1.15,
            Duration = TimeSpan.FromSeconds(fontSize >= 13 ? 2.7 : 3.15),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private Color ResourceColor(string key, Color fallback)
    {
        return TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;
    }

    private void ChatMessageTextSelection_MouseUp(object sender, MouseButtonEventArgs e) => CaptureChatTextSelection(sender);

    private void ChatMessageTextSelection_KeyUp(object sender, KeyEventArgs e) => CaptureChatTextSelection(sender);

    private void CaptureChatTextSelection(object? sender)
    {
        if (sender is TextBox { SelectedText.Length: > 0 } textBox) ViewModel.CaptureSelectedChatText(textBox.SelectedText);
    }

    private void SelectionAddToConversation_Click(object sender, RoutedEventArgs e) => ViewModel.AddSelectedTextToConversation();

    private void SelectionAskInSideChat_Click(object sender, RoutedEventArgs e) => ViewModel.AskSelectedTextInSideChat();

    private void MessageAddToConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PanelRow row }) ViewModel.AddRowToConversation(row);
    }

    private void MessageAskInSideChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PanelRow row }) ViewModel.AskRowInSideChat(row);
    }

    private void MessageInlineLink_Click(object? sender, string target)
    {
        ViewModel.OpenMessageTarget(target);
    }

    private void MessageCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PanelRow row }) return;
        var text = MainWindowViewModel.MessageTextForClipboard(row);
        if (string.IsNullOrWhiteSpace(text)) return;
        Clipboard.SetText(text);
        ViewModel.StatusText = ViewModel.IsEnglishUi ? "message copied" : "消息已复制";
    }

    private void MessageNewSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PanelRow row })
        {
            ViewModel.NewSessionFromRow(row);
            FocusPromptBox();
        }
    }

    private void MessageEditFromHere_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PanelRow row })
        {
            ViewModel.EditFromHere(row);
            FocusPromptBox();
        }
    }

    private void SideChatNew_Click(object sender, RoutedEventArgs e) => ViewModel.NewTemporarySideChat();

    private async void SideChatSend_Click(object sender, RoutedEventArgs e) => await ViewModel.SendSideChatAsync();

    private async void SideChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            await ViewModel.SendSideChatAsync();
            e.Handled = true;
        }
    }

    private void ModelMenu_Click(object sender, RoutedEventArgs e) => ViewModel.OpenModelPickerFromComposer();

    private void ModelOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ModelOptionItem option }) ViewModel.SelectModelOption(option);
    }

    private void VoicePicker_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleVoicePicker();

    private void VoiceOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VoiceOptionItem option }) ViewModel.SelectVoiceOption(option);
    }

    private void ChatRail_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChatMarkerScrollViewer.ScrollToVerticalOffset(ChatMarkerScrollViewer.VerticalOffset - e.Delta * 0.45);
        e.Handled = true;
    }

    private void ChatRail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject)?.DataContext is ChatMarkerItem) return;

        _isDraggingChatRail = true;
        _suppressNextChatMarkerClick = false;
        _chatRailDragStart = e.GetPosition(ChatMarkerScrollViewer);
        _chatRailDragStartOffset = ChatMarkerScrollViewer.VerticalOffset;
        if (sender is UIElement element) element.CaptureMouse();
    }

    private void ChatRail_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingChatRail || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(ChatMarkerScrollViewer);
        var delta = current.Y - _chatRailDragStart.Y;
        if (Math.Abs(delta) > 2) _suppressNextChatMarkerClick = true;
        ChatMarkerScrollViewer.ScrollToVerticalOffset(_chatRailDragStartOffset - delta);
        e.Handled = true;
    }

    private void ChatRail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingChatRail) return;
        _isDraggingChatRail = false;
        if (sender is UIElement element) element.ReleaseMouseCapture();
        if (_suppressNextChatMarkerClick) e.Handled = true;
    }

    private void ChatItemsControl_Loaded(object sender, RoutedEventArgs e)
    {
        _chatScrollViewer = FindVisualChild<ScrollViewer>(ChatItemsControl);
        UpdateChatExternalScrollBar();
    }

    private void ScheduleChatScrollToLatest()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)ScheduleChatScrollToLatest, DispatcherPriority.ContextIdle);
            return;
        }

        if (_chatScrollToLatestPending) return;
        _chatScrollToLatestPending = true;
        Dispatcher.BeginInvoke((Action)ScrollChatToLatest, DispatcherPriority.Render);
    }

    private void ScrollChatToLatest()
    {
        try
        {
            if (ViewModel.IsAgentRunning && !_chatAutoFollowEnabled && !_forceNextChatScrollToLatest)
            {
                UpdateChatExternalScrollBar();
                return;
            }
            _forceNextChatScrollToLatest = false;

            _isProgrammaticChatScroll = true;
            ChatScrollViewer.ScrollToEnd();
            ChatMarkerScrollViewer.ScrollToEnd();
            _chatAutoFollowEnabled = true;
            UpdateChatExternalScrollBar();
            Dispatcher.BeginInvoke((Action)(() => _isProgrammaticChatScroll = false), DispatcherPriority.Background);
        }
        finally
        {
            _chatScrollToLatestPending = false;
        }
    }

    private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var contentHeightChanged = Math.Abs(e.ExtentHeightChange) > 0.1;
        if (!_isProgrammaticChatScroll && Math.Abs(e.VerticalChange) > 0.1 && !contentHeightChanged) _chatAutoFollowEnabled = IsChatNearBottom();
        if (contentHeightChanged && _chatAutoFollowEnabled) ScheduleChatScrollToLatest();
        UpdateChatExternalScrollBar();
    }

    private void EnableChatAutoFollowForNewPrompt()
    {
        _chatAutoFollowEnabled = true;
        _forceNextChatScrollToLatest = true;
    }

    private bool IsChatNearBottom()
    {
        const double tolerance = 48;
        return ChatScrollViewer.ScrollableHeight <= 0 || ChatScrollViewer.ScrollableHeight - ChatScrollViewer.VerticalOffset <= tolerance;
    }

    private void ChatExternalScrollHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var metrics = GetChatExternalScrollMetrics();
        if (metrics.MaxScroll <= 0 || metrics.MaxThumbTop <= 0) return;

        var point = e.GetPosition(ChatExternalScrollHost);
        if (point.Y < metrics.ThumbTop || point.Y > metrics.ThumbTop + metrics.ThumbHeight)
        {
            var targetTop = Math.Clamp(point.Y - metrics.ThumbHeight / 2, 0, metrics.MaxThumbTop);
            ChatScrollViewer.ScrollToVerticalOffset(targetTop / metrics.MaxThumbTop * metrics.MaxScroll);
            metrics = GetChatExternalScrollMetrics();
        }

        _isDraggingChatExternalScroll = true;
        _chatExternalScrollDragStart = e.GetPosition(ChatExternalScrollHost);
        _chatExternalScrollDragStartOffset = ChatScrollViewer.VerticalOffset;
        ChatExternalScrollHost.CaptureMouse();
        e.Handled = true;
    }

    private void ChatExternalScrollHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingChatExternalScroll || e.LeftButton != MouseButtonState.Pressed) return;
        var metrics = GetChatExternalScrollMetrics();
        if (metrics.MaxScroll <= 0 || metrics.MaxThumbTop <= 0) return;

        var current = e.GetPosition(ChatExternalScrollHost);
        var delta = current.Y - _chatExternalScrollDragStart.Y;
        var targetOffset = _chatExternalScrollDragStartOffset + delta / metrics.MaxThumbTop * metrics.MaxScroll;
        ChatScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0, metrics.MaxScroll));
        e.Handled = true;
    }

    private void ChatExternalScrollHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingChatExternalScroll) return;
        _isDraggingChatExternalScroll = false;
        ChatExternalScrollHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateChatExternalScrollBar()
    {
        if (ChatExternalScrollHost.Visibility != Visibility.Visible || ChatExternalScrollHost.ActualHeight < 40)
        {
            ChatExternalScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }

        var metrics = GetChatExternalScrollMetrics();
        ChatExternalScrollThumb.Height = metrics.ThumbHeight;
        ChatExternalScrollThumb.Margin = new Thickness(0, metrics.ThumbTop, 0, 0);
        ChatExternalScrollThumb.Visibility = metrics.MaxScroll <= 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private (double TrackHeight, double ThumbHeight, double ThumbTop, double MaxThumbTop, double MaxScroll) GetChatExternalScrollMetrics()
    {
        var trackHeight = Math.Max(40, ChatExternalScrollHost.ActualHeight);
        var maxScroll = Math.Max(0, ChatScrollViewer.ScrollableHeight);
        var extent = Math.Max(1, ChatScrollViewer.ViewportHeight + maxScroll);
        const double initialThumbHeight = 70;
        const double minimumThumbHeight = 34;
        var maxThumbHeight = Math.Max(minimumThumbHeight, Math.Min(initialThumbHeight, trackHeight));
        var proportionalHeight = trackHeight * ChatScrollViewer.ViewportHeight / extent;
        var thumbHeight = Math.Clamp(proportionalHeight, minimumThumbHeight, maxThumbHeight);
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var thumbTop = maxScroll <= 0 || maxThumbTop <= 0 ? 0 : Math.Clamp(ChatScrollViewer.VerticalOffset / maxScroll * maxThumbTop, 0, maxThumbTop);
        return (trackHeight, thumbHeight, thumbTop, maxThumbTop, maxScroll);
    }

    private void ChatMarker_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button { DataContext: ChatMarkerItem marker }) ViewModel.SetChatMarkerHover(marker);
    }

    private void ChatMarker_MouseLeave(object sender, MouseEventArgs e) => ViewModel.SetChatMarkerHover(null);

    private void ChatMarker_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressNextChatMarkerClick)
        {
            _suppressNextChatMarkerClick = false;
            return;
        }

        if (sender is not Button { DataContext: ChatMarkerItem marker }) return;
        JumpChatToRow(marker.RowIndex);
    }

    private void ChatMarkerPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMarkerItem marker }) return;
        JumpChatToRow(marker.RowIndex);
        e.Handled = true;
    }

    private void JumpChatToRow(int rowIndex)
    {
        if (ViewModel.LoadSessionWindowForMarker(rowIndex)) return;
        if (rowIndex < 0 || rowIndex >= ViewModel.ContentRows.Count) return;

        ChatItemsControl.UpdateLayout();
        ChatScrollViewer.UpdateLayout();
        if (ChatItemsControl.ItemContainerGenerator.ContainerFromIndex(rowIndex) is FrameworkElement container)
        {
            var position = container.TransformToAncestor(ChatScrollViewer).Transform(new Point(0, 0));
            var target = ChatScrollViewer.VerticalOffset + position.Y - 24;
            ChatScrollViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, ChatScrollViewer.ScrollableHeight));
            _chatAutoFollowEnabled = IsChatNearBottom();
            UpdateChatExternalScrollBar();
            return;
        }

        var rows = Math.Max(1, ViewModel.ContentRows.Count - 1);
        var ratio = Math.Clamp(rowIndex / (double)rows, 0, 1);
        ChatScrollViewer.ScrollToVerticalOffset(ChatScrollViewer.ScrollableHeight * ratio);
        _chatAutoFollowEnabled = IsChatNearBottom();
        UpdateChatExternalScrollBar();
    }

    private static T? FindVisualChild<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(source);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ToolApprovalApprove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolApprovalRequestItem item }) ViewModel.ResolveToolApproval(item, ToolApprovalDecisionKind.Allow);
    }

    private void ToolApprovalAlwaysAllow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolApprovalRequestItem item }) ViewModel.ResolveToolApproval(item, ToolApprovalDecisionKind.AlwaysAllow);
    }

    private void ToolApprovalDeny_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolApprovalRequestItem item }) ViewModel.ResolveToolApproval(item, ToolApprovalDecisionKind.Deny);
    }

    private void ToolApprovalGuide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolApprovalRequestItem item } && item.CanSendGuidance)
        {
            ViewModel.ResolveToolApproval(item, ToolApprovalDecisionKind.Guide);
        }
    }

    private void ToolApprovalGuidance_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: ToolApprovalRequestItem item } || !item.CanSendGuidance) return;
        e.Handled = true;
        ViewModel.ResolveToolApproval(item, ToolApprovalDecisionKind.Guide);
    }

    private async void Voice_Click(object sender, RoutedEventArgs e)
    {
        FocusPromptBox();
        await ViewModel.ToggleVoiceInputAsync();
    }

    private void FocusPromptBox()
    {
        var box = ViewModel.IsChatMode ? ChatPromptBox : MainPromptBox;
        box.Focus();
        Keyboard.Focus(box);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        ViewModel.CancelActiveOperations();
        _completionSound.Dispose();
        _settingsViewModel.Dispose();
        _settingsWindow?.Close();
    }

    private static void SendWindowsDictationHotkey()
    {
        keybd_event(VkLeftWindows, 0, 0, UIntPtr.Zero);
        keybd_event(VkH, 0, 0, UIntPtr.Zero);
        keybd_event(VkH, 0, KeyEventUp, UIntPtr.Zero);
        keybd_event(VkLeftWindows, 0, KeyEventUp, UIntPtr.Zero);
    }
}
