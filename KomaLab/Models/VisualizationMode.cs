namespace KomaLab.Models;

public enum VisualizationMode
{
    Linear,         // (x - black) / (white - black)
    Logarithmic,    // Log((x - black) ...)
    SquareRoot,     // Sqrt((x - black) ...)
}