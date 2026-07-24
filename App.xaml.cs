using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using AgentCompanion.Services;
using AgentCompanion.Windows;
using SkiaSharp;
using WinForms = System.Windows.Forms;

namespace AgentCompanion;

public partial class App : Application
{
    public PetManager PetManager { get; } = new();
    public AppConfig Config { get; } = new();
    private MainWindow? _mainWindow;
    private Timer? _proxyTimer;
    private SettingsWindow? _settingsWindow;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _togglePetMenuItem;
    private SingleInstanceService? _singleInstance;
    private bool _persistOnExit;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var showSettings = e.Args.Any(arg => arg.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        _singleInstance = SingleInstanceService.Acquire();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.NotifyPrimary(showSettings);
            Shutdown();
            return;
        }

        RegisterGlobalExceptionHandlers();
        BootstrapData();
        Config.Load();
        new StartupService().MigrateLegacyRegistration();
        ProxyServer.EnableDebugLog = Config.ProxyDebugLogEnabled;
        PetManager.Setup();
        if (!string.IsNullOrEmpty(Config.ActivePetId))
            PetManager.SetActivePet(Config.ActivePetId);
        if (!Config.ActivePetId.Equals(PetManager.ActivePetId, StringComparison.Ordinal))
        {
            Config.ActivePetId = PetManager.ActivePetId;
            Config.Save();
        }
        PetManager.PetChanged += _ => RefreshTrayIcon();

        _mainWindow = new MainWindow();
        _mainWindow.Show();
        CreateTrayIcon();
        if (showSettings)
            Dispatcher.BeginInvoke(ShowSettings);
        StartProxyIfConfigured();
        _persistOnExit = true;

        _proxyTimer = new Timer(_ => PollProxySafely(), null, 0, 100);
        _singleInstance.Listen(
            () => Dispatch(ActivatePrimaryInstance),
            () => Dispatch(ShowSettings));
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled UI exception.", args.Exception);
            args.Handled = true;
            Shutdown(-1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                AppLogger.Error("Unhandled application exception.", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "AgentCompanion",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        _togglePetMenuItem = new WinForms.ToolStripMenuItem(
            "キャラクターを非表示",
            null,
            (_, _) => TogglePetVisibility());
        menu.Items.Add(_togglePetMenuItem);
        menu.Items.Add("\u8a2d\u5b9a", null, (_, _) => Dispatch(ShowSettings));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("\u7d42\u4e86", null, (_, _) => Dispatch(() => Current.Shutdown()));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatch(ShowPet);
        UpdatePetMenuText();
    }

    private void StartProxyIfConfigured()
    {
        if (!Config.ProxyEnabled || _mainWindow == null)
            return;

        try
        {
            _mainWindow.Proxy.Start(Config.ProxyPort, _mainWindow.Proxy.Targets);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Proxy startup failed.", ex);
            _trayIcon?.ShowBalloonTip(
                5000,
                "AgentCompanion",
                "proxy\u3092\u958b\u59cb\u3067\u304d\u307e\u305b\u3093\u3067\u3057\u305f\u3002\u8a2d\u5b9a\u3067\u30dd\u30fc\u30c8\u3092\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002",
                WinForms.ToolTipIcon.Warning);
        }
    }

    private void PollProxySafely()
    {
        try
        {
            _mainWindow?.Proxy.Poll();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Proxy polling failed.", ex);
        }
    }

    private void ActivatePrimaryInstance()
    {
        ShowPet();
        _mainWindow?.Activate();
        if (_settingsWindow?.IsVisible == true)
            _settingsWindow.Activate();
    }

    private static void BootstrapData()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var petsDir = Path.Combine(exeDir, "pet_data", "pets");

        try
        {
            Directory.CreateDirectory(petsDir);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith("pets/", StringComparison.Ordinal))
                    continue;
                var relative = name[5..];
                var destination = Path.Combine(petsDir, relative);
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (destinationDirectory != null)
                    Directory.CreateDirectory(destinationDirectory);

                using var source = assembly.GetManifestResourceStream(name);
                if (source == null)
                    continue;
                using var target = File.Create(destination);
                source.CopyTo(target);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Embedded character extraction failed.", ex);
        }
    }

    public void ShowSettings()
    {
        ShowPet();
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.SetContext(
                _mainWindow!.PetSpriteControl,
                PetManager,
                _mainWindow.Proxy,
                onScaleChanged: () =>
                {
                    _mainWindow.LoadPetSprite();
                    _mainWindow.SnapWindowToPet();
                });
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (_settingsWindow.IsVisible)
        {
            _settingsWindow.Hide();
            return;
        }

        _settingsWindow.Left = _mainWindow!.Left + _mainWindow.Width + 10;
        _settingsWindow.Top = _mainWindow.Top;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void TogglePetVisibility()
    {
        Dispatch(() =>
        {
            if (_mainWindow?.IsVisible == true)
                HidePet();
            else
                ShowPet();
        });
    }

    private void ShowPet()
    {
        _mainWindow?.ShowPet();
        UpdatePetMenuText();
    }

    private void HidePet()
    {
        _settingsWindow?.Hide();
        _mainWindow?.HidePet();
        UpdatePetMenuText();
    }

    private void UpdatePetMenuText()
    {
        if (_togglePetMenuItem == null)
            return;
        _togglePetMenuItem.Text = _mainWindow?.IsVisible == true
            ? "キャラクターを非表示"
            : "キャラクターを表示";
    }

    private void RefreshTrayIcon()
    {
        if (_trayIcon == null)
            return;
        var oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = LoadTrayIcon();
        oldIcon?.Dispose();
    }

    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.Invoke(action);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _proxyTimer?.Dispose();
        _settingsWindow?.Close();
        if (_persistOnExit && _mainWindow != null)
        {
            Config.WindowX = _mainWindow.Left;
            Config.WindowY = _mainWindow.Top;
            Config.Save();
            _mainWindow.History.Flush();
            _mainWindow.Proxy.Stop();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private Icon LoadTrayIcon()
    {
        var petIcon = TryLoadActivePetIcon();
        if (petIcon != null)
            return petIcon;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("favicon.ico");
        if (stream != null)
            return new Icon(stream);

        using var bitmap = new Bitmap(16, 16);
        for (var x = 0; x < 16; x++)
        {
            for (var y = 0; y < 16; y++)
            {
                if ((x - 8) * (x - 8) + (y - 8) * (y - 8) <= 36)
                    bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(0, 128, 255));
            }
        }
        return CreateIconFromBitmap(bitmap);
    }

    private Icon? TryLoadActivePetIcon()
    {
        try
        {
            var pet = PetManager.Pets.FirstOrDefault(item => item.Id == PetManager.ActivePetId);
            if (pet == null)
                return null;

            var previewPath = Path.Combine(pet.Directory, "preview-idle.png");
            if (File.Exists(previewPath))
            {
                using var preview = new Bitmap(previewPath);
                return CreateIconFromBitmap(preview);
            }

            var spritePath = PetManager.GetActiveSpritePath();
            if (spritePath != null)
            {
                using var sprite = LoadBitmapFromSprite(spritePath);
                if (sprite != null)
                {
                    using var firstCell = sprite.Clone(
                        new Rectangle(0, 0, Math.Min(192, sprite.Width), Math.Min(208, sprite.Height)),
                        sprite.PixelFormat);
                    return CreateIconFromBitmap(firstCell);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tray character icon loading failed.", ex);
        }

        return null;
    }

    private static Bitmap? LoadBitmapFromSprite(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp")
            return new Bitmap(path);
        if (extension != ".webp")
            return null;

        using var skBitmap = SKBitmap.Decode(path);
        if (skBitmap == null)
            return null;
        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        using var bitmap = new Bitmap(stream);
        return new Bitmap(bitmap);
    }

    private static Icon CreateIconFromBitmap(Bitmap source)
    {
        using var iconBitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(iconBitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            var bounds = FindVisibleBounds(source);
            var scale = Math.Min(30f / bounds.Width, 30f / bounds.Height);
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            var destination = new Rectangle((32 - width) / 2, 32 - height - 1, width, height);
            graphics.DrawImage(source, destination, bounds, GraphicsUnit.Pixel);
        }

        var handle = iconBitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Rectangle FindVisibleBounds(Bitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = 0;
        var bottom = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A <= 8)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }

        return right <= left || bottom <= top
            ? new Rectangle(0, 0, bitmap.Width, bitmap.Height)
            : Rectangle.FromLTRB(left, top, right, bottom);
    }
}
