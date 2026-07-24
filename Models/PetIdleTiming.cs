namespace TokenPet;

public static class PetIdleTiming
{
    public const double DefaultSittingDurationSeconds = 15.0;
    public const double DefaultSleepingDurationSeconds = 100.0;
    public const double MinimumDurationSeconds = 1.0;
    public const double MaximumDurationSeconds = 3600.0;

    public static bool ShouldReturnToIdle(
        PetState state,
        double elapsedSeconds,
        double sittingDurationSeconds,
        double sleepingDurationSeconds)
    {
        var duration = state switch
        {
            PetState.Sitting => NormalizeDuration(sittingDurationSeconds, DefaultSittingDurationSeconds),
            PetState.Sleeping => NormalizeDuration(sleepingDurationSeconds, DefaultSleepingDurationSeconds),
            _ => double.PositiveInfinity
        };
        return elapsedSeconds >= duration;
    }

    public static double NormalizeDuration(double value, double fallback)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumDurationSeconds, MaximumDurationSeconds)
            : fallback;
    }
}
