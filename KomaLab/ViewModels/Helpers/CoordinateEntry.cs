using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels.Helpers;

public partial class CoordinateEntry : ObservableObject
{
    public int Index { get; set; }
    public string? DisplayName { get; set; } = "";
    public int DisplayIndex => Index + 1;
        
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinateString))] 
    private Point? _coordinate;

    // Proprietà helper per il formatting
    public string CoordinateString => Coordinate.HasValue
        ? $"({Coordinate.Value.X:F2}; {Coordinate.Value.Y:F2})"
        : "---";
}