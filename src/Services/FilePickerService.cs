using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

public class FilePickerService : IFilePickerService
{
    public async Task<string?> PickFolderAsync(string title, string? startIn = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
        if (topLevel == null)
            return null;

        var start = startIn is null ? null : await topLevel.StorageProvider.TryGetFolderFromPathAsync(startIn);
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
