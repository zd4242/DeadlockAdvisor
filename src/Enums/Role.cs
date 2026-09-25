namespace DeadlockAdvisor.Enums;

public enum Role
{
    None,
    Ally,
    Enemy,
    Self
}

public static class Roles
{
    /// <summary>The spelling a saved match uses.</summary>
    public static string Key(this Role role) => role switch
    {
        Role.None => "none",
        Role.Ally => "ally",
        Role.Enemy => "enemy",
        Role.Self => "self",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public static string Label(this Role role) => role switch
    {
        Role.None => "Unassigned",
        Role.Ally => "Ally",
        Role.Enemy => "Enemy",
        Role.Self => "You",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public static bool TryParse(string? key, out Role role)
    {
        switch (key)
        {
            case "none":
                role = Role.None;
                return true;
            case "ally":
                role = Role.Ally;
                return true;
            case "enemy":
                role = Role.Enemy;
                return true;
            case "self":
                role = Role.Self;
                return true;
            default:
                role = default;
                return false;
        }
    }
}
