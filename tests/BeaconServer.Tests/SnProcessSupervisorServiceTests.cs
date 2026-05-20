using BeaconServer.Services;

namespace BeaconServer.Tests;

public sealed class SnProcessSupervisorServiceTests
{
    [Fact]
    public void BuildHostTravelUrlPinsCanonicalSaveSlot()
    {
        var url = SnProcessSupervisorService.BuildHostTravelUrl();

        Assert.Equal(
            "/Game/Maps/Awake?listen?slotname=savegame_0?SlotName=savegame_0?SaveSlot=savegame_0?LoadGame=savegame_0?SaveGame=savegame_0",
            url);
    }

    [Fact]
    public void BuildHostTravelOptionsEscapesCustomSaveSlot()
    {
        var options = SnProcessSupervisorService.BuildHostTravelOptions("save slot 1");

        Assert.Equal(
            "?listen?slotname=save%20slot%201?SlotName=save%20slot%201?SaveSlot=save%20slot%201?LoadGame=save%20slot%201?SaveGame=save%20slot%201",
            options);
    }
}
