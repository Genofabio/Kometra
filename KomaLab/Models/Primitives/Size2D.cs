namespace KomaLab.Models.Primitives;

public readonly struct Size2D
{
    public double Width { get; }
    public double Height { get; }

    public Size2D(double width, double height)
    {
        Width = width;
        Height = height;
    }

    // Deconstruct permette di fare: var (w, h) = mySize;
    public void Deconstruct(out double width, out double height)
    {
        width = Width;
        height = Height;
    }

    public override string ToString() => $"{Width:F0}x{Height:F0}";
}