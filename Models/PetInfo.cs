namespace TokenPet.Models;

public record PetInfo
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string SpritesheetPath { get; init; } = "";
    public string Directory { get; init; } = "";
}
