namespace DeadlockAdvisor.Services.Contracts;

public enum AttentionKind
{
    /// <summary>Something finished and needs nothing from you.</summary>
    Done,

    /// <summary>Something is waiting for you to look at it.</summary>
    NeedsLook,
}

/// <summary>Calls for attention while another app, such as the game, has the screen.</summary>
public interface IAttentionService
{
    /// <summary>A short system sound for <paramref name="kind"/>.</summary>
    void Chime(AttentionKind kind);

    /// <summary>Flash the window's taskbar button until it is switched to.</summary>
    void FlashWindow();
}
