using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace Dictum.UI.Views;

/// <summary>The Dictum mark (docs/design/Main.dc.html): an Ink speech bubble holding three white level bars.</summary>
internal sealed class MarkIcon : Viewbox
{
    public MarkIcon(double size)
    {
        Width = Height = size;
        var ink = new SolidColorBrush(Color.Parse("#0B1220"));
        var canvas = new Canvas { Width = 64, Height = 64 };
        canvas.Children.Add(At(new Rectangle { Width = 56, Height = 52, RadiusX = 14, RadiusY = 14, Fill = ink }, 4, 4));
        canvas.Children.Add(new Path { Data = Geometry.Parse("M14,50 L14,63 L27,50 Z"), Fill = ink });
        canvas.Children.Add(At(Bar(16), 18, 24));
        canvas.Children.Add(At(Bar(34), 29, 15));
        canvas.Children.Add(At(Bar(22), 40, 21));
        Child = canvas;
    }

    private static Rectangle Bar(double height) => new() { Width = 6, Height = height, RadiusX = 3, RadiusY = 3, Fill = Brushes.White };

    private static Control At(Control control, double left, double top)
    {
        Canvas.SetLeft(control, left);
        Canvas.SetTop(control, top);
        return control;
    }
}
