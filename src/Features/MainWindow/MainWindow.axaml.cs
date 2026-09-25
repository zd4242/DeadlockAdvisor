using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Features.MainWindow;

public partial class MainWindow : Window
{
    private ModalWindow? _modalWindow;
    private IDisposable? _modalBoundsSync;

    public MainWindow()
    {
        InitializeComponent();

#if DEBUG
        this.AttachDevTools();
#endif
    }

    public MainWindow(IModalService modalService) : this()
    {
        modalService.ShowModalObservable.Subscribe(ShowModalWindow);
        modalService.CloseModalObservable.Subscribe(_ => CloseModalWindow());
    }

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
