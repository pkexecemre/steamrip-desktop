using SteamRip.Core.Data;
using SteamRip.Core.Updates;

namespace SteamRip.Core.Tests;

public sealed class InstallRecoveryTests
{
    [Fact]
    public void A_stale_in_flight_row_keeps_an_installed_game_and_flags_a_missing_one()
    {
        Assert.Equal(InstallState.Installed, UpdateService.RecoveredState(hasGame: true));
        Assert.Equal(InstallState.NeedsAttention, UpdateService.RecoveredState(hasGame: false));
    }

    [Fact]
    public void Auto_updates_skip_rows_that_are_working_or_waiting_for_a_person()
    {
        Assert.False(UpdateService.AutoQueues(InstallState.Queued));
        Assert.False(UpdateService.AutoQueues(InstallState.Downloading));
        Assert.False(UpdateService.AutoQueues(InstallState.Installing));
        Assert.False(UpdateService.AutoQueues(InstallState.NeedsAttention));
        Assert.True(UpdateService.AutoQueues(InstallState.Installed));
        // A failed attempt may have failed for a reason that does not go away (a broken mirror, an
        // unwritable folder); re-queueing it hourly repeats a multi-gigabyte download, so it waits
        // for the Retry button the way NeedsAttention does.
        Assert.False(UpdateService.AutoQueues(InstallState.Failed));
    }
}
