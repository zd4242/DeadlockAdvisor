using System.Globalization;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls.Art;

/// <summary>
/// Where art-drawing controls find the art: an inherited attached property set once on the
/// window, plus a revision that re-renders every one of them after View → Reload Art.
/// </summary>
public sealed class ArtHost
{
    private ArtHost()
    {
    }

    public static readonly AttachedProperty<IArtService?> ServiceProperty =
        AvaloniaProperty.RegisterAttached<ArtHost, Visual, IArtService?>("Service", inherits: true);

    public static readonly AttachedProperty<int> RevisionProperty =
        AvaloniaProperty.RegisterAttached<ArtHost, Visual, int>("Revision", inherits: true);

    public static IArtService? GetService(Visual visual) => visual.GetValue(ServiceProperty);
    public static void SetService(Visual visual, IArtService? value) => visual.SetValue(ServiceProperty, value);
    public static int GetRevision(Visual visual) => visual.GetValue(RevisionProperty);
    public static void SetRevision(Visual visual, int value) => visual.SetValue(RevisionProperty, value);
}

/// <summary>Draws a hero portrait or item icon, or its placeholder tile, rounded off.</summary>
public static class ArtPainter
{
    private static readonly Typeface _bold = new("Segoe UI", FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush _placeholderText = new SolidColorBrush(Palette.Bg);

    /// <summary>The corner radius art of this size gets.</summary>
    public static double DefaultRadius(ArtKind kind, double size) =>
        Math.Max(3, Math.Round(size * (kind == ArtKind.Hero ? 0.18 : 0.22), MidpointRounding.ToEven));

    /// <summary>Physical pixels per logical pixel at <paramref name="visual"/>: the screen's scaling times the app's zoom.</summary>
    public static double RenderScale(Visual visual)
    {
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel is null)
            return 1;
        var zoom = visual.TransformToVisual(topLevel)?.M11 ?? 1;
        return topLevel.RenderScaling * Math.Abs(zoom);
    }

    public static void Draw(DrawingContext context, Visual owner, ArtKind kind, string id, string name, Rect rect,
        double? radius = null, Color? tint = null, double opacity = 1)
    {
        var corner = radius ?? DefaultRadius(kind, rect.Width);
        var shape = new RoundedRect(rect, corner);
        var bitmap = ArtHost.GetService(owner)?.Get(kind, id, (int)Math.Ceiling(rect.Width * RenderScale(owner)));

        using (context.PushOpacity(opacity))
        {
            if (bitmap is not null)
            {
                using (context.PushClip(shape))
                    context.DrawImage(bitmap, rect);
                return;
            }

            var color = tint ?? ArtPlaceholder.ColorFor(id);
            context.DrawRectangle(new SolidColorBrush(color), null, shape);
            var initials = ArtPlaceholder.Initials(string.IsNullOrEmpty(name) ? id : name);
            var fontSize = Math.Max(8, Math.Round(rect.Width * (initials.Length < 2 ? 0.44 : 0.36), MidpointRounding.ToEven));
            var text = new FormattedText(initials, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _bold, fontSize, _placeholderText);
            context.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
        }
    }
}
