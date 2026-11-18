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
}
