using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentCompanion.Controls;
using AgentCompanion.Models;
using AgentCompanion.Services;
using Microsoft.Win32;

namespace AgentCompanion.Windows;

public partial class SettingsWindow : Window
{
    private const double DefaultWidth = 640;
    private const double DefaultHeight = 520;
    private const double TrayAnchorGapPixels = 12;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private readonly App? _app;
    private PetManager? _manager;
    private ProxyServer? _proxy;
    private PetSprite? _pet;
    private Action? _onScaleChanged;
    private readonly Dictionary<string, Button> _sidebarBtns = new();
    private string _activeTab = "pet";
    private readonly StartupService _startupService = new();

    public SettingsWindow()
    {
        InitializeComponent();
        _app = Application.Current as App;

        _sidebarBtns["pet"] = PetTabBtn;
        _sidebarBtns["model"] = ModelTabBtn;
        _sidebarBtns["about"] = AboutTabBtn;

        SourceInitialized += (_, _) => EnsureWindowSize();
        Loaded += (_, _) => EnsureWindowSize();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        UpdateEditionHeader();
        ShowTab("pet");
    }

    public void SetContext(PetSprite pet, PetManager manager, ProxyServer proxy, Action? onScaleChanged = null)
    {
        _pet = pet;
        _manager = manager;
        _proxy = proxy;
        _onScaleChanged = onScaleChanged;
    }

    public new void Show()
    {
        EnsureWindowSize();
        base.Show();
        EnsureWindowSize();
        EnsureWindowOnScreen();
        UpdateEditionHeader();
        RefreshCurrentTab();
    }

    public void ShowAboveScreenPoint(System.Drawing.Point anchor)
    {
        EnsureWindowSize();
        base.Show();
        EnsureWindowSize();
        MoveAboveScreenPoint(anchor);
        Dispatcher.BeginInvoke(
            () => MoveAboveScreenPoint(anchor),
            DispatcherPriority.Loaded);
        UpdateEditionHeader();
        RefreshCurrentTab();
    }

    private void EnsureWindowSize()
    {
        SizeToContent = SizeToContent.Manual;
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = DefaultWidth;
        MinHeight = DefaultHeight;
        MaxWidth = DefaultWidth;
        MaxHeight = DefaultHeight;

        Dispatcher.BeginInvoke(() =>
        {
            Width = DefaultWidth;
            Height = DefaultHeight;
        }, DispatcherPriority.Loaded);
    }

    public void EnsureWindowOnScreen()
    {
        var width = Math.Max(1, Width);
        var height = Math.Max(1, Height);
        var bounds = GetVirtualDesktopBounds();
        var position = ResolveWindowPosition(Left, Top, width, height, bounds);
        Left = position.X;
        Top = position.Y;
    }

    internal static Point ResolveWindowPosition(
        double left,
        double top,
        double width,
        double height,
        Rect bounds)
    {
        if (!IsFinite(left) || !IsFinite(top))
        {
            return new Point(
                Math.Max(bounds.Left, bounds.Right - width - 40),
                Math.Max(bounds.Top, bounds.Bottom - height - 40));
        }

        var maxLeft = Math.Max(bounds.Left, bounds.Right - width);
        var maxTop = Math.Max(bounds.Top, bounds.Bottom - height);
        return new Point(
            Math.Min(Math.Max(left, bounds.Left), maxLeft),
            Math.Min(Math.Max(top, bounds.Top), maxTop));
    }

    internal static Point ResolveAboveAnchorPosition(
        Point anchor,
        double width,
        double height,
        Rect workArea)
    {
        var desiredLeft = anchor.X - (width / 2);
        var desiredTop = anchor.Y - height - TrayAnchorGapPixels;
        var maxLeft = Math.Max(workArea.Left, workArea.Right - width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - height);

        return new Point(
            Math.Min(Math.Max(desiredLeft, workArea.Left), maxLeft),
            Math.Min(Math.Max(desiredTop, workArea.Top), maxTop));
    }

    private void MoveAboveScreenPoint(System.Drawing.Point anchor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            !GetWindowRect(handle, out var windowBounds))
            return;

        var screen = System.Windows.Forms.Screen.FromPoint(anchor);
        var workArea = new Rect(
            screen.WorkingArea.Left,
            screen.WorkingArea.Top,
            screen.WorkingArea.Width,
            screen.WorkingArea.Height);
        var width = Math.Max(1, windowBounds.Right - windowBounds.Left);
        var height = Math.Max(1, windowBounds.Bottom - windowBounds.Top);

        var position = ResolveAboveAnchorPosition(
            new Point(anchor.X, anchor.Y),
            width,
            height,
            workArea);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(position.X),
            (int)Math.Round(position.Y),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static Rect GetVirtualDesktopBounds()
    {
        if (SystemParameters.VirtualScreenWidth > 0 && SystemParameters.VirtualScreenHeight > 0)
        {
            return new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
        }

        return SystemParameters.WorkArea;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
    private void UpdateEditionHeader()
    {
        var provider = _app?.Config.StatusProvider ?? "Codex";
        var isClaude = provider.Equals("Claude", StringComparison.OrdinalIgnoreCase);
        Title = isClaude ? "AgentCompanion 設定 - Claude監視" : "AgentCompanion 設定 - Codex監視";
        TitleText.Text = "AgentCompanion 設定";
        EditionBadgeText.Text = isClaude ? "Claude監視" : "Codex監視";
        EditionBadge.Background = isClaude
            ? new SolidColorBrush(Color.FromRgb(92, 70, 150))
            : new SolidColorBrush(Color.FromRgb(42, 130, 92));
    }
    private void ShowTab(string name)
    {
        _activeTab = name;
        foreach (var (key, btn) in _sidebarBtns)
        {
            btn.Background = key == name
                ? new SolidColorBrush(Color.FromRgb(68, 68, 68))
                : Brushes.Transparent;
            btn.Foreground = key == name ? Brushes.White : Brushes.LightGray;
        }
        RefreshCurrentTab();
    }

    private void RefreshCurrentTab()
    {
        TabContent.Children.Clear();
        switch (_activeTab)
        {
            case "pet": BuildPetTab(); break;
            case "model": BuildConnectionTab(); break;
            case "about": BuildAboutTab(); break;
        }

        Dispatcher.BeginInvoke(ContentScrollViewer.ScrollToTop, DispatcherPriority.Loaded);
    }

    private void BuildPetTab()
    {
        var stack = ContentStack();
        stack.Children.Add(SectionHeader("キャラクター設定"));

        var scaleLabel = Label($"表示サイズ: {(_app?.Config.PetScale ?? 0.85):F2}x");
        var scaleSlider = new Slider
        {
            Minimum = 0.3,
            Maximum = 2.5,
            SmallChange = 0.05,
            LargeChange = 0.1,
            Value = _app?.Config.PetScale ?? 0.85,
            Margin = new Thickness(0, 4, 0, 10)
        };
        scaleSlider.ValueChanged += (_, _) =>
        {
            scaleLabel.Text = $"表示サイズ: {scaleSlider.Value:F2}x";
            if (_app?.Config != null)
                _app.Config.PetScale = scaleSlider.Value;
        };
        scaleSlider.PreviewMouseUp += (_, _) => ApplyScaleChange();
        scaleSlider.LostMouseCapture += (_, _) => ApplyScaleChange();
        stack.Children.Add(scaleLabel);
        stack.Children.Add(scaleSlider);

        var ringToggle = new CheckBox
        {
            Content = "トークンリングを表示する",
            IsChecked = _app?.Config.ShowTokenRing ?? true,
            Foreground = Brushes.LightGray,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 14)
        };
        ringToggle.Checked += (_, _) => SetRingVisibility(true);
        ringToggle.Unchecked += (_, _) => SetRingVisibility(false);
        stack.Children.Add(ringToggle);

        stack.Children.Add(SectionHeader("待機モーション時間"));
        var idleTimingGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        idleTimingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        idleTimingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        idleTimingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddDoubleField(
            idleTimingGrid,
            0,
            0,
            "座る時間（秒）",
            _app?.Config.PetSittingDurationSeconds ?? PetIdleTiming.DefaultSittingDurationSeconds,
            value => _app!.Config.PetSittingDurationSeconds = value,
            PetIdleTiming.MinimumDurationSeconds,
            PetIdleTiming.MaximumDurationSeconds);
        AddDoubleField(
            idleTimingGrid,
            0,
            1,
            "寝る時間（秒）",
            _app?.Config.PetSleepingDurationSeconds ?? PetIdleTiming.DefaultSleepingDurationSeconds,
            value => _app!.Config.PetSleepingDurationSeconds = value,
            PetIdleTiming.MinimumDurationSeconds,
            PetIdleTiming.MaximumDurationSeconds);
        stack.Children.Add(idleTimingGrid);
        stack.Children.Add(Paragraph("通常待機から座る・寝る状態へ入った後、その姿勢を維持する時間です。"));

        stack.Children.Add(SectionHeader("動作プレビュー"));
        var animGrid = new Grid { Margin = new Thickness(0, 4, 0, 22) };
        for (var col = 0; col < 3; col++)
            animGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var animations = new[]
        {
            ("idle", "待機"),
            ("sit", "座る"),
            ("sleep", "寝る"),
            ("sprint", "作業中"),
            ("wave", "手を振る"),
            ("fail", "失敗"),
            ("walk", "右へ走る"),
            ("run_left", "左へ走る"),
            ("jump", "ジャンプ")
        };

        for (var i = 0; i < animations.Length; i++)
        {
            if (i % 3 == 0)
                animGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var (name, label) = animations[i];
            var button = SmallButton(label, () => _pet?.Play(name));
            button.Tag = name;
            button.Height = 32;
            button.Margin = new Thickness(0, 0, 8, 8);
            Grid.SetColumn(button, i % 3);
            Grid.SetRow(button, i / 3);
            animGrid.Children.Add(button);
        }
        stack.Children.Add(animGrid);

        stack.Children.Add(SectionHeader("キャラクター一覧"));
        var petList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (_manager == null || _manager.Pets.Count == 0)
        {
            petList.Children.Add(Label("登録済みのキャラクターがありません。"));
        }
        else
        {
            foreach (var pet in _manager.Pets)
                petList.Children.Add(BuildPetRow(pet));
        }
        stack.Children.Add(petList);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttonRow.Children.Add(MakeButton("インポート", ImportPet));
        buttonRow.Children.Add(MakeButton("エクスポート", ExportPet));
        buttonRow.Children.Add(MakeButton("フォルダを開く", OpenPetDir));
        stack.Children.Add(buttonRow);

        TabContent.Children.Add(stack);
    }

    private Border BuildPetRow(PetInfo pet)
    {
        var isActive = pet.Id == _manager?.ActivePetId;
        var row = new Border
        {
            Background = isActive
                ? new SolidColorBrush(Color.FromArgb(90, 255, 165, 0))
                : new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            BorderBrush = isActive ? Brushes.Orange : new SolidColorBrush(Color.FromRgb(70, 70, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumbnail = BuildThumbnail(pet);
        Grid.SetColumn(thumbnail, 0);
        grid.Children.Add(thumbnail);

        var textStack = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(pet.DisplayName) ? pet.Id : pet.DisplayName,
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.Bold
        });
        textStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(pet.Description) ? pet.Id : pet.Description,
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (isActive)
        {
            textStack.Children.Add(new TextBlock
            {
                Text = "選択中",
                Foreground = Brushes.Orange,
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);
        row.MouseLeftButtonUp += (_, _) =>
        {
            _manager?.SetActivePet(pet.Id);
            _app?.Config.Save();
            RefreshCurrentTab();
        };
        row.Child = grid;
        return row;
    }

    private Border BuildThumbnail(PetInfo pet)
    {
        var image = new System.Windows.Controls.Image { Width = 54, Height = 58, Stretch = Stretch.Uniform };
        var border = new Border
        {
            Width = 62,
            Height = 66,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
            CornerRadius = new CornerRadius(6),
            Child = image
        };

        var previewPath = Path.Combine(pet.Directory, "preview-idle.png");
        if (File.Exists(previewPath))
        {
            image.Source = new BitmapImage(new Uri(previewPath));
            return border;
        }

        var spritePath = Path.Combine(pet.Directory, pet.SpritesheetPath);
        Task.Run(() =>
        {
            var source = SpriteLoader.LoadSpritesheet(spritePath);
            if (source == null || source.PixelWidth < AnimationDefs.FrameWidth || source.PixelHeight < AnimationDefs.FrameHeight)
                return;

            var cropped = new CroppedBitmap(source, new Int32Rect(0, 0, AnimationDefs.FrameWidth, AnimationDefs.FrameHeight));
            cropped.Freeze();
            Dispatcher.Invoke(() => image.Source = cropped);
        });

        return border;
    }

    private void BuildConnectionTab()
    {
        var stack = ContentStack();
        stack.Children.Add(SectionHeader("接続・監視"));

        stack.Children.Add(Label("状況を読む対象"));
        stack.Children.Add(MakeChoiceRow(new[] { "Codex", "Claude" }, _app?.Config.StatusProvider ?? "Codex", value =>
        {
            if (_app?.Config == null) return;
            _app.Config.StatusProvider = value;
            if (value == "Claude" && _app.Config.LauncherTarget == "Codex")
                _app.Config.LauncherTarget = "VSCode";
            _app.Config.Save();
            UpdateEditionHeader();
            RefreshCurrentTab();
        }));

        stack.Children.Add(SectionHeader("起動"));
        var startupRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };
        var startupToggle = new CheckBox
        {
            Content = "Windows起動時にこの AgentCompanion を起動する",
            IsChecked = _startupService.IsEnabled(),
            Foreground = Brushes.LightGray,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        var startupStatus = new TextBlock
        {
            Text = _startupService.IsEnabled() ? "登録済み" : "未登録",
            Foreground = _startupService.IsEnabled()
                ? new SolidColorBrush(Color.FromRgb(80, 210, 110))
                : Brushes.Gray,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        startupToggle.Checked += (_, _) =>
        {
            try
            {
                _startupService.Enable();
                startupStatus.Text = "登録済み";
                startupStatus.Foreground = new SolidColorBrush(Color.FromRgb(80, 210, 110));
            }
            catch (Exception ex)
            {
                startupToggle.IsChecked = false;
                MessageBox.Show($"スタートアップ登録に失敗しました。\n{ex.Message}", "スタートアップ登録", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        startupToggle.Unchecked += (_, _) =>
        {
            _startupService.Disable();
            startupStatus.Text = "未登録";
            startupStatus.Foreground = Brushes.Gray;
        };
        startupRow.Children.Add(startupToggle);
        startupRow.Children.Add(startupStatus);
        stack.Children.Add(startupRow);
        stack.Children.Add(Paragraph($"起動先: {_startupService.ExecutablePath}"));
        stack.Children.Add(Label("ダブルクリックで開くアプリ"));
        stack.Children.Add(MakeChoiceRow(new[] { "Codex", "VSCode" }, _app?.Config.LauncherTarget ?? "Codex", value =>
        {
            if (_app?.Config == null) return;
            _app.Config.LauncherTarget = value;
            _app.Config.Save();
        }));

        stack.Children.Add(Label("Claude ホーム"));
        var claudeHome = MakeTextInput(_app?.Config.ClaudeHomePath ?? "");
        claudeHome.LostFocus += (_, _) => SaveText(value => _app!.Config.ClaudeHomePath = value, claudeHome.Text);
        stack.Children.Add(claudeHome);

        stack.Children.Add(Label("VSCode ワークスペース"));
        var workspace = MakeTextInput(_app?.Config.VSCodeWorkspacePath ?? "");
        workspace.LostFocus += (_, _) => SaveText(value => _app!.Config.VSCodeWorkspacePath = value, workspace.Text);
        stack.Children.Add(workspace);

        var limits = new Grid { Margin = new Thickness(0, 8, 0, 14) };
        limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        limits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        limits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        limits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddLimitField(limits, 0, 0, "Codex 1日上限", _app?.Config.DailyTokenLimit ?? 200_000, value => _app!.Config.DailyTokenLimit = value);
        AddLimitField(limits, 0, 1, "Claude 5時間上限", _app?.Config.ClaudeShortWindowTokenLimit ?? 3_640_000, value => _app!.Config.ClaudeShortWindowTokenLimit = value);
        AddLimitField(limits, 1, 0, "Claude 週間上限", _app?.Config.ClaudeWeeklyTokenLimit ?? 562_000_000, value => _app!.Config.ClaudeWeeklyTokenLimit = value);
        AddDoubleField(limits, 1, 1, "Claude 短期枠の時間", _app?.Config.ClaudeShortWindowHours ?? 5, value => _app!.Config.ClaudeShortWindowHours = value);
        stack.Children.Add(limits);

        stack.Children.Add(SectionHeader("API プロキシ"));
        var portInput = MakeTextInput((_app?.Config.ProxyPort ?? 11435).ToString(CultureInfo.InvariantCulture));
        stack.Children.Add(Label("待受ポート"));
        stack.Children.Add(portInput);

        var proxyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 14) };
        var proxyToggle = new CheckBox
        {
            Content = "プロキシを有効にする",
            Foreground = Brushes.LightGray,
            FontSize = 14,
            IsChecked = _proxy?.IsActive ?? _app?.Config.ProxyEnabled ?? false,
            VerticalAlignment = VerticalAlignment.Center
        };
        var proxyStatus = new TextBlock
        {
            Text = _proxy?.IsActive == true ? "稼働中" : "停止中",
            Foreground = _proxy?.IsActive == true ? new SolidColorBrush(Color.FromRgb(80, 210, 110)) : new SolidColorBrush(Color.FromRgb(220, 90, 90)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        proxyToggle.Checked += (_, _) =>
        {
            var port = int.TryParse(portInput.Text, out var parsed) ? parsed : 11435;
            try
            {
                _proxy?.Stop();
                _proxy?.Start(port, _proxy.Targets);
                proxyStatus.Text = "稼働中";
                proxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(80, 210, 110));
                if (_app?.Config != null)
                {
                    _app.Config.ProxyEnabled = true;
                    _app.Config.ProxyPort = port;
                    _app.Config.Save();
                }
            }
            catch (Exception ex)
            {
                proxyToggle.IsChecked = false;
                MessageBox.Show($"プロキシを開始できませんでした。\n{ex.Message}", "プロキシ開始エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        proxyToggle.Unchecked += (_, _) =>
        {
            _proxy?.Stop();
            proxyStatus.Text = "停止中";
            proxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(220, 90, 90));
            if (_app?.Config != null)
            {
                _app.Config.ProxyEnabled = false;
                _app.Config.Save();
            }
        };
        proxyRow.Children.Add(proxyToggle);
        proxyRow.Children.Add(proxyStatus);
        stack.Children.Add(proxyRow);

        var debugLogToggle = new CheckBox
        {
            Content = "デバッグログを出力する",
            IsChecked = _app?.Config.ProxyDebugLogEnabled ?? false,
            Foreground = Brushes.LightGray,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 14)
        };
        debugLogToggle.Checked += (_, _) => SetProxyDebugLog(true);
        debugLogToggle.Unchecked += (_, _) => SetProxyDebugLog(false);
        stack.Children.Add(debugLogToggle);

        stack.Children.Add(SectionHeader("転送先"));
        var targetsPanel = new StackPanel();
        stack.Children.Add(targetsPanel);
        BuildTargetsPanel(targetsPanel, portInput);

        TabContent.Children.Add(stack);
    }

    private void SetProxyDebugLog(bool enabled)
    {
        if (_app?.Config == null)
            return;

        _app.Config.ProxyDebugLogEnabled = enabled;
        ProxyServer.EnableDebugLog = enabled;
        _app.Config.Save();
    }
    private void BuildTargetsPanel(StackPanel targetsPanel, TextBox portInput)
    {
        targetsPanel.Children.Clear();
        var targets = _proxy?.Targets.ToList() ?? new List<ProxyTarget>();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(ColumnLabel("名前", 110));
        header.Children.Add(ColumnLabel("パス", 80));
        header.Children.Add(ColumnLabel("ホスト", 250));
        targetsPanel.Children.Add(header);

        for (var i = 0; i < targets.Count; i++)
        {
            var index = i;
            var target = targets[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

            var nameBox = MakeTextInput(target.Name, 110);
            nameBox.LostFocus += (_, _) => UpdateTarget(index, nameBox.Text, null, null);
            var prefixBox = MakeTextInput(target.Prefix, 80);
            prefixBox.LostFocus += (_, _) => UpdateTarget(index, null, prefixBox.Text, null);
            var hostBox = MakeTextInput(target.Host, 250);
            hostBox.LostFocus += (_, _) => UpdateTarget(index, null, null, hostBox.Text);
            var delete = SmallButton("削除", () =>
            {
                _proxy?.RemoveTarget(index);
                BuildTargetsPanel(targetsPanel, portInput);
            });
            delete.Foreground = new SolidColorBrush(Color.FromRgb(230, 95, 95));

            row.Children.Add(nameBox);
            row.Children.Add(prefixBox);
            row.Children.Add(hostBox);
            row.Children.Add(delete);
            targetsPanel.Children.Add(row);
        }

        var add = MakeButton("転送先を追加", () =>
        {
            _proxy?.AddTarget(new ProxyTarget("新規", "new", "api.example.com"));
            BuildTargetsPanel(targetsPanel, portInput);
        });
        add.Margin = new Thickness(0, 4, 0, 10);
        targetsPanel.Children.Add(add);

        targetsPanel.Children.Add(Label("コピー用 URL"));
        foreach (var target in targets)
        {
            var url = $"http://127.0.0.1:{portInput.Text}/{target.Prefix}";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = $"{target.Name}: {url}",
                Foreground = Brushes.LightGray,
                FontSize = 12,
                Width = 390,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(SmallButton("コピー", () => Clipboard.SetText(url)));
            targetsPanel.Children.Add(row);
        }
    }

    private void BuildAboutTab()
    {
        var stack = ContentStack();
        stack.Children.Add(SectionHeader("AgentCompanion について"));
        stack.Children.Add(new TextBlock
        {
            Text = "AgentCompanion v1.0.0",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 0, 8)
        });
        stack.Children.Add(Paragraph("AgentCompanion は、デスクトップ上のキャラクターが Codex / Claude の実行状況と利用量を知らせる常駐ツールです。"));
        stack.Children.Add(Paragraph("元プロジェクト TokenPet をベースに、Codex/Claude のローカル履歴監視、常時リング表示、アプリ起動/前面表示、複数キャラクター切替を追加しています。"));

        stack.Children.Add(SectionHeader("基本操作"));
        stack.Children.Add(CodeBlock("左ドラッグ: キャラクターを移動\nダブルクリック: Codex / VSCode を開く、または前面に表示\nタスクトレイ右クリック: 表示/非表示、設定、終了"));

        stack.Children.Add(SectionHeader("設定の要点"));
        stack.Children.Add(CodeBlock("キャラクター: Koharu / Luna などの見た目を選択、表示サイズ、トークンリング表示\n接続: 監視対象プロファイル、スタートアップ登録、ダブルクリック対象、Claude/VSCode パス、利用量上限\nAPI プロキシ: OpenAI 互換 API を proxy 経由で使う場合のみ設定"));

        stack.Children.Add(SectionHeader("キャラクターパッケージ"));
        stack.Children.Add(CodeBlock("pet.json + spritesheet.webp/png を ZIP にまとめたキャラクターパッケージをインポートできます。\n標準フレーム: 192 x 208px\nアニメーション行: idle, walk, run_left, wave, jump, fail, sleep, sprint, sit"));

        stack.Children.Add(SectionHeader("ライセンス / 派生元"));
        stack.Children.Add(Paragraph("AgentCompanion は TokenPet から派生したカスタム版です。元プロジェクトは MIT License として公開されています。"));
        stack.Children.Add(CodeBlock("Original: https://github.com/sugar301/TokenPet\nLicense: MIT\nThis fork/customization: https://github.com/k-hattori-itcs/agent-companion"));

        TabContent.Children.Add(stack);
    }
    private void ApplyScaleChange()
    {
        _app?.Config.Save();
        _onScaleChanged?.Invoke();
        if (_pet == null)
            return;
        _pet.InvalidateMeasure();
        _pet.InvalidateVisual();
        _pet.Play("idle");
    }

    private void SetRingVisibility(bool visible)
    {
        if (_app?.Config == null)
            return;
        _app.Config.ShowTokenRing = visible;
        _app.Config.Save();
    }

    private void ImportPet()
    {
        var dialog = new OpenFileDialog { Filter = "ZIP ファイル|*.zip", Title = "キャラクターをインポート" };
        if (dialog.ShowDialog() != true)
            return;

        var error = _manager?.ImportPet(dialog.FileName);
        if (error != null)
            MessageBox.Show(error, "インポートエラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            RefreshCurrentTab();
    }

    private void ExportPet()
    {
        if (_manager == null || string.IsNullOrEmpty(_manager.ActivePetId))
            return;

        var dialog = new SaveFileDialog { Filter = "ZIP ファイル|*.zip", Title = "キャラクターをエクスポート", FileName = $"{_manager.ActivePetId}.zip" };
        if (dialog.ShowDialog() != true)
            return;

        var error = _manager.ExportPet(_manager.ActivePetId, dialog.FileName);
        if (error != null)
            MessageBox.Show(error, "エクスポートエラー", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenPetDir()
    {
        var dir = _manager?.PetsDir ?? "";
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var startInfo = new ProcessStartInfo { FileName = explorerPath, UseShellExecute = false };
        startInfo.ArgumentList.Add(Path.GetFullPath(dir));
        Process.Start(startInfo);
    }

    private void UpdateTarget(int index, string? name, string? prefix, string? host)
    {
        var targets = _proxy?.Targets;
        if (targets == null || index >= targets.Count)
            return;

        var current = targets[index];
        _proxy?.ReplaceTarget(index, new ProxyTarget(name ?? current.Name, prefix ?? current.Prefix, host ?? current.Host));
    }

    private void SaveText(Action<string> setter, string value)
    {
        if (_app?.Config == null)
            return;
        setter(value.Trim());
        _app.Config.Save();
    }

    private void AddLimitField(Grid grid, int row, int column, string label, long value, Action<long> setter)
    {
        var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, column == 0 ? 8 : 0, 8) };
        panel.Children.Add(Label(label));
        var input = MakeTextInput(value.ToString(CultureInfo.InvariantCulture));
        input.LostFocus += (_, _) =>
        {
            if (_app?.Config == null || !long.TryParse(input.Text.Replace(",", ""), out var parsed))
                return;
            setter(Math.Max(1, parsed));
            _app.Config.Save();
        };
        panel.Children.Add(input);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private void AddDoubleField(
        Grid grid,
        int row,
        int column,
        string label,
        double value,
        Action<double> setter,
        double minimum = 0.25,
        double maximum = double.PositiveInfinity)
    {
        var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, column == 0 ? 8 : 0, 8) };
        panel.Children.Add(Label(label));
        var input = MakeTextInput(value.ToString("0.##", CultureInfo.InvariantCulture));
        input.LostFocus += (_, _) =>
        {
            if (_app?.Config == null || !double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return;
            var normalized = Math.Clamp(parsed, minimum, maximum);
            setter(normalized);
            input.Text = normalized.ToString("0.##", CultureInfo.InvariantCulture);
            _app.Config.Save();
        };
        panel.Children.Add(input);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static StackPanel ContentStack() => new() { Margin = new Thickness(0, 0, 8, 0) };

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Orange,
        FontSize = 16,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 2, 0, 8)
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gray,
        FontSize = 13,
        Margin = new Thickness(0, 8, 0, 4)
    };

    private static TextBlock ColumnLabel(string text, double width) => new()
    {
        Text = text,
        Width = width,
        Foreground = Brushes.Gray,
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private static TextBox MakeTextInput(string text, double width = double.NaN) => new()
    {
        Text = text,
        Width = width,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 82)),
        FontSize = 13,
        Padding = new Thickness(8, 5, 8, 5),
        Margin = new Thickness(0, 0, 8, 0),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private static StackPanel MakeChoiceRow(IEnumerable<string> options, string selected, Action<string> onSelected)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new List<Button>();
        foreach (var option in options)
        {
            var button = SmallButton(option, () =>
            {
                onSelected(option);
                UpdateChoiceButtons(buttons, option);
            });
            button.Tag = option;
            button.Width = 92;
            button.Margin = new Thickness(0, 0, 8, 0);
            buttons.Add(button);
            row.Children.Add(button);
        }
        UpdateChoiceButtons(buttons, selected);
        return row;
    }

    private static void UpdateChoiceButtons(IEnumerable<Button> buttons, string selected)
    {
        foreach (var button in buttons)
        {
            var isSelected = string.Equals(button.Tag as string, selected, StringComparison.OrdinalIgnoreCase);
            button.Background = isSelected
                ? new SolidColorBrush(Color.FromRgb(54, 125, 92))
                : new SolidColorBrush(Color.FromRgb(42, 42, 42));
            button.BorderBrush = isSelected
                ? new SolidColorBrush(Color.FromRgb(96, 210, 150))
                : new SolidColorBrush(Color.FromRgb(82, 82, 82));
            button.Foreground = Brushes.White;
        }
    }
    private static Button MakeButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 13,
            Foreground = Brushes.LightGray,
            Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(84, 84, 84)),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button SmallButton(string text, Action action)
    {
        var button = MakeButton(text, action);
        button.FontSize = 12;
        button.Padding = new Thickness(10, 4, 10, 4);
        return button;
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.LightGray,
        FontSize = 13,
        LineHeight = 21,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static TextBlock CodeBlock(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.LightGray,
        FontSize = 12,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
        Padding = new Thickness(10),
        Margin = new Thickness(0, 2, 0, 10)
    };

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();
    private void PetTabBtn_Click(object sender, RoutedEventArgs e) => ShowTab("pet");
    private void ModelTabBtn_Click(object sender, RoutedEventArgs e) => ShowTab("model");
    private void AboutTabBtn_Click(object sender, RoutedEventArgs e) => ShowTab("about");
}
