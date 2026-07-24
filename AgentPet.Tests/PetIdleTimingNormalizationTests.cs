using TokenPet;
using Xunit;

namespace AgentPet.Tests;

public sealed class PetIdleTimingNormalizationTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(999999, 3600)]
    public void NormalizeDuration_ClampsToSupportedRange(double value, double expected)
    {
        Assert.Equal(expected, PetIdleTiming.NormalizeDuration(value, 15));
    }
}
