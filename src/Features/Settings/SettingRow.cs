using Avalonia.Automation;

namespace DeadlockAdvisor.Features.Settings;

/// <summary>One setting on a settings card: its name and what it does on the left, its control (the content) on the right.</summary>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    private Control? _named;

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty || change.Property == TitleProperty || change.Property == DescriptionProperty)
            NameContent();
    }

    /// <summary>
    /// A lone control, such as a toggle or a drop-down, is read out as the row's title and description,
    /// unless it has a name of its own. A group of controls is left for their own names.
    /// </summary>
    private void NameContent()
    {
        if (_named is not null && !ReferenceEquals(_named, Content))
        {
            _named.ClearValue(AutomationProperties.NameProperty);
            _named.ClearValue(AutomationProperties.HelpTextProperty);
            _named = null;
        }
        if (Content is not Control control || control is Panel || Title is not { Length: > 0 } title)
            return;
        if (_named is null && control.IsSet(AutomationProperties.NameProperty))
            return;

        _named = control;
        control.SetValue(AutomationProperties.NameProperty, title);
        control.SetValue(AutomationProperties.HelpTextProperty, Description);
    }
}
