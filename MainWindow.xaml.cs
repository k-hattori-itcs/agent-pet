using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentCompanion.Controls;
using AgentCompanion.Services;

namespace AgentCompanion;

public enum PetState { Idle, Sleeping, Sitting, Dragged, Working, Waiting }

public partial class MainWindow : Window
{
    private const double TokenRingPadding = 10;
    private const double BubbleReservedHeight = 62;
    // GetCursorPos returns physical screen pixels, so keep the drag threshold in pixels too.
    private const double DragThresholdPixels = 6;

    private readonly App _app;
    private readonly DispatcherTimer _animTimer;
    private readonly DispatcherTimer _agentStatusTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly CodexStatusService _codexStatus = new();
    private readonly ClaudeStatusService _claudeStatus = new();
    private readonly CodexLauncherService _codexLauncher = new();
    private readonly VSCodeLauncherService _vsCodeLauncher = new();
    private PetState _state = PetState.Idle;
    private double _sleepTimer;
    private double _sitTimer;
    private int _idleSitCount;
    private DateTime _lastFrame = DateTime.Now;
    private DateTime _lastClick = DateTime.MinValue;
    private DateTime _transientStatusUntil = DateTime.MinValue;
    private CodexStatusSnapshot? _lastCodexStatus;

    private bool _isDragging;
    private bool _hasDragged;
    private Point _dragStartMouseScreen;
    private Point _dragStartWindowPos;
    private double _lastDragScreenX;

    private DateTime _reactiveEnd;
    private PetState _stateBeforeReactive;
    private string _lastErrorReactionKey = "";

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public Controls.PetSprite PetSpriteControl => PetSprite;
    public TokenHistory History { get; }
    public ProxyServer Proxy { get; }

    public void ShowPet()
    {
        Show();
        WindowState = WindowState.Normal;
        EnsureWindowOnScreen();
        Activate();
        KeepWindowTopmost();
    }

    public void HidePet()
    {
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
        }

        SyncWindowPositionFromScreen();
        Hide();
    }

    public MainWindow()
    {
        InitializeComponent();
        _app = (Application.Current as App)!;

        History = new TokenHistory();
        History.Load();
        Proxy = new ProxyServer();
        Proxy.TokenUsed += (input, output, target) =>
        {
            Dispatcher.Invoke(() =>
            {
                History.Record(target, input, output);
                UpdateTokenRing();
                SetTransientStatus($"Used {FormatTokenCount(input + output)} tokens - {target}", 5);
            });
        };

        Proxy.RequestReceived += target =>
        {
            Dispatcher.Invoke(() =>
            {
                _transitionTo(PetState.Working);
                SetTransientStatus($"Working on {target.ToUpperInvariant()}", 4);
            });
        };

        Proxy.ResponseFinished += (status, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (status >= 400)
                {
                    SetTransientStatus($"Needs attention - HTTP {status}", 8);
                    _reactiveAnim("fail", 6);
                }
                else
                {
                    SetTransientStatus("Done", 4);
                    _reactiveAnim("wave", 5);
                }
            });
        };

        _animTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => OnTick(), Dispatcher);
        _animTimer.Start();

        _agentStatusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => RefreshAgentStatus(), Dispatcher);
        _agentStatusTimer.Start();

        _topmostTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => KeepWindowTopmost(), Dispatcher);
        _topmostTimer.Start();

        Loaded += OnLoaded;
        Closing += OnClosing;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
        KeyDown += OnKeyDown;
        PetSprite.SizeChanged += (_, _) => SnapWindowToPet();
        Activated += (_, _) => KeepWindowTopmost();
        Deactivated += (_, _) => KeepWindowTopmost();
        SourceInitialized += (_, _) => KeepWindowTopmost();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _app.PetManager.PetChanged += OnPetChanged;

        if (!string.IsNullOrEmpty(_app.Config.ActivePetId))
            _app.PetManager.SetActivePet(_app.Config.ActivePetId);

        LoadPetSprite();
        SnapWindowToPet();
        PositionWindow();
        EnsureWindowOnScreen();
        _transitionTo(PetState.Idle);
        RefreshAgentStatus();
        UpdateTokenRing();
        KeepWindowTopmost();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _app.Config.WindowX = Left;
        _app.Config.WindowY = Top;
        _app.Config.TotalCalls = History.GetTotalCalls();
        _app.Config.TotalTokens = History.GetCumulativeTokens();
        _app.Config.Save();
        _agentStatusTimer.Stop();
        _topmostTimer.Stop();
        Proxy.Stop();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var now = DateTime.Now;
        if ((now - _lastClick).TotalMilliseconds < 350)
        {
            OpenOrFocusAgent();
            _lastClick = DateTime.MinValue;
            return;
        }
        _lastClick = now;

        GetCursorPos(out var pt);
        _dragStartMouseScreen = new Point(pt.X, pt.Y);
        _dragStartWindowPos = GetWindowScreenPosition();
        _lastDragScreenX = pt.X;
        _isDragging = true;
        _hasDragged = false;
        CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        var shouldJump = _hasDragged;
        _isDragging = false;
        _hasDragged = false;
        ReleaseMouseCapture();
        if (shouldJump)
        {
            SyncWindowPositionFromScreen();
            KeepWindowTopmost();
            _transitionTo(PetState.Idle);
            _reactiveAnim("jump", 1);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        GetCursorPos(out var cur);
        var deltaX = cur.X - _dragStartMouseScreen.X;
        var deltaY = cur.Y - _dragStartMouseScreen.Y;
        if (!_hasDragged)
        {
            if (deltaX * deltaX + deltaY * deltaY < DragThresholdPixels * DragThresholdPixels)
                return;

            _hasDragged = true;
            _transitionTo(PetState.Dragged);
        }

        MoveWindowToScreenPosition(
            (int)Math.Round(_dragStartWindowPos.X + deltaX),
            (int)Math.Round(_dragStartWindowPos.Y + deltaY));

        var dx = cur.X - _lastDragScreenX;
        if (dx > 2)
            PetSprite.Play("walk");
        else if (dx < -2)
            PetSprite.Play("run_left");
        _lastDragScreenX = cur.X;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            KeepWindowTopmost();
        }
    }

    private void OnPetChanged(string petId)
    {
        _app.Config.ActivePetId = petId;
        _app.Config.Save();
        LoadPetSprite();
        _transitionTo(PetState.Idle);
    }

    internal void OpenSettings()
    {
        _app.ShowSettings();
    }

    internal void LoadPetSprite()
    {
        var scale = _app.Config.PetScale;
        var fw = Models.AnimationDefs.FrameWidth * scale;
        var fh = Models.AnimationDefs.FrameHeight * scale;
        PetSprite.Width = fw;
        PetSprite.Height = fh;

        var spritePath = _app.PetManager.GetActiveSpritePath();
        if (spritePath != null)
            PetSprite.LoadSpriteSheet(spritePath);
        else
            PetSprite.LoadProceduralSprites();
    }

    internal void SnapWindowToPet()
    {
        var stageWidth = PetSprite.Width + TokenRingPadding * 2;
        var stageHeight = PetSprite.Height + TokenRingPadding * 2;

        PetStage.Width = stageWidth;
        PetStage.Height = stageHeight;
        RingTrack.Width = stageWidth;
        RingTrack.Height = stageHeight;
        RingTrackSecondary.Width = Math.Max(1, stageWidth - 14);
        RingTrackSecondary.Height = Math.Max(1, stageHeight - 14);
        RingBadge.Margin = new Thickness(0, 0, 2, 2);

        Width = Math.Max(stageWidth, 240);
        Height = stageHeight + BubbleReservedHeight;
        UpdateTokenRing();
    }

    private void PositionWindow()
    {
        if (_app.Config.WindowX >= 0 && _app.Config.WindowY >= 0)
        {
            Left = _app.Config.WindowX;
            Top = _app.Config.WindowY;
        }
        else
        {
            var screen = SystemParameters.WorkArea;
            Left = screen.Right - Width - 60;
            Top = screen.Bottom - Height - 80;
        }
    }

    private void EnsureWindowOnScreen()
    {
        var width = Math.Max(1, Width);
        var height = Math.Max(1, Height);
        if (!IsFinite(Left) || !IsFinite(Top))
        {
            MoveToDefaultPosition(width, height);
            return;
        }

        var window = new Rect(Left, Top, width, height);
        var desktop = GetVirtualDesktopBounds();
        var visible = Rect.Intersect(window, desktop);
        const double MinVisibleSize = 40;
        if (!visible.IsEmpty && visible.Width >= MinVisibleSize && visible.Height >= MinVisibleSize)
            return;

        MoveToDefaultPosition(width, height);
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

    private void MoveToDefaultPosition(double width, double height)
    {
        var screen = SystemParameters.WorkArea;
        Left = Math.Max(screen.Left, screen.Right - width - 60);
        Top = Math.Max(screen.Top, screen.Bottom - height - 80);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
    private void KeepWindowTopmost()
    {
        Topmost = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private Point GetWindowScreenPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
            return new Point(rect.Left, rect.Top);

        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget == null
            ? new Point(Left, Top)
            : source.CompositionTarget.TransformToDevice.Transform(new Point(Left, Top));
    }

    private void MoveWindowToScreenPosition(int left, int top)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            Left = left;
            Top = top;
            return;
        }

        SetWindowPos(handle, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void SyncWindowPositionFromScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
        {
            Left = rect.Left;
            Top = rect.Top;
            return;
        }

        var dip = source.CompositionTarget.TransformFromDevice.Transform(new Point(rect.Left, rect.Top));
        Left = dip.X;
        Top = dip.Y;
    }


    public void OnTick()
    {
        var now = DateTime.Now;
        var delta = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        PetSprite.Tick();
        if (_transientStatusUntil > DateTime.MinValue && now > _transientStatusUntil)
        {
            _transientStatusUntil = DateTime.MinValue;
            SetAgentStatusText();
        }

        if (_isDragging) return;

        if (_reactiveEnd > DateTime.MinValue && now > _reactiveEnd)
        {
            _reactiveEnd = DateTime.MinValue;
            _transitionTo(_stateBeforeReactive == PetState.Working ? PetState.Idle : _stateBeforeReactive);
            return;
        }
        if (_reactiveEnd > DateTime.MinValue) return;

        switch (_state)
        {
            case PetState.Idle:
                _sitTimer += delta;
                if (_sitTimer >= 18.0)
                {
                    if (_idleSitCount >= 2)
                        _transitionTo(PetState.Sleeping);
                    else
                    {
                        _idleSitCount++;
                        _transitionTo(PetState.Sitting);
                    }
                }
                break;

            case PetState.Sleeping:
                _sleepTimer += delta;
                if (PetIdleTiming.ShouldReturnToIdle(
                    PetState.Sleeping,
                    _sleepTimer,
                    _app.Config.PetSittingDurationSeconds,
                    _app.Config.PetSleepingDurationSeconds))
                    _transitionTo(PetState.Idle);
                break;

            case PetState.Sitting:
                _sitTimer += delta;
                if (PetIdleTiming.ShouldReturnToIdle(
                    PetState.Sitting,
                    _sitTimer,
                    _app.Config.PetSittingDurationSeconds,
                    _app.Config.PetSleepingDurationSeconds))
                    _transitionTo(PetState.Idle);
                break;

        }
    }

    private void _reactiveAnim(string anim, int seconds)
    {
        _stateBeforeReactive = _state;
        _reactiveEnd = DateTime.Now.AddSeconds(seconds);
        PetSprite.Play(anim);
    }


    private void _transitionTo(PetState newState)
    {
        _state = newState;
        switch (newState)
        {
            case PetState.Idle:
                _sleepTimer = 0;
                _sitTimer = 0;
                PetSprite.Play("idle");
                break;
            case PetState.Sleeping:
                _sleepTimer = 0;
                _idleSitCount = 0;
                PetSprite.Play("sleep");
                break;
            case PetState.Sitting:
                _sitTimer = 0;
                PetSprite.Play("sit");
                break;
            case PetState.Dragged:
                SetTransientStatus("Moving", 1);
                break;
            case PetState.Working:
                _idleSitCount = 0;
                PetSprite.Play("sprint");
                break;
            case PetState.Waiting:
                _idleSitCount = 0;
                PetSprite.Play("sit");
                break;
        }

        if (_transientStatusUntil <= DateTime.Now)
            SetAgentStatusText();
    }

    private void RefreshAgentStatus()
    {
        var previousStatus = _lastCodexStatus;
        var currentStatus = IsClaudeProvider()
            ? _claudeStatus.Poll(
                _app.Config.ClaudeHomePath,
                Math.Max(1, _app.Config.ClaudeShortWindowTokenLimit),
                Math.Max(1, _app.Config.ClaudeWeeklyTokenLimit),
                Math.Max(0.25, _app.Config.ClaudeShortWindowHours))
            : _codexStatus.Poll();

        _lastCodexStatus = currentStatus;
        UpdateTokenRing();
        if (ShouldReactToError(currentStatus))
            ReactToAgentError(currentStatus);
        else if (ShouldReactToCompletion(previousStatus, currentStatus))
            ReactToAgentCompletion(currentStatus);
        else
            ApplyAgentActivityState(currentStatus);
    }

    private static bool ShouldReactToCompletion(CodexStatusSnapshot? previousStatus, CodexStatusSnapshot currentStatus)
    {
        if (previousStatus == null)
            return false;
        if (!previousStatus.IsRunning || currentStatus.IsRunning)
            return false;
        if (string.IsNullOrWhiteSpace(previousStatus.RolloutPath) || previousStatus.RolloutPath != currentStatus.RolloutPath)
            return false;
        return currentStatus.UpdatedAt >= previousStatus.UpdatedAt;
    }

    private bool ShouldReactToError(CodexStatusSnapshot status)
    {
        if (!LooksLikeError(status.Summary))
        {
            _lastErrorReactionKey = "";
            return false;
        }

        var key = $"{status.RolloutPath}|{status.UpdatedAt:O}|{status.Summary}";
        if (key == _lastErrorReactionKey)
            return false;

        _lastErrorReactionKey = key;
        return true;
    }

    private void ApplyAgentActivityState(CodexStatusSnapshot status)
    {
        if (_isDragging || _reactiveEnd > DateTime.Now)
            return;

        if (LooksLikeWaiting(status.Summary))
        {
            if (_state != PetState.Waiting)
                _transitionTo(PetState.Waiting);
        }
        else if (status.IsRunning)
        {
            if (_state != PetState.Working)
                _transitionTo(PetState.Working);
        }
        else if (_state is PetState.Working or PetState.Waiting)
        {
            _transitionTo(PetState.Idle);
        }

        if (_transientStatusUntil <= DateTime.Now)
            SetAgentStatusText();
    }

    private void ReactToAgentCompletion(CodexStatusSnapshot status)
    {
        SetTransientStatus($"{GetAgentName()} 完了: {status.Summary}", 7);
        PlayStatusReaction("wave", 7);
    }

    private void ReactToAgentError(CodexStatusSnapshot status)
    {
        SetTransientStatus($"{GetAgentName()} エラー: {status.Summary}", 8);
        PlayStatusReaction("fail", 8);
    }

    private void PlayStatusReaction(string animation, int seconds)
    {
        _state = PetState.Idle;
        _stateBeforeReactive = PetState.Idle;
        _reactiveEnd = DateTime.Now.AddSeconds(seconds);
        PetSprite.Play(animation);
    }

    private void SetAgentStatusText()
    {
        var status = _lastCodexStatus;
        var agentName = GetAgentName();
        var text = status == null ? $"Watching {agentName}" : status.Summary;
        SetPersistentStatus(text);
    }

    private void SetPersistentStatus(string text)
    {
        StatusBubbleText.Text = text;
        StatusBubble.Visibility = Visibility.Visible;
    }

    private void SetTransientStatus(string text, int seconds)
    {
        StatusBubbleText.Text = text;
        StatusBubble.Visibility = Visibility.Visible;
        _transientStatusUntil = DateTime.Now.AddSeconds(Math.Max(1, seconds));
    }

    private void UpdateTokenRing()
    {
        var showRing = _app.Config.ShowTokenRing;
        RingTrack.Visibility = showRing ? Visibility.Visible : Visibility.Collapsed;
        RingProgress.Visibility = showRing ? Visibility.Visible : Visibility.Collapsed;
        RingBadge.Visibility = showRing ? Visibility.Visible : Visibility.Collapsed;

        var secondaryPercent = _lastCodexStatus?.SecondaryTokenUsagePercent;
        var showSecondaryRing = showRing && secondaryPercent.HasValue;
        RingTrackSecondary.Visibility = showSecondaryRing ? Visibility.Visible : Visibility.Collapsed;
        RingProgressSecondary.Visibility = showSecondaryRing ? Visibility.Visible : Visibility.Collapsed;
        if (!showRing) return;

        var agentName = GetAgentName();
        var statusPercent = _lastCodexStatus?.TokenUsagePercent;
        var statusTokens = _lastCodexStatus?.LastTurnTokens;
        var progress = statusPercent.HasValue
            ? Math.Clamp(statusPercent.Value / 100.0, 0, 1)
            : statusTokens.HasValue
                ? Math.Clamp(statusTokens.Value / (double)Math.Max(1, _app.Config.DailyTokenLimit), 0, 1)
                : Math.Clamp(Math.Max(0, History.GetTodayTotal()) / (double)Math.Max(1, _app.Config.DailyTokenLimit), 0, 1);

        RingProgress.Data = CreateRingGeometry(progress);
        RingProgress.Stroke = new SolidColorBrush(GetRingColor(progress));

        if (secondaryPercent.HasValue)
            RingProgressSecondary.Data = CreateRingGeometry(Math.Clamp(secondaryPercent.Value / 100.0, 0, 1), 8);
        else
            RingProgressSecondary.Data = Geometry.Empty;

        if (IsClaudeProvider() && secondaryPercent.HasValue)
        {
            var primaryLabel = _lastCodexStatus?.TokenUsageLabel ?? $"{progress * 100:0.#}%";
            var secondaryLabel = _lastCodexStatus?.SecondaryTokenUsageLabel ?? $"W {secondaryPercent.Value:0.#}%";
            RingBadgeText.Text = $"{primaryLabel} / {secondaryLabel}";
        }
        else
        {
            RingBadgeText.Text = statusPercent.HasValue
                ? $"{agentName} {statusPercent.Value:0.#}%"
                : statusTokens.HasValue
                    ? $"{agentName} {progress * 100:0.#}%"
                    : $"{FormatTokenCount(History.GetTodayTotal())} / {FormatTokenCount(Math.Max(1, _app.Config.DailyTokenLimit))}";
        }
    }

    private Geometry CreateRingGeometry(double progress, double inset = 0)
    {
        if (progress <= 0)
            return Geometry.Empty;

        var width = PetStage.Width > 0 ? PetStage.Width : Width;
        var height = PetStage.Height > 0 ? PetStage.Height : Math.Max(1, Height - BubbleReservedHeight);
        var stroke = RingProgress.StrokeThickness;
        var radius = Math.Max(1, Math.Min(width, height) / 2 - stroke - 2 - inset);
        var center = new Point(width / 2, height / 2);
        var startAngle = -90.0;
        var endAngle = startAngle + Math.Min(359.99, progress * 360.0);
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, endAngle);
        var largeArc = progress > 0.5;

        var segment = new ArcSegment(end, new System.Windows.Size(radius, radius), 0, largeArc, SweepDirection.Clockwise, true);
        var figure = new PathFigure(start, new PathSegment[] { segment }, false);
        return new PathGeometry(new PathFigure[] { figure });
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static Color GetRingColor(double progress)
    {
        if (progress >= 0.9) return Color.FromRgb(255, 90, 95);
        if (progress >= 0.7) return Color.FromRgb(246, 196, 83);
        return Color.FromRgb(66, 211, 146);
    }


    private void OpenOrFocusAgent()
    {
        if (IsVSCodeLauncher())
            _vsCodeLauncher.OpenOrFocus(_app.Config.VSCodeWorkspacePath);
        else
            _codexLauncher.OpenOrFocus();
    }

    private bool IsClaudeProvider()
    {
        return _app.Config.StatusProvider.Equals("Claude", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsVSCodeLauncher()
    {
        return _app.Config.LauncherTarget.Equals("VSCode", StringComparison.OrdinalIgnoreCase)
            || IsClaudeProvider();
    }

    private static bool LooksLikeWaiting(string text)
    {
        return text.Contains("Waiting", StringComparison.OrdinalIgnoreCase)
            || text.Contains("approval", StringComparison.OrdinalIgnoreCase)
            || text.Contains("input", StringComparison.OrdinalIgnoreCase)
            || text.Contains("承認", StringComparison.OrdinalIgnoreCase)
            || text.Contains("入力", StringComparison.OrdinalIgnoreCase)
            || text.Contains("待機", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeError(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("failed", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("failure", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 4", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("エラー", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("失敗", StringComparison.OrdinalIgnoreCase)
            || text.Contains("例外", StringComparison.OrdinalIgnoreCase);
    }

    private string GetAgentName()
    {
        return IsClaudeProvider() ? "Claude" : "Codex";
    }

    private static string FormatTokenCount(long value)
    {
        if (value >= 1_000_000)
            return $"{value / 1_000_000d:0.#}M";
        if (value >= 1_000)
            return $"{value / 1_000d:0.#}k";
        return value.ToString(CultureInfo.CurrentCulture);
    }
}
