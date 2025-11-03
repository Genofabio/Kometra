using Avalonia;
using nom.tam.fits;

namespace KomaLab.Models;

public class FitsImageData
{
    public object RawData { get; set; } // Array T[][]
    public Header FitsHeader { get; set; }
    public Size ImageSize { get; set; }
    public double InitialBlackPoint { get; set; }
    public double InitialWhitePoint { get; set; }
}