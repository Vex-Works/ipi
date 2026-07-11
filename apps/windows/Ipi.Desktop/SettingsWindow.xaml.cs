using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Ipi.Desktop.Services;

namespace Ipi.Desktop;

public partial class SettingsWindow
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int DwmRound = 2;
    private const int DwmBackdropAcrylic = 3;

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

    public SettingsWindow(SettingsWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SettingsChanged += ApplyAppearanceSettings;
        Loaded += (_, _) =>
        {
            ApplyAppearanceSettings(viewModel.CurrentSettings);
            Dispatcher.BeginInvoke(() =>
            {
                var target = viewModel.IsModelsSection ? ModelsSettingsButton
                    : viewModel.IsSkillsSection ? SkillsSettingsButton
                    : viewModel.IsPackagesSection ? PackagesSettingsButton
                    : viewModel.IsArchiveSection ? ArchivedSettingsButton
                    : viewModel.IsStorageSection ? StorageSettingsButton
                    : viewModel.IsDiagnosticsSection ? DiagnosticsSettingsButton
                    : AppearanceSettingsButton;
                target.Focus();
                Keyboard.Focus(target);
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
        Closing += (_, _) => viewModel.CancelActiveOperations();
        Closed += (_, _) => viewModel.SettingsChanged -= ApplyAppearanceSettings;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowCorners();
        if (DataContext is SettingsWindowViewModel viewModel) ApplyDwmAcrylicBackdrop(viewModel.CurrentSettings);
    }

    private void ApplyWindowCorners()
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
            // Rounded corners are best-effort on older Windows builds.
        }
    }

    private void ApplyDwmAcrylicBackdrop(AppearanceSettings settings)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            {
                target.BackgroundColor = Colors.Transparent;
            }

            var normalized = settings.Normalize();
            var darkMode = normalized.EffectiveMode() == "light" ? 0 : 1;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int));

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var backdrop = DwmBackdropAcrylic;
            _ = DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch
        {
            // System backdrop is best-effort. Unsupported builds keep the solid settings shell.
        }
    }

    private void ApplyAppearanceSettings(AppearanceSettings settings)
    {
        settings = settings.Normalize();
        Opacity = 1.0;
        ApplyTypographySettings(settings);
        ApplyDwmAcrylicBackdrop(settings);

        var mode = settings.EffectiveMode();
        var alpha = (byte)Math.Clamp(0xF0 - settings.WindowTransparency * 2.3, 0x86, 0xF0);
        var sidebarAlpha = (byte)Math.Clamp(0xF2 - settings.WindowTransparency * 2.1, 0x88, 0xF2);
        var cardAlpha = (byte)Math.Clamp(0xFA - settings.WindowTransparency * 1.8, 0xAA, 0xFA);

        if (settings.Theme == "broadsheet")
        {
            if (mode == "dark")
            {
                SetBrush("SettingsBg", Color.FromArgb(alpha, 17, 25, 103));
                SetBrush("SettingsSidebarBg", Color.FromArgb(sidebarAlpha, 13, 19, 85));
                SetBrush("SettingsCard", Color.FromArgb(cardAlpha, 17, 25, 103));
                SetBrush("SettingsText", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsMuted", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsDim", Color.FromRgb(198, 202, 255));
                SetBrush("SettingsLine", Color.FromArgb(72, 232, 230, 224));
                SetBrush("SettingsWindowBorder", Color.FromArgb(150, 232, 230, 224));
                SetBrush("SettingsControlBg", Color.FromRgb(13, 19, 85));
                SetBrush("SettingsControlBorder", Color.FromArgb(170, 232, 230, 224));
                SetBrush("SettingsHoverBg", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsPressedBg", Color.FromRgb(11, 16, 72));
                SetBrush("SettingsPopupBg", Color.FromRgb(13, 19, 85));
                SetBrush("SettingsSegmentBg", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsSidebarSelectedBg", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsFooterBg", Color.FromRgb(13, 19, 85));
                SetBrush("SettingsPreviewBg", Color.FromRgb(17, 25, 103));
                SetBrush("SettingsCaret", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsSelection", Color.FromArgb(110, 232, 230, 224));
            }
            else
            {
                SetBrush("SettingsBg", Color.FromArgb(alpha, 232, 230, 224));
                SetBrush("SettingsSidebarBg", Color.FromArgb(sidebarAlpha, 232, 230, 224));
                SetBrush("SettingsCard", Color.FromArgb(cardAlpha, 232, 230, 224));
                SetBrush("SettingsText", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsMuted", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsDim", Color.FromRgb(75, 85, 170));
                SetBrush("SettingsLine", Color.FromArgb(58, 25, 37, 170));
                SetBrush("SettingsWindowBorder", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsControlBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsControlBorder", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsHoverBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsPressedBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsPopupBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsSegmentBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsSidebarSelectedBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsFooterBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsPreviewBg", Color.FromRgb(232, 230, 224));
                SetBrush("SettingsCaret", Color.FromRgb(25, 37, 170));
                SetBrush("SettingsSelection", Color.FromArgb(92, 25, 37, 170));
            }
        }
        else if (settings.Theme == "candy-block")
        {
            if (mode == "dark")
            {
                SetBrush("SettingsBg", Color.FromRgb(22, 22, 22));
                SetBrush("SettingsSidebarBg", Color.FromRgb(22, 22, 22));
                SetBrush("SettingsCard", Color.FromRgb(22, 22, 22));
                SetBrush("SettingsText", Color.FromRgb(255, 252, 246));
                SetBrush("SettingsMuted", Color.FromRgb(204, 204, 204));
                SetBrush("SettingsDim", Color.FromRgb(96, 96, 96));
                SetBrush("SettingsLine", Color.FromRgb(51, 51, 51));
                SetBrush("SettingsWindowBorder", Color.FromRgb(196, 201, 221));
                SetBrush("SettingsControlBg", Color.FromRgb(45, 32, 43));
                SetBrush("SettingsControlBorder", Color.FromRgb(255, 131, 218));
                SetBrush("SettingsHoverBg", Color.FromRgb(45, 32, 43));
                SetBrush("SettingsPressedBg", Color.FromRgb(67, 42, 61));
                SetBrush("SettingsPopupBg", Color.FromRgb(22, 22, 22));
                SetBrush("SettingsSegmentBg", Color.FromRgb(45, 32, 43));
                SetBrush("SettingsSidebarSelectedBg", Color.FromRgb(45, 32, 43));
                SetBrush("SettingsFooterBg", Color.FromRgb(22, 22, 22));
                SetBrush("SettingsPreviewBg", Color.FromRgb(22, 22, 22));
                SetBrush("SettingsCaret", Color.FromRgb(255, 252, 246));
                SetBrush("SettingsSelection", Color.FromRgb(255, 197, 238));
            }
            else
            {
                SetBrush("SettingsBg", Color.FromArgb(alpha, 255, 255, 255));
                SetBrush("SettingsSidebarBg", Color.FromArgb(sidebarAlpha, 255, 252, 246));
                SetBrush("SettingsCard", Color.FromArgb(cardAlpha, 255, 252, 246));
                SetBrush("SettingsText", Color.FromRgb(0, 0, 0));
                SetBrush("SettingsMuted", Color.FromRgb(51, 51, 51));
                SetBrush("SettingsDim", Color.FromRgb(140, 140, 140));
                SetBrush("SettingsLine", Color.FromRgb(204, 204, 204));
                SetBrush("SettingsWindowBorder", Color.FromRgb(196, 201, 221));
                SetBrush("SettingsControlBg", Color.FromRgb(255, 252, 246));
                SetBrush("SettingsControlBorder", Color.FromRgb(196, 201, 221));
                SetBrush("SettingsHoverBg", Color.FromRgb(247, 225, 247));
                SetBrush("SettingsPressedBg", Color.FromRgb(255, 197, 238));
                SetBrush("SettingsPopupBg", Color.FromRgb(255, 252, 246));
                SetBrush("SettingsSegmentBg", Color.FromRgb(247, 225, 247));
                SetBrush("SettingsSidebarSelectedBg", Color.FromRgb(255, 197, 238));
                SetBrush("SettingsFooterBg", Color.FromRgb(255, 255, 255));
                SetBrush("SettingsPreviewBg", Color.FromRgb(255, 255, 255));
                SetBrush("SettingsCaret", Color.FromRgb(0, 0, 0));
                SetBrush("SettingsSelection", Color.FromRgb(255, 197, 238));
            }
        }
        else if (mode == "light")
        {
            SetBrush("SettingsBg", Color.FromArgb(alpha, 238, 241, 247));
            SetBrush("SettingsSidebarBg", Color.FromArgb(sidebarAlpha, 232, 237, 247));
            SetBrush("SettingsCard", Color.FromArgb(cardAlpha, 255, 255, 255));
            SetBrush("SettingsText", Color.FromRgb(36, 39, 51));
            SetBrush("SettingsMuted", Color.FromRgb(98, 106, 122));
            SetBrush("SettingsDim", Color.FromRgb(101, 109, 125));
            SetBrush("SettingsLine", Color.FromRgb(204, 212, 229));
            SetBrush("SettingsWindowBorder", Color.FromRgb(174, 184, 206));
            SetBrush("SettingsControlBg", Color.FromRgb(232, 237, 249));
            SetBrush("SettingsControlBorder", Color.FromRgb(203, 212, 234));
            SetBrush("SettingsHoverBg", Color.FromRgb(221, 228, 243));
            SetBrush("SettingsPressedBg", Color.FromRgb(209, 218, 237));
            SetBrush("SettingsPopupBg", Color.FromRgb(248, 250, 254));
            SetBrush("SettingsSegmentBg", Color.FromRgb(216, 222, 238));
            SetBrush("SettingsSidebarSelectedBg", Color.FromRgb(214, 222, 239));
            SetBrush("SettingsFooterBg", Color.FromRgb(243, 246, 252));
            SetBrush("SettingsPreviewBg", Color.FromRgb(247, 249, 254));
            SetBrush("SettingsCaret", Color.FromRgb(36, 39, 51));
            SetBrush("SettingsSelection", Color.FromRgb(185, 200, 246));
        }
        else
        {
            SetBrush("SettingsBg", Color.FromArgb(alpha, 17, 18, 20));
            SetBrush("SettingsSidebarBg", Color.FromArgb(sidebarAlpha, 29, 32, 39));
            SetBrush("SettingsCard", Color.FromArgb(cardAlpha, 24, 26, 31));
            SetBrush("SettingsText", Color.FromRgb(243, 244, 246));
            SetBrush("SettingsMuted", Color.FromRgb(169, 173, 183));
            SetBrush("SettingsDim", Color.FromRgb(138, 145, 158));
            SetBrush("SettingsLine", Color.FromRgb(54, 59, 69));
            SetBrush("SettingsWindowBorder", Color.FromRgb(70, 76, 88));
            SetBrush("SettingsControlBg", Color.FromRgb(31, 34, 41));
            SetBrush("SettingsControlBorder", Color.FromRgb(67, 73, 86));
            SetBrush("SettingsHoverBg", Color.FromRgb(42, 46, 55));
            SetBrush("SettingsPressedBg", Color.FromRgb(50, 56, 68));
            SetBrush("SettingsPopupBg", Color.FromRgb(28, 31, 38));
            SetBrush("SettingsSegmentBg", Color.FromRgb(35, 39, 48));
            SetBrush("SettingsSidebarSelectedBg", Color.FromRgb(38, 43, 53));
            SetBrush("SettingsFooterBg", Color.FromRgb(22, 24, 30));
            SetBrush("SettingsPreviewBg", Color.FromRgb(32, 35, 42));
            SetBrush("SettingsCaret", Color.FromRgb(243, 244, 246));
            SetBrush("SettingsSelection", Color.FromRgb(75, 111, 234));
        }
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void LanguageMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.ToggleLanguageMenu();
    }

    private void LanguageOption_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is Button { DataContext: LanguageOption option })
        {
            viewModel.SelectLanguage(option);
        }
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is Button { DataContext: ModeOption option })
        {
            viewModel.SelectMode(option);
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is Button { DataContext: ThemeCardItem theme })
        {
            viewModel.SelectTheme(theme);
        }
    }

    private void AppearanceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SelectSection("appearance");
    }

    private void ArchivedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SelectSection("archive");
    }

    private void ModelsSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SelectSection("models");
    }

    private void SkillsSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SelectSection("skills");
    }

    private void PackagesSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SelectSection("packages");
    }

    private void StorageSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SelectSection("storage");
    }

    private void DiagnosticsSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SelectSection("diagnostics");
    }

    private void BrowseAppDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel) return;
        var selected = BrowseForDirectory(viewModel.AppDataDirOverride, viewModel.EffectiveAppDataDir, viewModel.Language == "en-US" ? "Choose app data directory" : "选择应用数据目录");
        if (!string.IsNullOrWhiteSpace(selected)) viewModel.AppDataDirOverride = selected;
    }

    private void BrowseLocalAppDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel) return;
        var selected = BrowseForDirectory(viewModel.LocalAppDataDirOverride, viewModel.EffectiveLocalAppDataDir, viewModel.Language == "en-US" ? "Choose local runtime/cache directory" : "选择本地运行时/缓存目录");
        if (!string.IsNullOrWhiteSpace(selected)) viewModel.LocalAppDataDirOverride = selected;
    }

    private void ResetAppDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.ResetAppDataDir();
    }

    private void ResetLocalAppDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.ResetLocalAppDataDir();
    }

    private void SaveStoragePaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.SaveStoragePaths();
    }

    private string? BrowseForDirectory(string currentValue, string fallbackValue, string title)
    {
        var initial = !string.IsNullOrWhiteSpace(currentValue) ? currentValue : fallbackValue;
        if (!Directory.Exists(initial)) initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initial
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void AddSkillSource_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = viewModel.Language == "en-US" ? "Choose an agent root or skills folder" : "选择 agent 根目录或 skills 文件夹",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true) viewModel.AddSkillSource(dialog.FolderName);
    }

    private void SkillSourceRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is FrameworkElement { DataContext: SettingsSkillSourceItem item }) viewModel.SelectSkillSource(item);
    }

    private void SkillSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is FrameworkElement { DataContext: SettingsSkillSourceItem item }) viewModel.SelectSkillSource(item);
    }

    private void SkillSourceToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is SettingsWindowViewModel viewModel && sender is FrameworkElement { DataContext: SettingsSkillSourceItem item }) viewModel.ToggleSkillSource(item);
    }

    private void SkillToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is SettingsWindowViewModel viewModel && sender is FrameworkElement { DataContext: SettingsSkillItem item }) viewModel.ToggleSkill(item);
    }

    private void RefreshPackages_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.RefreshPluginPackages();
    }

    private void AddPackageOpen_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.OpenAddPackage();
    }

    private async void AddPackageConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel) return;
        var source = viewModel.PackageNewSource.Trim();
        var packageName = source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) ? source[4..].Trim() : source;
        if (!ShowPackageActionConfirm(
                viewModel.Language == "en-US" ? "Install package" : "安装插件包",
                viewModel.Language == "en-US" ? "This will install a third-party Pi package. npm/git/https sources may download code and execute install scripts from the package author." : "这会安装第三方 Pi package。npm/git/https 来源可能下载代码并执行 package 作者提供的安装脚本。",
                viewModel.Language == "en-US" ? $"Package: {packageName}" : $"Package：{packageName}",
                viewModel.Language == "en-US" ? $"Source: {source}\n{viewModel.PackageInstallScopeDetail}\nOnly continue if you trust this source." : $"来源：{source}\n{viewModel.PackageInstallScopeDetail}\n只在信任该来源时继续。",
                viewModel.Language == "en-US" ? "Install" : "安装",
                isDanger: false)) return;
        await viewModel.AddPackageAsync();
    }

    private void AddPackageCancel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.CancelAddPackage();
    }

    private async void UpdatePackage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel) return;
        if (!viewModel.SelectedPackageActionButtonEnabled) return;
        if (!ShowPackageActionConfirm(
                viewModel.Language == "en-US" ? "Update package" : "更新插件包",
                viewModel.Language == "en-US" ? "This will run Pi package update. npm/git/https sources may download code and execute install scripts from the package author." : "这会运行 Pi package update。npm/git/https 来源可能下载代码并执行 package 作者提供的安装脚本。",
                viewModel.Language == "en-US" ? $"Package: {viewModel.SelectedPluginPackageName}" : $"Package：{viewModel.SelectedPluginPackageName}",
                viewModel.Language == "en-US" ? $"Source: {viewModel.SelectedPluginPackageSource}\nOnly update packages from sources you trust." : $"来源：{viewModel.SelectedPluginPackageSource}\n只更新你信任来源的 package。",
                viewModel.Language == "en-US" ? "Update" : "更新",
                isDanger: false)) return;
        await viewModel.UpdateSelectedPackageAsync();
    }

    private async void RemovePackage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel) return;
        if (!viewModel.SelectedPackageActionButtonEnabled) return;
        if (!ShowPackageActionConfirm(
                viewModel.Language == "en-US" ? "Uninstall package" : "卸载插件包",
                viewModel.SelectedPluginPackageHasManagedInstall
                    ? viewModel.Language == "en-US" ? "This will remove the package from settings.json and uninstall/delete its managed install files." : "这会从 settings.json 移除 package，并卸载/删除它的受管安装文件。"
                    : viewModel.Language == "en-US" ? "This local/file package is not installed in a Pi-managed directory, so ipi will remove the settings reference only." : "这个本地/file package 不在 Pi 受管安装目录内；ipi 只会移除 settings 引用。",
                viewModel.SelectedPluginPackageSource,
                viewModel.SelectedPluginPackageUninstallTarget,
                viewModel.Language == "en-US" ? "Uninstall" : "卸载",
                isDanger: true)) return;
        await viewModel.RemoveSelectedPackageAsync();
    }

    private bool ShowPackageActionConfirm(string title, string message, string itemTitle, string detail, string primaryText, bool isDanger)
    {
        var accepted = false;
        var dialog = CreateArchiveDialog(title, message, itemTitle, detail, null, primaryText, showCancel: true, isDanger: isDanger, () => accepted = true);
        return dialog.ShowDialog() == true && accepted;
    }

    private void SettingsPluginPackageRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is FrameworkElement { DataContext: PluginPackageViewItem item }) viewModel.SelectPluginPackage(item);
    }

    private void SettingsPluginPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is FrameworkElement { DataContext: PluginPackageViewItem item }) viewModel.SelectPluginPackage(item);
    }

    private void SettingsPluginPackageToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is SettingsWindowViewModel viewModel && sender is FrameworkElement { DataContext: PluginPackageViewItem item }) viewModel.TogglePluginPackage(item);
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.RefreshRuntimeDiagnostics();
    }

    private void ModelProvider_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is Button { DataContext: ModelProviderViewItem item }) viewModel.SelectModelProvider(item);
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.StartAddProvider();
    }

    private void ProviderTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is Button { DataContext: ProviderTemplateItem item }) viewModel.SelectProviderTemplate(item);
    }

    private void CancelAddProvider_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.CancelAddProvider();
    }

    private void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && ValidateProviderSecurity(viewModel)) viewModel.SaveNewProvider();
    }

    private bool ValidateProviderSecurity(SettingsWindowViewModel viewModel)
    {
        if (!Uri.TryCreate(viewModel.NewProviderBaseUrl.Trim(), UriKind.Absolute, out var endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !(endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
              endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && endpoint.IsLoopback))
        {
            MessageBox.Show(
                this,
                viewModel.Language == "en-US"
                    ? "Remote endpoints require HTTPS; HTTP is loopback-only, and URL credentials are not allowed."
                    : "远程 Provider 地址必须使用 HTTPS。只有 localhost 或其他回环地址可以使用 HTTP。",
                viewModel.Language == "en-US" ? "Unsafe provider URL" : "Provider 地址不安全",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SettingsProviderBaseUrlBox.Focus();
            SettingsProviderBaseUrlBox.SelectAll();
            return false;
        }

        var apiKeyReference = viewModel.NewProviderApiKeyRef.Trim();
        if (apiKeyReference.Length > 0 && !Regex.IsMatch(apiKeyReference, "^\\$[A-Za-z_][A-Za-z0-9_]*$"))
        {
            viewModel.NewProviderApiKeyRef = string.Empty;
            MessageBox.Show(
                this,
                viewModel.Language == "en-US"
                    ? "Enter an environment variable reference such as $OPENAI_API_KEY. Secret values are not stored in models.json."
                    : "请输入环境变量引用，例如 $OPENAI_API_KEY。密钥明文不会写入 models.json。",
                viewModel.Language == "en-US" ? "Use an environment variable" : "请使用环境变量",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SettingsProviderApiKeyRefBox.Focus();
            return false;
        }

        return true;
    }

    private void RestoreArchived_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel && sender is Button { DataContext: ArchivedSessionViewItem item })
        {
            viewModel.RestoreArchivedSession(item);
        }
    }

    private void DeleteArchived_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel || sender is not Button { DataContext: ArchivedSessionViewItem item }) return;
        var english = viewModel.Language == "en-US";
        var confirmed = ShowArchiveDeleteConfirm(
            english ? "Delete archived chat" : "删除已归档对话",
            english ? "Permanently delete this archived chat and its local session file?" : "确定彻底删除这条已归档对话，并删除本地 session 文件吗？",
            item.Title,
            item.FilePath,
            english ? "Delete" : "删除",
            english ? "This cannot be undone." : "此操作不能撤销。");
        if (!confirmed) return;

        var deleteResult = viewModel.DeleteArchivedSession(item);
        ShowArchiveDeleteResult(viewModel, deleteResult);
    }

    private void RestoreAllArchived_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.RestoreAllArchivedSessions();
    }

    private void DeleteAllArchived_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel) return;
        var english = viewModel.Language == "en-US";
        var confirmed = ShowArchiveDeleteConfirm(
            english ? "Delete all archived chats" : "删除全部已归档对话",
            english ? "Permanently delete all archived chats and their local session files?" : "确定彻底删除所有已归档对话，并删除对应的本地 session 文件吗？",
            english ? "All archived chats" : "所有已归档对话",
            english ? "Every archived session file that still exists on disk." : "所有仍存在于本机磁盘的已归档 session 文件。",
            english ? "Delete all" : "全部删除",
            english ? "This cannot be undone." : "此操作不能撤销。");
        if (!confirmed) return;

        var deleteResult = viewModel.DeleteAllArchivedSessions();
        ShowArchiveDeleteResult(viewModel, deleteResult);
    }

    private void ShowArchiveDeleteResult(SettingsWindowViewModel viewModel, ArchiveDeleteResult result)
    {
        if (result.Errors.Count == 0) return;
        var english = viewModel.Language == "en-US";
        ShowArchiveNotice(
            english ? "Delete failed" : "删除失败",
            english ? "Some session files could not be deleted. The failed records were kept." : "部分 session 文件删除失败，失败记录已保留。",
            string.Join("\n", result.Errors.Take(6)),
            english ? "OK" : "知道了");
    }

    private bool ShowArchiveDeleteConfirm(string title, string message, string itemTitle, string path, string primaryText, string footer)
    {
        var accepted = false;
        var dialog = CreateArchiveDialog(title, message, itemTitle, path, footer, primaryText, showCancel: true, isDanger: true, () => accepted = true);
        return dialog.ShowDialog() == true && accepted;
    }

    private void ShowArchiveNotice(string title, string message, string detail, string primaryText)
    {
        var dialog = CreateArchiveDialog(title, message, null, detail, null, primaryText, showCancel: false, isDanger: false, null);
        _ = dialog.ShowDialog();
    }

    private Window CreateArchiveDialog(string title, string message, string? itemTitle, string? detail, string? footer, string primaryText, bool showCancel, bool isDanger, Action? onPrimary)
    {
        var popupBg = ResourceBrush("SettingsPopupBg", Color.FromRgb(28, 31, 38));
        var card = ResourceBrush("SettingsCard", Color.FromRgb(24, 26, 31));
        var border = ResourceBrush("SettingsControlBorder", Color.FromRgb(67, 73, 86));
        var text = ResourceBrush("SettingsText", Color.FromRgb(243, 244, 246));
        var muted = ResourceBrush("SettingsMuted", Color.FromRgb(169, 173, 183));
        var dim = ResourceBrush("SettingsDim", Color.FromRgb(116, 121, 134));
        var control = ResourceBrush("SettingsControlBg", Color.FromRgb(31, 34, 41));
        var dangerFg = new SolidColorBrush(Color.FromRgb(227, 91, 85));
        var dangerBg = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0x4D, 0x4D));
        var dangerBorder = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x4D, 0x4D));

        var dialog = new Window
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            FontFamily = TryFindResource("IpiTextFontFamily") as FontFamily ?? FontFamily,
        };

        var shell = new Border
        {
            Width = 520,
            Background = popupBg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            SnapsToDevicePixels = true,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Child = root;

        var header = new Grid { Height = 40, Margin = new Thickness(0, 0, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) dialog.DragMove(); };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = text,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        });
        var close = new Button
        {
            Width = 38,
            Height = 38,
            Style = TryFindResource("SettingsGhostButton") as Style,
            Content = new LucideIcon { Icon = "x", Size = 15, Stroke = muted, HorizontalAlignment = HorizontalAlignment.Center },
        };
        close.Click += (_, _) => dialog.DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(22, 14, 22, 18) };
        Grid.SetRow(body, 1);
        body.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(itemTitle))
        {
            body.Children.Add(new TextBlock
            {
                Text = itemTitle,
                Foreground = muted,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });
        }
        if (!string.IsNullOrWhiteSpace(detail))
        {
            body.Children.Add(new Border
            {
                Background = card,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBox
                {
                    Text = detail,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = muted,
                    FontSize = 12,
                    FontFamily = TryFindResource("IpiTextFontFamily") as FontFamily ?? FontFamily,
                    Padding = new Thickness(0),
                    MaxHeight = 90,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                }
            });
        }
        if (!string.IsNullOrWhiteSpace(footer))
        {
            body.Children.Add(new TextBlock
            {
                Text = footer,
                Foreground = dim,
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
            });
        }
        root.Children.Add(body);

        var actionsBorder = new Border
        {
            Background = control,
            BorderBrush = border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12, 16, 12),
        };
        Grid.SetRow(actionsBorder, 2);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actionsBorder.Child = actions;
        if (showCancel)
        {
            var cancel = CreateArchiveDialogButton((DataContext as SettingsWindowViewModel)?.Language == "en-US" ? "Cancel" : "取消", muted, Brushes.Transparent, border);
            cancel.Margin = new Thickness(0, 0, 10, 0);
            cancel.Click += (_, _) => dialog.DialogResult = false;
            actions.Children.Add(cancel);
        }
        var primary = CreateArchiveDialogButton(primaryText, isDanger ? dangerFg : text, isDanger ? dangerBg : card, isDanger ? dangerBorder : border);
        primary.Click += (_, _) =>
        {
            onPrimary?.Invoke();
            dialog.DialogResult = true;
        };
        actions.Children.Add(primary);
        root.Children.Add(actionsBorder);

        dialog.Content = shell;
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.DialogResult = false;
            else if (e.Key == Key.Enter && !showCancel)
            {
                onPrimary?.Invoke();
                dialog.DialogResult = true;
            }
        };
        return dialog;
    }

    private Button CreateArchiveDialogButton(string label, Brush foreground, Brush background, Brush border)
    {
        return new Button
        {
            MinWidth = 86,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Style = TryFindResource("SettingsGhostButton") as Style,
            Content = new TextBlock
            {
                Text = label,
                Foreground = foreground,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }

    private Brush ResourceBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private void CleanMissingArchived_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel) viewModel.CleanMissingArchivedSessions();
    }
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag && !flag;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag && !flag;
}
