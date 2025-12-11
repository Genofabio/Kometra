namespace KomaLab.Models;

public record ContrastProfile(double Val1, double Val2, bool IsAbsolute)
{
    public double KBlack => IsAbsolute ? 0 : Val1;
    public double KWhite => IsAbsolute ? 0 : Val2;
    public double Black => IsAbsolute ? Val1 : 0;
    public double White => IsAbsolute ? Val2 : 0;
}