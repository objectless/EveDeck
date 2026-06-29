namespace EveWindowCommander.Models;

public sealed record DisplayModeOption(string Label, int Width, int Height)
{
    public override string ToString() => Label;
}
