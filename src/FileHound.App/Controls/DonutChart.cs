using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using FileHound.App.ViewModels;

namespace FileHound.App.Controls;

/// <summary>Lightweight donut chart: one arc per <see cref="CategorySlice"/> with a small gap between slices.</summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(IEnumerable), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSlicesChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(DonutChart), new FrameworkPropertyMetadata(34.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Slices
    {
        get => (IEnumerable?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private static void OnSlicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (DonutChart)d;
        if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += chart.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - 2, inner = Math.Max(0, outer - Thickness);
        var slices = Slices?.OfType<CategorySlice>().Where(s => s.Fraction > 0).ToList() ?? [];
        if (slices.Count == 0)
        {
            DrawRing(dc, center, outer, inner, (Brush?)TryFindResource("SurfaceSunken") ?? Brushes.LightGray);
            return;
        }
        if (slices.Count == 1)
        {
            DrawRing(dc, center, outer, inner, slices[0].Brush);
            return;
        }
        const double gapDeg = 2.5;
        double total = slices.Sum(s => s.Fraction);
        double angle = -90;
        foreach (var s in slices)
        {
            double sweep = s.Fraction / total * 360;
            double drawn = Math.Max(0.6, sweep - gapDeg);
            dc.DrawGeometry(s.Brush, null, Arc(center, outer, inner, angle + gapDeg / 2, drawn));
            angle += sweep;
        }
    }

    private static void DrawRing(DrawingContext dc, Point c, double outer, double inner, Brush brush)
    {
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(c, outer, outer), new EllipseGeometry(c, inner, inner));
        dc.DrawGeometry(brush, null, ring);
    }

    private static Geometry Arc(Point c, double outer, double inner, double startDeg, double sweepDeg)
    {
        static Point P(Point c, double r, double deg)
        {
            double rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        double end = startDeg + sweepDeg;
        bool large = sweepDeg > 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(c, outer, startDeg), isFilled: true, isClosed: true);
            ctx.ArcTo(P(c, outer, end), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(P(c, inner, end), true, true);
            ctx.ArcTo(P(c, inner, startDeg), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}
