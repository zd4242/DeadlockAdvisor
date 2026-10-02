using Avalonia.Media;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Controls.Art;

/// <summary>A hero portrait or item icon at the control's size, or its placeholder tile when there's no art.</summary>
public class ArtImage : Control
{
    public static readonly StyledProperty<ArtKind> KindProperty =
        AvaloniaProperty.Register<ArtImage, ArtKind>(nameof(Kind), ArtKind.Item);

    public static readonly StyledProperty<string?> ArtIdProperty =
        AvaloniaProperty.Register<ArtImage, string?>(nameof(ArtId));

    public static readonly StyledProperty<string?> ArtNameProperty =
        AvaloniaProperty.Register<ArtImage, string?>(nameof(ArtName));

    /// <summary>The placeholder's colour for art that's missing, e.g. an item's shop colour. Null picks one per id.</summary>
    public static readonly StyledProperty<Color?> TintProperty =
        AvaloniaProperty.Register<ArtImage, Color?>(nameof(Tint));

    /// <summary>Null uses the default radius for the size.</summary>
    public static readonly StyledProperty<double?> RadiusProperty =
        AvaloniaProperty.Register<ArtImage, double?>(nameof(Radius));

    static ArtImage()
    {
        AffectsRender<ArtImage>(KindProperty, ArtIdProperty, ArtNameProperty, TintProperty, RadiusProperty,
            ArtHost.ServiceProperty, ArtHost.RevisionProperty);
    }

    public ArtKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? ArtId
    {
        get => GetValue(ArtIdProperty);
        set => SetValue(ArtIdProperty, value);
    }

    public string? ArtName
    {
        get => GetValue(ArtNameProperty);
        set => SetValue(ArtNameProperty, value);
    }

    public Color? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public double? Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(ArtId) || Bounds.Width <= 0)
            return;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        ArtPainter.Draw(context, this, Kind, ArtId, ArtName ?? ArtId, new Rect(0, 0, side, side), Radius, Tint);
    }
}
