using Avalonia.Controls;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 設定頁的預覽區：用和小精靈視窗同樣的方式（<see cref="SpriteLimits.Fit"/>、<see cref="SpriteStateResolver"/>、
/// <see cref="FrameDriver"/>）播放所選角色的某個狀態。角色一樣只認識 Abstractions，不知道自己是在預覽。
/// 只在可見時才驅動畫面，不可見就停下來。
/// </summary>
public sealed class SpritePreviewHost : IDisposable
{
    private readonly Border _host;
    private readonly FrameDriver _driver;
    private ISpriteFactory? _factory;
    private ISpriteView? _view;
    private SpriteState _requested = SpriteState.Idle;
    private bool _running;

    public SpritePreviewHost(Border host, TopLevel top)
    {
        _host = host;
        _driver = new FrameDriver(top, dt =>
        {
            _view?.Advance(dt);
            if (_view is not null)
                _driver!.TargetFps = _view.PreferredFps;
        });
    }

    public ISpriteView? View => _view;

    /// <summary>目前實際給角色的狀態（已依退化規則解析）。</summary>
    public SpriteState? AppliedState { get; private set; }

    public void Show(ISpriteFactory factory)
    {
        _factory = factory;
        var options = SpriteLimits.Fit(factory.Manifest, SpriteLimits.DefaultTier).Options;
        _view = factory.Create(options);
        _host.Child = _view.Visual;
        _host.Width = options.Outer.Width;
        _host.Height = options.Outer.Height;
        AppliedState = null;
        SetState(_requested);
    }

    public void SetState(SpriteState state)
    {
        _requested = state;
        if (_view is null || _factory is null)
            return;
        var resolved = SpriteStateResolver.Resolve(state, _factory.Manifest);
        AppliedState = resolved;
        _view.SetState(resolved);
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _driver.Start();
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        _driver.Stop();
    }

    public void Dispose() => Stop();
}
