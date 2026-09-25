namespace DeadlockAdvisor.Services.Contracts;

public interface IFilePickerService
{
    /// <summary>A folder the user picked, as a local path, or null if they cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startIn = null);
}
