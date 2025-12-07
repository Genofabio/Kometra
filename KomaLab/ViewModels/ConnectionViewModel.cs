using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media; // Solo per i colori base

namespace KomaLab.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    // Dati puri, nessuna geometria qui
    public BaseNodeViewModel Source { get; }
    public BaseNodeViewModel Target { get; }

    [ObservableProperty]
    private bool _isHighlighted;

    // Colori di base (potresti anche spostarli nella View, ma qui va bene)
    public Color DefaultColor { get; } = Color.Parse("#99666666");
    public Color HighlightColor { get; } = Color.Parse("#FF8058E8");

    public ConnectionViewModel(BaseNodeViewModel source, BaseNodeViewModel target)
    {
        Source = source;
        Target = target;

        // Ascoltiamo la selezione per aggiornare lo stato (non la geometria!)
        Source.PropertyChanged += (s, e) => CheckSelection();
        Target.PropertyChanged += (s, e) => CheckSelection();
    }

    private void CheckSelection()
    {
        IsHighlighted = Source.IsSelected || Target.IsSelected;
    }
}