using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TokenPet.Models;
using TokenPet.Services;

namespace TokenPet.Controls;

public partial class PetSprite : UserControl
{
    private BitmapSource? _spritesheet;
    private readonly Dictionary<string, BitmapSource[]> _frames = new();
    private string _currentAnim = "idle";
    private int _currentFrame;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private bool _playing;

    public PetSprite()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSprites();
    }

    public void LoadSpriteSheet(string? path)
    {
        _spritesheet = null;
        _frames.Clear();
        if (path != null)
            _spritesheet = SpriteLoader.LoadSpritesheet(path);
        if (_spritesheet != null)
            ExtractAllFrames();
        else
            LoadProceduralSprites();
        ShowFrame();
    }

    public void LoadProceduralSprites()
    {
        if (_frames.Count > 0) return;
        foreach (var anim in AnimationDefs.All)
        {
            var list = new List<BitmapSource>();
            for (int i = 0; i < anim.FrameCount; i++)
            {
                var frame = SpriteGenerator.GenerateFrame(anim.Name, i);
                if (frame != null) list.Add(frame);
            }
            _frames[anim.Name] = list.ToArray();
        }
        ShowFrame();
    }

    public void Play(string animationName)
    {
        var anim = AnimationDefs.GetByName(animationName);
        if (anim == null) return;
        if (_currentAnim == animationName && _playing) return; // Don't restart same anim
        _currentAnim = animationName;
        _currentFrame = 0;
        _lastFrameTime = DateTime.Now;
        _playing = true;
        ShowFrame();
    }

    public void Stop()
    {
        _playing = false;
    }

    public void Tick()
    {
        if (!_playing) return;

        var anim = AnimationDefs.GetByName(_currentAnim);
        if (anim == null) return;

        var interval = TimeSpan.FromSeconds(1.0 / anim.Fps);
        if (DateTime.Now - _lastFrameTime >= interval)
        {
            _currentFrame = (_currentFrame + 1) % anim.FrameCount;
            _lastFrameTime = DateTime.Now;
            ShowFrame();
        }
    }

    private void LoadSprites()
    {
        var app = Application.Current as App;
        if (app == null) return;

        var spritePath = app.PetManager?.GetActiveSpritePath();
        if (spritePath != null)
        {
            _spritesheet = SpriteLoader.LoadSpritesheet(spritePath);
            if (_spritesheet != null)
            {
                ExtractAllFrames();
                return;
            }
        }
        LoadProceduralSprites();
    }

    private void ExtractAllFrames()
    {
        if (_spritesheet == null) return;
        _frames.Clear();

        int pw = _spritesheet.PixelWidth, ph = _spritesheet.PixelHeight;
        int fw = AnimationDefs.FrameWidth, fh = AnimationDefs.FrameHeight;

        foreach (var anim in AnimationDefs.All)
        {
            var list = new List<BitmapSource>();
            for (int i = 0; i < anim.FrameCount; i++)
            {
                int sx = i * fw, sy = anim.Row * fh;
                if (sx + fw <= pw && sy + fh <= ph)
                {
                    var cropped = new CroppedBitmap(_spritesheet, new Int32Rect(sx, sy, fw, fh));
                    cropped.Freeze();
                    list.Add(cropped);
                }
            }
            if (list.Count > 0)
                _frames[anim.Name] = list.ToArray();
        }
    }

    private void ShowFrame()
    {
        if (_frames.TryGetValue(_currentAnim, out var frames) && _currentFrame < frames.Length)
        {
            SpriteImage.Source = frames[_currentFrame];
        }
    }
}
