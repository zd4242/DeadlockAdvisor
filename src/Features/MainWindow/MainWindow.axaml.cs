using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.ItemCard;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Features.MainWindow;

public partial class MainWindow : Window
{
    private static readonly Geometry _maximizeGlyph = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    // Restore: a window with another peeking out behind it.
    private static readonly Geometry _restoreGlyph = Geometry.Parse("M0.5,2.5 H7.5 V9.5 H0.5 Z M2.5,2.5 V0.5 H9.5 V7.5 H7.5");

    private readonly ISettingsService? _settings;
    private ModalWindow? _modalWindow;
    private IDisposable? _modalBoundsSync;
    private IDisposable? _viewActions;
    private PixelPoint _normalPosition;
    private Size _normalSize;

    public MainWindow()
    {
        InitializeComponent();

#if DEBUG
        this.AttachDevTools();
#endif

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        TitleBar.LayoutUpdated += (_, _) => PlaceTitle();
        ItemCardHover.SetPresenter(this, ItemCards);
    }

    public MainWindow(IModalService modalService, ISettingsService settings, IArtService art, IDataService data) : this()
    {
        _settings = settings;
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
            _viewActions = vm.ViewInteraction.Subscribe(action =>
            {
                if (action == MainWindowViewModel.CloseAction)
                    Close();
                else if (action == MainWindowViewModel.ArtChangedAction)
                    ArtHost.SetRevision(this, ArtHost.GetRevision(this) + 1);
            });
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
            var maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Data = maximized ? _restoreGlyph : _maximizeGlyph;
            ToolTip.SetTip(MaximizeButton, maximized ? "Restore Down" : "Maximize");
        }
        else if (change.Property == IsActiveProperty)
        {
            TitleBar.Classes.Set("inactive", !IsActive);
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
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        (DataContext as MainWindowViewModel)?.OnClosing();
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

    /// <summary>The bare strip, the divider and the title drag the window; menus, tabs and buttons handle their own clicks.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }
        BeginMoveDrag(e);
    }

    /// <summary>Hide the title rather than let it slide under the tabs or the buttons on a narrow window.</summary>
    private void PlaceTitle()
    {
        var titleBounds = WindowTitle.Bounds;
        var roomLeft = PageTabs.Bounds.Right + 16;
        var roomRight = CaptionButtons.Bounds.Left - 16;
        var fits = titleBounds.Left >= roomLeft && titleBounds.Right <= roomRight;
        WindowTitle.Opacity = fits ? 1 : 0;
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    // -- modals -------------------------------------------------------------------

    private void ShowModalWindow(ViewModelBase content)
    {
        var modalVm = new ModalViewModel();
        _modalWindow = new ModalWindow { DataContext = modalVm };

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
