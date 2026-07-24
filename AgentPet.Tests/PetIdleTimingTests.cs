using TokenPet;
using Xunit;

namespace AgentPet.Tests;

public sealed class PetIdleTimingTests
{
    [Theory]
    [InlineData(14.99, false)]
    [InlineData(15.0, true)]
    public void Sitting_UsesConfiguredDuration(double elapsedSeconds, bool expected)
    {
        Assert.Equal(expected, PetIdleTiming.ShouldReturnToIdle(
            PetState.Sitting,
            elapsedSeconds,
            sittingDurationSeconds: 15,
            sleepingDurationSeconds: 100));
    }

    [Theory]
    [InlineData(99.99, false)]
    [InlineData(100.0, true)]
    public void Sleeping_UsesConfiguredDuration(double elapsedSeconds, bool expected)
    {
        Assert.Equal(expected, PetIdleTiming.ShouldReturnToIdle(
            PetState.Sleeping,
            elapsedSeconds,
            sittingDurationSeconds: 15,
            sleepingDurationSeconds: 100));
    }

    [Fact]
    public void Sitting_CustomDurationChangesReturnTime()
    {
        Assert.False(PetIdleTiming.ShouldReturnToIdle(PetState.Sitting, 29.99, 30, 100));
        Assert.True(PetIdleTiming.ShouldReturnToIdle(PetState.Sitting, 30, 30, 100));
    }

    [Fact]
    public void Sleeping_CustomDurationChangesReturnTime()
    {
        Assert.False(PetIdleTiming.ShouldReturnToIdle(PetState.Sleeping, 199.99, 15, 200));
        Assert.True(PetIdleTiming.ShouldReturnToIdle(PetState.Sleeping, 200, 15, 200));
    }
}
