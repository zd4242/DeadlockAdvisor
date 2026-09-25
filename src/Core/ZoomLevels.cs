namespace DeadlockAdvisor.Core;

public static class ZoomLevels
{
    public static readonly IReadOnlyList<double> Steps = [0.75, 0.85, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0];
    public const int DefaultIndex = 2;

    public static int Clamp(int index) => Math.Clamp(index, 0, Steps.Count - 1);
}
