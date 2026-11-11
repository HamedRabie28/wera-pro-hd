// Views/SalesTrendChart.cs — WERA PRO HD (G9 · HOME-01)
// رسم اتجاه صافي المبيعات: مساحي هادئ بلون العلامة — Polyline/Path داخل Canvas
// يُعاد بناؤه عند تغيير النقاط أو الحجم. لا مكتبات خارجية ولا أرقام مزيفة:
// نقاط SeriesPoint تأتي من الخادم (صافي يومي موثق).
namespace WeraPro.Desktop.Views;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WeraPro.Desktop.ViewModels;

public sealed class SalesTrendChart : Canvas
{
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(System.Collections.ObjectModel.ObservableCollection<SeriesPoint>),
            typeof(SalesTrendChart), new PropertyMetadata(null, OnPointsChanged));

    public System.Collections.ObjectModel.ObservableCollection<SeriesPoint>? Points
    {
        get => (System.Collections.ObjectModel.ObservableCollection<SeriesPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private System.Collections.ObjectModel.ObservableCollection<SeriesPoint>? _subscribed;

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SalesTrendChart chart) return;
        if (chart._subscribed is not null)
        {
            chart._subscribed.CollectionChanged -= chart.OnCollectionChanged;
        }
        chart._subscribed = e.NewValue as System.Collections.ObjectModel.ObservableCollection<SeriesPoint>;
        if (chart._subscribed is not null)
        {
            chart._subscribed.CollectionChanged += chart.OnCollectionChanged;
        }
        chart.Rebuild();
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();

    public SalesTrendChart()
    {
        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var points = Points;
        if (points is null || points.Count == 0 || ActualWidth < 40 || ActualHeight < 30)
        {
            DrawEmpty();
            return;
        }

        var w = ActualWidth;
        var h = ActualHeight;
        var padL = 4.0;
        var padR = 4.0;
        var padT = 8.0;
        var padB = 18.0;
        var max = points.Max(p => p.Value);
        if (max <= 0) max = 1;
        var stepX = points.Count == 1 ? 0 : (w - padL - padR) / (points.Count - 1);

        // المسار المساحي (خط + إغلاق للأسفل)
        var linePts = new System.Collections.Generic.List<Point>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var x = padL + (points.Count == 1 ? (w - padL - padR) / 2 : stepX * i);
            var y = padT + (h - padT - padB) * (1 - (double)(points[i].Value / max));
            linePts.Add(new Point(x, y));
        }

        var areaGeometry = new StreamGeometry();
        using (var ctx = areaGeometry.Open())
        {
            ctx.BeginFigure(linePts[0], false);
            for (var i = 1; i < linePts.Count; i++)
            {
                ctx.LineTo(linePts[i]);
            }
            ctx.LineTo(new Point(linePts[^1].X, h - padB));
            ctx.LineTo(new Point(linePts[0].X, h - padB));
        }
        var area = new Path
        {
            Data = areaGeometry,
            Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, 0x2E, 0x5B, 0x9E)),
        };
        SetLeft(area, 0);
        SetTop(area, 0);
        Children.Add(area);

        var line = new Polyline
        {
            StrokeLineJoin = PenLineJoin.Round,
            StrokeThickness = 2.2,
            Stroke = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2E, 0x5B, 0x9E)),
        };
        foreach (var p in linePts)
        {
            line.Points.Add(p);
        }
        SetLeft(line, 0);
        SetTop(line, 0);
        Children.Add(line);

        // نقطة النهاية الأخيرة (الأحدث) بلون ذهبي دلالي
        var last = linePts[^1];
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xB9, 0x95, 0x5C)),
        };
        SetLeft(dot, last.X - 3.5);
        SetTop(dot, last.Y - 3.5);
        Children.Add(dot);

        // محور سيني: أول/وسط/آخر تسمية تاريخ
        AddAxisLabel(points[0].Label, padL, w);
        if (points.Count > 2)
        {
            var mid = points[points.Count / 2].Label;
            var midX = padL + stepX * (points.Count / 2);
            AddAxisLabel(mid, midX, w);
        }
        AddAxisLabel(points[^1].Label, points.Count == 1 ? (w - padL - padR) / 2 : padL + stepX * (points.Count - 1), w, alignEnd: true);
    }

    private void AddAxisLabel(string text, double x, double totalWidth, bool alignEnd = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D)),
        };
        label.Loaded += (_, _) =>
        {
            var width = label.ActualWidth;
            SetLeft(label, alignEnd ? x - width : Math.Clamp(x - width / 2, 0, Math.Max(0, totalWidth - width)));
        };
        SetTop(label, ActualHeight - 16);
        Children.Add(label);
    }

    private void DrawEmpty()
    {
        var empty = new TextBlock
        {
            Text = "لا مبيعات في هذه الفترة بعد",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D)),
        };
        SetLeft(empty, 0);
        SetTop(empty, ActualHeight / 2 - 10);
        empty.Width = ActualWidth;
        empty.TextAlignment = TextAlignment.Center;
        Children.Add(empty);
    }
}
