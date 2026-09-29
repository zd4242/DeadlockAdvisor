using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// A key shown as a button: click it, then press the new key (with any of Ctrl, Alt and Shift) to hand
/// it to <see cref="RecordedCommand"/>. Delete or Backspace hands over null, taking the key away; Esc,
/// a second click or clicking elsewhere keeps the old one. Whether the key is allowed is the command's call.
/// </summary>
public class ShortcutRecorder : Button
{
    public static readonly StyledProperty<KeyGesture?> GestureProperty =
        AvaloniaProperty.Register<ShortcutRecorder, KeyGesture?>(nameof(Gesture));

    public static readonly StyledProperty<ICommand?> RecordedCommandProperty =
        AvaloniaProperty.Register<ShortcutRecorder, ICommand?>(nameof(RecordedCommand));

    public static readonly DirectProperty<ShortcutRecorder, bool> IsRecordingProperty =
        AvaloniaProperty.RegisterDirect<ShortcutRecorder, bool>(nameof(IsRecording),
            recorder => recorder.IsRecording, (recorder, value) => recorder.IsRecording = value);

    private readonly List<(InputElement Element, List<KeyBinding> Bindings)> _heldBindings = [];
    private bool _isRecording;

    static ShortcutRecorder()
    {
        GestureProperty.Changed.AddClassHandler<ShortcutRecorder>((recorder, _) => recorder.ShowGesture());
    }

    public ShortcutRecorder()
    {
        ShowGesture();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public KeyGesture? Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <summary>Run with the key pressed, or null for Delete or Backspace.</summary>
    public ICommand? RecordedCommand
    {
        get => GetValue(RecordedCommandProperty);
        set => SetValue(RecordedCommandProperty, value);
    }

    /// <summary>Waiting for a key, which then goes to <see cref="RecordedCommand"/> rather than to the window's shortcuts.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (value == _isRecording)
                return;
            HoldKeyBindings(value);
            SetAndRaise(IsRecordingProperty, ref _isRecording, value);
            PseudoClasses.Set(":recording", value);
            ShowGesture();
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        IsRecording = !IsRecording;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        if (IsModifier(e.Key))
            return;
        IsRecording = false;
        var alone = e.KeyModifiers == KeyModifiers.None;
        if (alone && e.Key == Key.Escape)
            return;
        RecordedCommand?.Execute(alone && e.Key is Key.Delete or Key.Back ? null : new KeyGesture(e.Key, e.KeyModifiers));
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        IsRecording = false;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        IsRecording = false;
    }

    /// <summary>
    /// Key bindings act before the key is routed to the focused control, so while recording, the
    /// window's are set aside: pressing Ctrl+Q to try it mustn't quit.
    /// </summary>
    private void HoldKeyBindings(bool hold)
    {
        if (hold)
        {
            foreach (var element in this.GetVisualAncestors().OfType<InputElement>().Where(element => element.KeyBindings.Count > 0))
            {
                _heldBindings.Add((element, element.KeyBindings.ToList()));
                element.KeyBindings.Clear();
            }
            return;
        }

        foreach (var (element, bindings) in _heldBindings)
            element.KeyBindings.AddRange(bindings);
        _heldBindings.Clear();
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void ShowGesture() =>
        Content = IsRecording ? "Press a key…" : Gesture is null ? "None" : ShortcutKeys.Label(Gesture);
}
