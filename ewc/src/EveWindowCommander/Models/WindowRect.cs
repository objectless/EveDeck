namespace EveWindowCommander.Models;

public sealed class WindowRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public WindowRect Clone() => new() { X = X, Y = Y, Width = Width, Height = Height };

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
