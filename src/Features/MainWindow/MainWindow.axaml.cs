using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeadlockAdvisor.Behaviors;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.ItemCard;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Features.MainWindow;

public partial class MainWindow : Window
{
    private static readonly Geometry _maximizeGlyph = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    // Restore: a window with another peeking out behind it.
    private static readonly Geometry _restoreGlyph = Geometry.Parse("M0.5,2.5 H7.5 V9.5 H0.5 Z M2.5,2.5 V0.5 H9.5 V7.5 H7.5");

    private readonly ISettingsService? _settings;
    private readonly IForegroundService? _foreground;
    private readonly List<KeyBinding> _shortcutBindings = [];
    private ModalWindow? _modalWindow;
    private ViewModelBase? _waitingModal;
    private IDisposable? _modalBoundsSync;
    private IDisposable? _viewActions;
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private WindowState _stateBeforeMinimize = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

#if DEBUG
        this.AttachDevTools();
#endif

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        AutoScroll = new MiddleClickAutoScroll(this);
        Closed += (_, _) => AutoScroll.Dispose();
        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        ResizeGrip.PointerPressed += OnResizeGripPressed;
        PointerPressed += OnDismissLayerPressed;
        PointerPressed += OnNavigationButtonPressed;
        TitleBar.LayoutUpdated += (_, _) => PlaceTitle();
        ItemCardHover.SetPresenter(this, ItemCards);
    }

    /// <summary>Middle-click scrolling for every scroll viewer in the window.</summary>
    public MiddleClickAutoScroll AutoScroll { get; }

    public MainWindow(IModalService modalService, ISettingsService settings, IArtService art, IDataService data, IForegroundService foreground)
        : this()
    {
        _settings = settings;
        _foreground = foreground;
        modalService.ShowModalObservable.Subscribe(ShowModalWindow);
        modalService.CloseModalObservable.Subscribe(_ => CloseModalWindow());

        ArtHost.SetService(this, art);
        ItemCards.CardFactory = itemId =>
            data.Store.Items.TryGetValue(itemId, out var item) ? ItemCardBuilder.Build(data.Store, item) : null;

        RestoreGeometry(settings.Current.WindowGeometry);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewActions?.Dispose();
        if (DataContext is MainWindowViewModel vm)
        {
            _viewActions = new CompositeDisposable(
                vm.ViewInteraction.Subscribe(action =>
                {
                    if (action == MainWindowViewModel.CloseAction)
                        Close();
                    else if (action == MainWindowViewModel.ArtChangedAction)
                        ArtHost.SetRevision(this, ArtHost.GetRevision(this) + 1);
                    else if (action == MainWindowViewModel.BringForwardAction)
                        BringForward();
                }),
                vm.WhenAnyValue(v => v.IsSettingsOpen).Where(open => open).Subscribe(_ => SettingsPage.FocusCategories()),
                vm.WhenAnyValue(v => v.ShortcutBindings).Subscribe(ApplyShortcuts));
        }
    }

    /// <summary>Swap the rebindable keys in beside the fixed ones from XAML.</summary>
    private void ApplyShortcuts(IReadOnlyList<ShortcutBinding> shortcuts)
    {
        foreach (var binding in _shortcutBindings)
            KeyBindings.Remove(binding);
        _shortcutBindings.Clear();
        foreach (var shortcut in shortcuts)
        {
            var binding = new KeyBinding { Gesture = shortcut.Gesture, Command = shortcut.Command, CommandParameter = shortcut.Parameter };
            _shortcutBindings.Add(binding);
            KeyBindings.Add(binding);
        }
    }

    // -- window state -----------------------------------------------------------

    private void RestoreGeometry(WindowGeometry? geometry)
    {
        if (geometry is null || geometry.Width < MinWidth || geometry.Height < MinHeight)
            return;
        var onScreen = Screens.All.Any(screen => screen.WorkingArea.Contains(new PixelPoint(geometry.X + 40, geometry.Y + 10)));
        if (!onScreen)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(geometry.X, geometry.Y);
        Width = geometry.Width;
        Height = geometry.Height;
        if (geometry.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // With the client area extended over the chrome, the native window reports a larger client
        // size while it's being shown, before Avalonia copies it to Width/Height; keep them in step.
        if (change.Property == ClientSizeProperty)
        {
            var clientSize = change.GetNewValue<Size>();
            if (Width != clientSize.Width)
                SetCurrentValue(WidthProperty, clientSize.Width);
            if (Height != clientSize.Height)
                SetCurrentValue(HeightProperty, clientSize.Height);
            if (WindowState == WindowState.Normal)
                _normalSize = clientSize;
        }
        else if (change.Property == WindowStateProperty)
        {
            if (change.GetNewValue<WindowState>() == WindowState.Minimized)
                _stateBeforeMinimize = change.GetOldValue<WindowState>();
            var maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Data = maximized ? _restoreGlyph : _maximizeGlyph;
            ToolTip.SetTip(MaximizeButton, maximized ? "Restore Down" : "Maximize");
            var floating = WindowState == WindowState.Normal;
            WindowOutline.IsVisible = floating;
            ResizeGrip.IsVisible = floating;
        }
        else if (change.Property == IsActiveProperty)
        {
            TitleBar.Classes.Set("inactive", !IsActive);
            if (IsActive)
                OpenWaitingModal();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _normalPosition = Position;
        _normalSize = ClientSize;
        PositionChanged += (_, args) =>
        {
            if (WindowState == WindowState.Normal)
                _normalPosition = args.Point;
        };
        (DataContext as MainWindowViewModel)?.OnOpened();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        var vm = DataContext as MainWindowViewModel;
        if (e.CloseReason != WindowCloseReason.OSShutdown && vm?.HoldCloseForJobs() == true)
        {
            e.Cancel = true;
            return;
        }
        vm?.OnClosing();
        _settings?.Update(s => s.WindowGeometry = new WindowGeometry
        {
            X = _normalPosition.X,
            Y = _normalPosition.Y,
            Width = _normalSize.Width,
            Height = _normalSize.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        });
    }

    // -- keyboard and wheel -------------------------------------------------------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Tab / Ctrl+Shift+Tab cycle the pages.
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainWindowViewModel vm)
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? vm.PreviousPageCommand : vm.NextPageCommand;
            command.Execute().Subscribe();
            e.Handled = true;
        }
    }

    /// <summary>The mouse's back and forward buttons step through the pages. Settings takes the back button first, to close itself.</summary>
    private void OnNavigationButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed)
            vm.BackCommand.Execute().Subscribe();
        else if (properties.IsXButton2Pressed)
            vm.ForwardCommand.Execute().Subscribe();
        else
            return;
        e.Handled = true;
    }

    /// <summary>Ctrl+wheel zooms anywhere in the window, even over a scroll area that would otherwise eat it.</summary>
    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not MainWindowViewModel vm)
            return;
        if (e.Delta.Y > 0)
            vm.ZoomInCommand.Execute().Subscribe();
        else if (e.Delta.Y < 0)
            vm.ZoomOutCommand.Execute().Subscribe();
        e.Handled = true;
    }

    // -- title bar ----------------------------------------------------------------

    /// <summary>Raised just before the title bar hands a press to the OS to drag the window.</summary>
    internal event EventHandler? WindowDragStarting;

    /// <summary>The bare strip, the divider and the title drag the window; menus, tabs and buttons handle their own clicks.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A press in a menu's dropdown bubbles up here from its popup. Dragging from it would start the
        // OS move loop, which swallows the release, so the menu item would never see its click.
        if (e.Source is Visual source && (source == TitleBar || TitleBar.IsVisualAncestorOf(source)))
            DragFromTitleBar(e);
    }

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    /// <summary>
    /// An open menu or popup lays a layer over the window that takes the next press to close it. On the
    /// title bar, that press drags the window as well, rather than needing a second one.
    /// </summary>
    private void OnDismissLayerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is LightDismissOverlayLayer && IsOverTitleBar(e, this))
            DragFromTitleBar(e);
    }

    /// <summary>The modal's dim covers the title bar too, but only the window's content is modal: the title bar still moves it.</summary>
    private void OnModalBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Visual backdrop && IsOverTitleBar(e, backdrop))
            DragFromTitleBar(e);
    }

    /// <summary>Whether a press on <paramref name="frame"/>, in this window or one laid over it, falls on the title bar.</summary>
    private bool IsOverTitleBar(PointerEventArgs e, Visual frame)
    {
        var point = TitleBar.PointToClient(frame.PointToScreen(e.GetPosition(frame)));
        return new Rect(TitleBar.Bounds.Size).Contains(point);
    }

    private void DragFromTitleBar(PointerPressedEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }
        WindowDragStarting?.Invoke(this, EventArgs.Empty);
        BeginMoveDrag(e);
    }

    /// <summary>Hide the title rather than let it slide under the tabs or the buttons on a narrow window.</summary>
    private void PlaceTitle()
    {
        var titleBounds = WindowTitle.Bounds;
        var roomLeft = (PageTabs.IsVisible ? (Control)PageTabs : MainMenu).Bounds.Right + 16;
        var roomRight = CaptionButtons.Bounds.Left - 16;
        var fits = titleBounds.Left >= roomLeft && titleBounds.Right <= roomRight;
        WindowTitle.Opacity = fits ? 1 : 0;
    }

    /// <summary>Up from the taskbar or from behind the game, with the modal on top if one is open.</summary>
    private void BringForward()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeMinimize;
        Activate();
        OpenWaitingModal();
        _modalWindow?.Activate();
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    // -- modals -------------------------------------------------------------------

    /// <summary>
    /// Opening a modal's window takes focus, so while another app such as the game is in front, the
    /// modal waits for you to come back to this window, or for something to bring it forward.
    /// </summary>
    private void ShowModalWindow(ViewModelBase content)
    {
        if (_foreground?.IsAnotherAppInFront == true)
            _waitingModal = content;
        else
            OpenModalWindow(content);
    }

    private void OpenWaitingModal()
    {
        if (_waitingModal is not { } content)
            return;
        _waitingModal = null;
        OpenModalWindow(content);
    }

    private void OpenModalWindow(ViewModelBase content)
    {
        var modalVm = new ModalViewModel();
        _modalWindow = new ModalWindow { DataContext = modalVm };
        _modalWindow.BackdropPressed += OnModalBackdropPressed;
        // The modal is its own visual root, so the art service isn't inherited into it.
        ArtHost.SetService(_modalWindow, ArtHost.GetService(this));

        SyncModalBounds();
        _modalBoundsSync = new CompositeDisposable(
            this.GetObservable(BoundsProperty).Select(_ => Unit.Default)
                .Merge(Observable.FromEventPattern<PixelPointEventArgs>(
                    h => PositionChanged += h, h => PositionChanged -= h)
                    .Select(_ => Unit.Default))
                .Subscribe(_ => SyncModalBounds()));

        _modalWindow.Show(this);

        // Set content after the window is shown so the enter animation plays correctly.
        Dispatcher.UIThread.Post(() =>
        {
            modalVm.Content = content;
            modalVm.IsVisible = true;
        });
    }

    private void CloseModalWindow()
    {
        _waitingModal = null;
        _modalBoundsSync?.Dispose();
        _modalBoundsSync = null;
        _modalWindow?.CloseIntentionally();
        _modalWindow = null;
    }

    private void SyncModalBounds()
    {
        if (_modalWindow is null)
            return;
        _modalWindow.Width = ClientSize.Width;
        _modalWindow.Height = ClientSize.Height;
        _modalWindow.Position = Position;
    }
}
