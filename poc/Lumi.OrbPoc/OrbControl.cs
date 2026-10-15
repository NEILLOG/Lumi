using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Lumi.OrbPoc;

public enum OrbState { Idle, Listening, Transcribing, Thinking, Tool, Streaming, Done, Attention, Error, Sleep }

public enum EyeMode { Occasional, Always, Off }

/// <summary>光球配色：中心白 → Mid → Body → Edge，外加光暈色與線條色。</summary>
internal readonly record struct OrbPalette(Color Center, Color Mid, Color Body, Color Edge, Color Glow, double GlowAlpha, Color Line)
{
    public static OrbPalette Lerp(in OrbPalette a, in OrbPalette b, double t) => new(
        Mix(a.Center, b.Center, t), Mix(a.Mid, b.Mid, t), Mix(a.Body, b.Body, t), Mix(a.Edge, b.Edge, t),
        Mix(a.Glow, b.Glow, t), a.GlowAlpha + (b.GlowAlpha - a.GlowAlpha) * t, Mix(a.Line, b.Line, t));

    public static Color Mix(Color a, Color b, double t)
    {
        static byte L(byte x, byte y, double u) => (byte)Math.Clamp(Math.Round(x + (y - x) * u), 0, 255);
        return Color.FromArgb(L(a.A, b.A, t), L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t));
    }
}

/// <summary>
/// 光球本體：全部以程式繪製（漸層橢圓 + 變形矩陣），不使用任何圖片或 BlurEffect。
/// 由外部的 FrameDriver 呼叫 <see cref="Advance"/> 推進時間，幀率由外部控制以便量測耗能。
/// 尺寸設計基準：BaseRadius = 52（對應草圖中 104px 的光球）。
/// </summary>
public sealed class OrbControl : Control
{
    // ───────── 配色 ─────────
    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static readonly Color W = Colors.White;

    static readonly Dictionary<OrbState, OrbPalette> Palettes = new()
    {
        [OrbState.Idle]         = new(W, C(0xFFE9C7), C(0xF5B76A), C(0xC9783A), C(0xF5B76A), 0.60, C(0xF5B76A)),
        [OrbState.Listening]    = new(W, C(0xBDF3FF), C(0x4FC3E8), C(0x1B6E9E), C(0x4FC3E8), 0.60, C(0x4FC3E8)),
        [OrbState.Transcribing] = new(W, C(0xD6E4FF), C(0x7C8CF0), C(0x3B3F9E), C(0x7C8CF0), 0.55, C(0x7C8CF0)),
        [OrbState.Thinking]     = new(W, C(0xE6D4FF), C(0x9B6BF2), C(0x4B2A9E), C(0x9B6BF2), 0.60, C(0x9B6BF2)),
        [OrbState.Tool]         = new(W, C(0xFFE3B0), C(0xF2A93B), C(0x9E5A12), C(0xF2A93B), 0.55, C(0xF2A93B)),
        [OrbState.Streaming]    = new(W, C(0xD9FFF3), C(0x5ED6B5), C(0x1E7A66), C(0x5ED6B5), 0.55, C(0x5ED6B5)),
        [OrbState.Done]         = new(W, C(0xD8FFD0), C(0x6BD36A), C(0x2C7A2B), C(0x6BD36A), 0.55, C(0x6BD36A)),
        [OrbState.Attention]    = new(W, C(0xFFD9C2), C(0xF27A3B), C(0x9E3A12), C(0xF27A3B), 0.60, C(0xF27A3B)),
        [OrbState.Error]        = new(W, C(0xFFC9C9), C(0xE5484D), C(0x7A1A1D), C(0xE5484D), 0.55, C(0xE5484D)),
        [OrbState.Sleep]        = new(C(0xE4E7EC), C(0xA8B0BD), C(0x5E6676), C(0x2E333C), C(0x8A93A3), 0.25, C(0x8A93A3)),
    };

    // 思考時與藍色交叉漸變，模擬 CSS hue-rotate（Avalonia 沒有色相濾鏡）
    static readonly OrbPalette ThinkingAlt = new(W, C(0xD6E4FF), C(0x7C8CF0), C(0x3B3F9E), C(0x7C8CF0), 0.55, C(0x7C8CF0));
    static readonly OrbPalette PokePalette = new(W, C(0xFFE1D2), C(0xF59A7A), C(0xC9603A), C(0xF59A7A), 0.60, C(0xF59A7A));

    static readonly Color EyeColor = C(0x17120E);

    // ───────── 外部可設定 ─────────
    public OrbState State { get; private set; } = OrbState.Idle;
    public bool Near { get; set; }
    /// <summary>游標相對光球的方向，-1..1。</summary>
    public Vector LookTarget { get; set; }
    public bool Dragging { get; set; }
    public EyeMode EyeMode { get; set; } = EyeMode.Occasional;

    /// <summary>光球本體直徑（DIP）。控制項本身要比這大約 2.3 倍，留空間給光暈與漣漪。</summary>
    public double OrbDiameter { get; set; } = 80;
    public double BaseRadius => OrbDiameter / 2;
    public Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    public bool IsPoking => _pokePressed;
    public bool IsSettling => _t - _pokeRelease < 1.6;
    public double StateElapsed => _t - _stateStart;

    // ───────── 內部狀態 ─────────
    readonly Random _rng = new();
    double _t, _stateStart;
    OrbPalette _pal = Palettes[OrbState.Idle];

    bool _pokePressed;
    double _pokeRaw, _pokeAmount, _pokeRelease = -100;
    Point _pokePoint;

    double _eyeOpacity, _eyeSize = 1, _eyeSX = 1, _eyeSY = 1;
    Vector _eyeOffset;
    bool _blinkAllowed = true;
    double _blinkStart = -100, _nextBlink = 3, _winkStart = -100;
    double _peekStart = -100, _nextPeek = 2;

    public void SetState(OrbState s)
    {
        if (s == State) return;
        State = s;
        _stateStart = _t;
        if (s == OrbState.Done) _winkStart = _t + 0.25;   // 完成時單眼 wink
    }

    public bool HitOrb(Point local)
    {
        var d = local - Center;
        return Math.Sqrt(d.X * d.X + d.Y * d.Y) <= BaseRadius * 1.15;
    }

    public void BeginPoke(Point local)
    {
        _pokePressed = true;
        _pokeRaw = 0;
        _pokePoint = local;
    }

    public void EndPoke()
    {
        if (!_pokePressed) return;
        _pokePressed = false;
        _pokeRaw = 0;
        _pokeAmount = 0;
        _pokeRelease = _t;
    }

    /// <summary>推進 dt 秒並要求重繪。</summary>
    public void Advance(double dt)
    {
        dt = Math.Clamp(dt, 0, 0.1);
        _t += dt;

        if (_pokePressed)
        {
            _pokeRaw = Math.Min(1, _pokeRaw + dt / 0.12);
            _pokeAmount = 1 - Math.Pow(1 - _pokeRaw, 3);
        }

        _pal = OrbPalette.Lerp(_pal, TargetPalette(), 1 - Math.Exp(-dt * 7));
        UpdateEyes(dt);
        InvalidateVisual();
    }

    OrbPalette TargetPalette()
    {
        var p = Palettes[State];
        if (State == OrbState.Thinking)
            p = OrbPalette.Lerp(p, ThinkingAlt, (Math.Sin(2 * Math.PI * _t / 3) + 1) / 2);
        else if (State == OrbState.Transcribing)
            p = OrbPalette.Lerp(p, Palettes[OrbState.Listening], (Math.Sin(2 * Math.PI * _t / 2) + 1) / 2 * 0.6);

        double poke = Math.Max(_pokeAmount, IsSettling ? Math.Max(0, 1 - (_t - _pokeRelease) / 1.2) : 0);
        return poke > 0 ? OrbPalette.Lerp(p, PokePalette, poke) : p;
    }

    static double BreathPeriod(OrbState s) => s switch
    {
        OrbState.Idle => 4, OrbState.Listening => 1.2, OrbState.Transcribing => 1.6, OrbState.Thinking => 1.4,
        OrbState.Tool => 2, OrbState.Streaming => 0.8, OrbState.Done => 2.4, OrbState.Attention => 1.2,
        OrbState.Error => 2, OrbState.Sleep => 8, _ => 4,
    };

    // ───────── 眼睛 ─────────
    double Next(double min, double max) => min + _rng.NextDouble() * (max - min);

    void UpdateEyes(double dt)
    {
        double size = 1, sx = 1, sy = 1;
        Vector off = default;
        bool blink = true;

        switch (State)
        {
            case OrbState.Idle: if (Near) off = new Vector(LookTarget.X * 3, LookTarget.Y * 2.5); break;
            case OrbState.Listening: size = 1.2; off = new Vector(0, -3); break;
            case OrbState.Transcribing: off = new Vector(5 * Math.Sin(2 * Math.PI * _t / 3), -1); break;
            case OrbState.Thinking: off = new Vector(5 * Math.Sin(2 * Math.PI * _t / 2.4), -6); break;
            case OrbState.Tool: sy = 0.45; off = new Vector(0, 3); blink = false; break;
            case OrbState.Attention: size = 1.2; break;
            case OrbState.Error: sx = 1.15; sy = 0.3; blink = false; break;
            case OrbState.Sleep: sy = 0.15; off = new Vector(0, 3); blink = false; break;
        }
        if (Dragging)
        {
            off = new Vector(3 * Math.Cos(_t * 7), 3 * Math.Sin(_t * 7));   // 暈頭轉圈
            blink = false;
        }
        if (_pokeAmount > 0)
        {
            sx += (1.25 - sx) * _pokeAmount;   // >_<
            sy += (0.15 - sy) * _pokeAmount;
            blink = false;
        }

        // 偶爾現身：待機、沒人靠近時，每 9–15 秒探出來 4 秒，期間眨眼、有機會 wink
        double opacityTarget = EyeMode == EyeMode.Off ? 0 : 1;
        if (EyeMode == EyeMode.Occasional && State == OrbState.Idle && !Near && !Dragging
            && _pokeAmount <= 0 && !IsSettling)
        {
            if (_t >= _nextPeek)
            {
                _peekStart = _t;
                _nextPeek = _t + Next(9, 15);
                _blinkStart = _t + 0.9;
                _nextBlink = _t + 5;
                if (_rng.NextDouble() < 0.6) _winkStart = _t + 2.2;
            }
            opacityTarget = _t - _peekStart < 4.0 ? 1 : 0;
        }

        double k = 1 - Math.Exp(-dt * 12);
        _eyeOpacity += (opacityTarget - _eyeOpacity) * (1 - Math.Exp(-dt * 8));
        _eyeSize += (size - _eyeSize) * k;
        _eyeSX += (sx - _eyeSX) * k;
        _eyeSY += (sy - _eyeSY) * k;
        _eyeOffset += (off - _eyeOffset) * k;
        _blinkAllowed = blink;

        if (blink && _t >= _nextBlink)
        {
            _blinkStart = _t;
            _nextBlink = State == OrbState.Streaming ? _t + Next(1.5, 3) : _t + Next(3, 7);
        }
    }

    double BlinkFactor()
    {
        double e = _t - _blinkStart;
        if (!_blinkAllowed || e < 0 || e > 0.16) return 1;
        return 1 - 0.9 * Math.Sin(Math.PI * e / 0.16);
    }

    double WinkFactor()
    {
        double e = _t - _winkStart;
        if (e < 0 || e > 0.42) return 1;
        if (e < 0.08) return 1 - 0.9 * e / 0.08;
        if (e < 0.32) return 0.1;
        return 0.1 + 0.9 * (e - 0.32) / 0.10;
    }

    // ───────── 繪製 ─────────
    static Color A(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    static RadialGradientBrush Radial(params (Color color, double offset)[] stops) => Radial(new RelativePoint(0.5, 0.5, RelativeUnit.Relative), stops);

    static RadialGradientBrush Radial(RelativePoint origin, params (Color color, double offset)[] stops)
    {
        var b = new RadialGradientBrush { GradientOrigin = origin };
        foreach (var (color, offset) in stops) b.GradientStops.Add(new GradientStop(color, offset));
        return b;
    }

    /// <summary>
    /// 柔邊徑向漸層：以高斯曲線取 16 個色標，並在外緣用 smoothstep 收到 0。
    /// 只有 2–3 個色標的線性漸層會在邊緣出現明顯的「斷層圈」。
    /// 只用在正圓上（rx == ry），漸層半徑才會剛好等於圖形半徑。
    /// </summary>
    static RadialGradientBrush Soft(Color c, double peak, double sigma = 0.4, int steps = 16)
    {
        var b = new RadialGradientBrush();
        for (int i = 0; i <= steps; i++)
        {
            double x = (double)i / steps;
            double g = Math.Exp(-(x * x) / (2 * sigma * sigma));
            double edge = x <= 0.6 ? 1 : 1 - SmoothStep((x - 0.6) / 0.4);
            b.GradientStops.Add(new GradientStop(A(c, peak * g * edge), x));
        }
        return b;
    }

    /// <summary>
    /// 用漸層筆刷畫一個正圓：在控制項中心畫滿版圓，再用變形縮放、移到目標位置。
    /// 漸層一定會剛好對齊這個圓，不受筆刷相對座標的計算方式影響。
    /// </summary>
    void FillCircle(DrawingContext ctx, IBrush brush, Point at, double radius)
    {
        double half = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (half <= 0 || radius <= 0) return;
        var mid = Center;
        var m = Matrix.CreateTranslation(-mid.X, -mid.Y)
              * Matrix.CreateScale(radius / half, radius / half)
              * Matrix.CreateTranslation(at.X, at.Y);
        using (ctx.PushTransform(m))
            ctx.DrawEllipse(brush, null, mid, half, half);
    }

    static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    public override void Render(DrawingContext ctx)
    {
        double R = BaseRadius;
        if (R <= 0) return;
        var c = Center;
        double k = R / 52.0;   // 設計稿比例
        var pal = _pal;

        // ── 動態：呼吸 / 閃動 / 彈跳 / 搖頭 / 果凍 ──
        double ph = Math.Sin(2 * Math.PI * _t / BreathPeriod(State));
        double s = State == OrbState.Streaming
            ? 1 + 0.025 * Math.Sin(_t * 17) + 0.015 * Math.Sin(_t * 29.3)
            : 0.99 + 0.05 * ph;
        if (Near && State == OrbState.Idle) s *= 1.03;
        double sx = s, sy = s, tx = 0, ty = 0;

        double e = StateElapsed;
        switch (State)
        {
            case OrbState.Attention:
                ty = -5 * k * (1 - Math.Cos(2 * Math.PI * _t / 1.2));
                break;
            case OrbState.Error:
                double m = e % 2.0;
                if (m < 0.6) tx = 7 * k * Math.Sin(m * 42) * (1 - m / 0.6);
                break;
            case OrbState.Done:
                if (e < 0.4) { double pop = 1 + 0.18 * Math.Sin(Math.PI * e / 0.4); sx *= pop; sy *= pop; }
                break;
        }

        if (Dragging)
        {
            double w = Math.Sin(_t * 9);
            sx *= 1 + 0.08 * w;
            sy *= 1 - 0.08 * w;
        }

        // 戳：按住時壓扁；放開後阻尼彈簧 QQ 回彈
        sx *= 1 - 0.14 * _pokeAmount;
        sy *= 1 - 0.10 * _pokeAmount;
        double re = _t - _pokeRelease;
        double settle = 0;
        if (!_pokePressed && re >= 0 && re < 1.6)
        {
            settle = Math.Exp(-4.2 * re);
            double u = settle * Math.Cos(16 * re);
            double wob = 0.05 * settle * Math.Sin(16 * re);
            sx *= 1 - 0.14 * u + wob;
            sy *= 1 - 0.10 * u - wob;
        }

        var gc = new Point(c.X + tx, c.Y + ty);

        // ── 光暈：中心色 → 透明的徑向漸層（取代昂貴的 BlurEffect）──
        double breath01 = (ph + 1) / 2;
        double glowR = R * 2.1 * (0.92 + 0.10 * breath01) * (Near ? 1.06 : 1);
        double ga = Math.Min(1, pal.GlowAlpha * (0.75 + 0.35 * breath01) * (Near && State == OrbState.Idle ? 1.3 : 1));
        FillCircle(ctx, Soft(pal.Glow, ga, sigma: 0.36), gc, glowR);

        // ── 聆聽：漣漪 ──
        if (State == OrbState.Listening)
        {
            for (int i = 0; i < 2; i++)
            {
                double p = (_t / 1.6 + i * 0.5) % 1;
                var pen = new Pen(new SolidColorBrush(A(pal.Line, 0.7 * (1 - p))), 2 * k);
                ctx.DrawEllipse(null, pen, gc, R * (1 + 0.9 * p), R * (1 + 0.9 * p));
            }
        }

        // ── 呼叫工具：旋轉弧線 ──
        if (State == OrbState.Tool)
        {
            double rr = R * 1.27, a0 = 2 * Math.PI * (_t / 1.1), a1 = a0 + Math.PI * 0.95;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(gc.X + rr * Math.Cos(a0), gc.Y + rr * Math.Sin(a0)), false);
                g.ArcTo(new Point(gc.X + rr * Math.Cos(a1), gc.Y + rr * Math.Sin(a1)), new Size(rr, rr), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(pal.Line), 3 * k, lineCap: PenLineCap.Round), geo);
        }

        // ── 本體（套用變形）──
        var mtx = Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(gc.X, gc.Y);
        using (ctx.PushTransform(mtx))
        {
            // 本體漸層：對應草圖 CSS 的 radial-gradient(circle at 35% 30%, …)。
            // 注意：不要讓 GradientOrigin 和 Center 不同 —— Skia 會改用「雙點錐形漸層」，
            // 在焦點附近畫出一圈硬邊白色圓盤（就是之前左上那顆沒暈開的白圈）。
            // 改法：用一般同心漸層，把圓心放在左上焦點、半徑放大到 1.9R（約等於 CSS 的 farthest-corner），
            // 再裁切成光球的圓形。
            var core = new RadialGradientBrush();
            core.GradientStops.Add(new GradientStop(pal.Center, 0));
            core.GradientStops.Add(new GradientStop(pal.Mid, 0.30));
            core.GradientStops.Add(new GradientStop(pal.Body, 0.70));
            core.GradientStops.Add(new GradientStop(pal.Edge, 1));
            var focal = new Point(c.X - 0.30 * R, c.Y - 0.40 * R);
            using (ctx.PushGeometryClip(new EllipseGeometry(new Rect(c.X - R, c.Y - R, 2 * R, 2 * R))))
            {
                FillCircle(ctx, core, focal, 1.9 * R);

                // 高光：柔邊、略扁，對應草圖裡 blur(5px) 的那塊亮點
                var hl = new Point(c.X - 0.29 * R, c.Y - 0.48 * R);
                var flat = Matrix.CreateTranslation(-hl.X, -hl.Y) * Matrix.CreateScale(1, 0.72) * Matrix.CreateTranslation(hl.X, hl.Y);
                using (ctx.PushTransform(flat))
                    FillCircle(ctx, Soft(W, 0.62, sigma: 0.40), hl, 0.42 * R);
            }

            // 戳入凹痕
            double dent = _pokeAmount > 0 ? _pokeAmount : (re >= 0 && re < 0.25 ? 1 - re / 0.25 : 0);
            if (dent > 0.01)
            {
                var d = _pokePoint - c;
                double len = Math.Sqrt(d.X * d.X + d.Y * d.Y), max = 0.6 * R;
                var dp = len > max ? c + d * (max / len) : _pokePoint;
                var dentColor = C(0x8C3C1E);
                double dr = 0.40 * R * (0.6 + 0.4 * dent);
                FillCircle(ctx, Soft(dentColor, 0.36 * dent, sigma: 0.42), dp, dr);
            }

            // 腮紅
            double blush = _pokeAmount > 0 ? _pokeAmount : (re >= 0 && re < 1.6 ? Math.Clamp(1 - (re - 0.6) / 1.0, 0, 1) : 0);
            if (blush > 0.01)
            {
                var bc = C(0xFF788C);
                var bb = Soft(bc, 0.6 * blush, sigma: 0.42);
                // 腮紅要扁橢圓：用變形把正圓壓扁，漸層會跟著壓扁
                foreach (double side in new[] { -1.0, 1.0 })
                {
                    var bp = new Point(c.X + side * 0.6 * R, c.Y + 0.24 * R);
                    var squash = Matrix.CreateTranslation(-bp.X, -bp.Y) * Matrix.CreateScale(1, 0.5) * Matrix.CreateTranslation(bp.X, bp.Y);
                    using (ctx.PushTransform(squash))
                        FillCircle(ctx, bb, bp, 0.2 * R);
                }
            }

            // 眼睛：兩個黑色小圓點，間距 26（邊到邊）、直徑 9（設計稿 104px 光球）
            if (_eyeOpacity > 0.01)
            {
                var eb = new SolidColorBrush(A(EyeColor, _eyeOpacity));
                double r0 = 4.5 * k * _eyeSize;
                double dx = 17.5 * k;
                double ex = c.X + _eyeOffset.X * k, ey = c.Y + (1 + _eyeOffset.Y) * k;
                double blinkF = BlinkFactor();
                ctx.DrawEllipse(eb, null, new Point(ex - dx, ey), r0 * _eyeSX, Math.Max(0.6, r0 * _eyeSY * blinkF));
                ctx.DrawEllipse(eb, null, new Point(ex + dx, ey), r0 * _eyeSX, Math.Max(0.6, r0 * _eyeSY * blinkF * WinkFactor()));
            }
        }

        // ── 思考：繞行粒子 ──
        if (State == OrbState.Thinking)
        {
            (double off, double size, uint col)[] dots = { (0, 4, 0xE6D4FF), (2.2, 3, 0xCFE0FF), (4.1, 2.5, 0xF1E6FF) };
            foreach (var (off, size, col) in dots)
            {
                double a = 2 * Math.PI * _t / 2.4 + off;
                var p = new Point(gc.X + R * 1.35 * Math.Cos(a), gc.Y + R * 1.35 * Math.Sin(a));
                var cc = C(col);
                FillCircle(ctx, Soft(cc, 0.5), p, size * 3.5 * k);
                ctx.DrawEllipse(new SolidColorBrush(cc), null, p, size * k, size * k);
            }
        }
    }
}
