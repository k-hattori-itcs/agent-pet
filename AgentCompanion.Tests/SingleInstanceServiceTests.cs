using AgentCompanion.Services;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public async Task NotifyPrimary_ForwardsSettingsIntent()
    {
        using var primary = SingleInstanceService.Acquire();
        using var secondary = SingleInstanceService.Acquire();
        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);

        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.Listen(
            () => result.TrySetResult(false),
            () => result.TrySetResult(true));

        secondary.NotifyPrimary(showSettings: true);

        Assert.True(await result.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
