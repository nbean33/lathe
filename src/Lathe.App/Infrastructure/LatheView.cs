using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clipper2Lib;
using Lathe.Cam;
using Lathe.Core.Geometry;

namespace Lathe.App.Infrastructure;

/// <summary>What to draw. All coordinates are real (Z, radius) in mm; regions are upper-half and get mirrored.</summary>
public sealed record Scene
{
    public PathsD? Stock { get; init; }
    public PathsD? Part { get; init; }
    public PathsD? SimStock { get; init; }
    public PathsD? Gouge { get; init; }
    public PathsD? Uncut { get; init; }
    public (double JawZ, double ChuckR, double StockR)? Chuck { get; init; }
    public List<(MoveKind Kind, Pt A, Pt B, int Op)> Path { get; init; } = new();
    public int HighlightOp { get; init; } = -1;
    public Pt? ToolPos { get; set; }
    public PathD? ToolShape { get; set; }
    public string? Caption { get; init; }
}

/// <summary>2D half-section view: Z to the right, X (diameter) up, mirrored below the centerline. Wheel = zoom, drag = pan, double-click = fit.</summary>
public sealed class LatheView : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(Scene), typeof(LatheView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((LatheView)d).OnSceneChanged((Scene?)e.OldValue, (Scene?)e.NewValue)));
    public Scene? Scene { get => (Scene?)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }

    private double _scale = 5, _ox, _oy; private bool _fitted; private Point? _drag; private Point _mouse;

    private static readonly Brush Bg = Freeze(new SolidColorBrush(Color.FromRgb(250, 251, 253)));
    private static readonly Brush StockFill = Freeze(new SolidColorBrush(Color.FromArgb(70, 160, 170, 185)));
    private static readonly Brush PartFill = Freeze(new SolidColorBrush(Color.FromRgb(118, 150, 196)));
    private static readonly Brush SimFill = Freeze(new SolidColorBrush(Color.FromArgb(150, 225, 160, 70)));
    private static readonly Brush GougeFill = Freeze(new SolidColorBrush(Color.FromArgb(220, 220, 30, 30)));
    private static readonly Brush UncutFill = Freeze(new SolidColorBrush(Color.FromArgb(200, 240, 200, 0)));
    private static readonly Brush ChuckFill = Freeze(new SolidColorBrush(Color.FromRgb(70, 74, 82)));
    private static readonly Pen Outline = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(40, 60, 100)), 1));
    private static readonly Pen Center = Freeze(new Pen(Brushes.Gray, 0.8) { DashStyle = new DashStyle(new double[] { 10, 4, 2, 4 }, 0) });
    private static readonly Pen RapidPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(210, 60, 60)), 0.8) { DashStyle = DashStyles.Dash });
    private static readonly Pen FeedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(20, 110, 200)), 1));
    private static readonly Pen FeedHi = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0, 170, 90)), 1.6));
    private static readonly Pen ThreadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(150, 40, 180)), 1.2));
    private static readonly Pen ToolPen = Freeze(new Pen(Brushes.Black, 1.2));
    private static readonly Brush ToolFill = Freeze(new SolidColorBrush(Color.FromArgb(160, 255, 220, 60)));
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush Label = Freeze(new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public LatheView()
    {
        ClipToBounds = true; Focusable = true;
        MouseWheel += (_, e) => { var p = e.GetPosition(this); Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2, p); };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Fit(); return; } _drag = e.GetPosition(this); CaptureMouse(); };
        MouseLeftButtonUp += (_, _) => { _drag = null; ReleaseMouseCapture(); };
        MouseMove += (_, e) =>
        {
            _mouse = e.GetPosition(this);
            if (_drag is Point d) { _ox += _mouse.X - d.X; _oy += _mouse.Y - d.Y; _drag = _mouse; }
            InvalidateVisual();
        };
        SizeChanged += (_, _) => { if (!_fitted) Fit(); else InvalidateVisual(); };
    }

    private void OnSceneChanged(Scene? old, Scene? now)
    {
        if (old is null || !_fitted) Fit();
    }

    private void Zoom(double f, Point at)
    {
        double wz = (at.X - _ox) / _scale, wr = (_oy - at.Y) / _scale;
        _scale = Math.Clamp(_scale * f, 0.2, 2000);
        _ox = at.X - wz * _scale; _oy = at.Y + wr * _scale;
        InvalidateVisual();
    }

    public void Fit()
    {
        var s = Scene;
        if (s is null || ActualWidth < 10 || ActualHeight < 10) return;
        double zMin = double.MaxValue, zMax = double.MinValue, rMax = 1;
        void Acc(PathsD? p) { if (p is null || p.Count == 0) return; var b = Clipper.GetBounds(p); zMin = Math.Min(zMin, b.left); zMax = Math.Max(zMax, b.right); rMax = Math.Max(rMax, Math.Max(Math.Abs(b.bottom), Math.Abs(b.top))); }
        Acc(s.Stock); Acc(s.Part);
        foreach (var m in s.Path) { zMin = Math.Min(zMin, Math.Min(m.A.Z, m.B.Z)); zMax = Math.Max(zMax, Math.Max(m.A.Z, m.B.Z)); rMax = Math.Max(rMax, Math.Max(Math.Abs(m.A.R), Math.Abs(m.B.R))); }
        if (s.Chuck is { } c) zMin = Math.Min(zMin, c.JawZ - 15);
        if (zMin > zMax) { zMin = -50; zMax = 10; }
        double w = zMax - zMin + 10, h = 2 * rMax + 10;
        _scale = Math.Max(0.2, Math.Min(ActualWidth / w, ActualHeight / h) * 0.92);
        _ox = ActualWidth / 2 - (zMin + zMax) / 2 * _scale;
        _oy = ActualHeight / 2;
        _fitted = true;
        InvalidateVisual();
    }

    private Point S(double z, double r) => new(_ox + z * _scale, _oy - r * _scale);

    private Geometry Region(PathsD paths, bool mirror)
    {
        var g = new StreamGeometry { FillRule = System.Windows.Media.FillRule.EvenOdd };
        using (var c = g.Open())
        {
            foreach (var sign in mirror ? new[] { 1, -1 } : new[] { 1 })
                foreach (var p in paths)
                {
                    if (p.Count < 3) continue;
                    c.BeginFigure(S(p[0].x, sign * p[0].y), true, true);
                    c.PolyLineTo(p.Skip(1).Select(q => S(q.x, sign * q.y)).ToList(), true, false);
                }
        }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var s = Scene;
        if (s is null) return;
        if (s.Chuck is { } ch)
        {
            double jawH = Math.Max(ch.StockR + 8, ch.StockR * 1.6);
            foreach (var sign in new[] { 1, -1 })
            {
                dc.DrawRectangle(ChuckFill, null, new Rect(S(ch.JawZ - 18, sign > 0 ? jawH : -ch.StockR), S(ch.JawZ, sign > 0 ? ch.StockR : -jawH)));
                dc.DrawRectangle(ChuckFill, null, new Rect(S(ch.JawZ - 60, sign > 0 ? ch.ChuckR : -18), S(ch.JawZ - 18, sign > 0 ? 18 : -ch.ChuckR)));
            }
        }
        if (s.Stock is not null) dc.DrawGeometry(StockFill, null, Region(s.Stock, true));
        if (s.SimStock is not null) dc.DrawGeometry(SimFill, null, Region(s.SimStock, true));
        if (s.Part is not null) dc.DrawGeometry(s.SimStock is null ? PartFill : null, Outline, Region(s.Part, true));
        if (s.Uncut is not null && s.Uncut.Count > 0) dc.DrawGeometry(UncutFill, null, Region(s.Uncut, true));
        if (s.Gouge is not null && s.Gouge.Count > 0) dc.DrawGeometry(GougeFill, null, Region(s.Gouge, true));

        dc.DrawLine(Center, new Point(0, _oy), new Point(ActualWidth, _oy));
        var z0 = S(0, 0);
        dc.DrawLine(Center, new Point(z0.X, 0), new Point(z0.X, ActualHeight));

        foreach (var m in s.Path)
        {
            var pen = m.Kind switch { MoveKind.Rapid => RapidPen, MoveKind.Thread => ThreadPen, _ => m.Op == s.HighlightOp ? FeedHi : FeedPen };
            dc.DrawLine(pen, S(m.A.Z, m.A.R), S(m.B.Z, m.B.R));
        }

        if (s.ToolPos is Pt tp)
        {
            if (s.ToolShape is not null) dc.DrawGeometry(ToolFill, ToolPen, Region(new PathsD { Geo.Translate(s.ToolShape, tp) }, false));
            var c = S(tp.Z, tp.R);
            dc.DrawLine(ToolPen, new Point(c.X - 8, c.Y), new Point(c.X + 8, c.Y));
            dc.DrawLine(ToolPen, new Point(c.X, c.Y - 8), new Point(c.X, c.Y + 8));
        }

        // Cursor readout + caption.
        double wz = (_mouse.X - _ox) / _scale, wr = (_oy - _mouse.Y) / _scale;
        var txt = new FormattedText($"Z {wz:0.00}   X Ø{2 * Math.Abs(wr):0.00}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, Brushes.Black, 1.0);
        dc.DrawRectangle(Label, null, new Rect(2, ActualHeight - 20, txt.Width + 8, 18));
        dc.DrawText(txt, new Point(6, ActualHeight - 18));
        if (s.Caption is not null)
        {
            var cap = new FormattedText(s.Caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, Brushes.Black, 1.0) { MaxTextWidth = Math.Max(50, ActualWidth - 12) };
            dc.DrawRectangle(Label, null, new Rect(2, 2, cap.Width + 8, cap.Height + 4));
            dc.DrawText(cap, new Point(6, 4));
        }
        var hint = new FormattedText("wheel: zoom · drag: pan · double-click: fit", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Brushes.Silver, 1.0);
        dc.DrawText(hint, new Point(ActualWidth - hint.Width - 6, ActualHeight - 16));
    }
}
