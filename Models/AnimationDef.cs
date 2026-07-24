namespace TokenPet.Models;

public record AnimationDef(string Name, int Row, int FrameCount, double Fps);

public static class AnimationDefs
{
    public static readonly IReadOnlyList<AnimationDef> All = new List<AnimationDef>
    {
        new("idle",      0, 6, 6.0),
        new("walk",      1, 8, 8.0),
        new("run_left",  2, 8, 8.0),
        new("wave",      3, 4, 4.0),
        new("jump",      4, 5, 5.0),
        new("fail",      5, 8, 8.0),
        new("sleep",     6, 6, 6.0),
        new("sprint",    7, 6, 6.0),
        new("sit",       8, 6, 6.0),
    };

    public const int FrameWidth = 192;
    public const int FrameHeight = 208;
    public const int RowCount = 9;

    public static AnimationDef? GetByName(string name) =>
        All.FirstOrDefault(a => a.Name == name);
}
