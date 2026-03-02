using System;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kometra.ViewModels.Visualization;

/// <summary>
/// Estende il Viewport standard per gestire i marker di allineamento (mirino e box).
/// Include la logica di reset con margine di sicurezza.
/// </summary>
public partial class AlignmentImageViewport : ImageViewport
{
    [ObservableProperty] 
    private Point? _targetCoordinate;

    [ObservableProperty] 
    private int _searchRadius = 100;

    // =======================================================================
    // PROPRIETÀ DI VISIBILITÀ (Binding XAML)
    // =======================================================================
    
    public bool IsTargetMarkerVisible => TargetCoordinate.HasValue;
    
    public bool IsSearchBoxVisible => TargetCoordinate.HasValue && SearchRadius > 0;

    // =======================================================================
    // COORDINATE SCHERMO (Proiezione Matematica Immagine -> UI)
    // =======================================================================
    
    // Posizione del mirino: (CoordImmagine * Scala) + Offset
    public double TargetMarkerScreenX => TargetCoordinate.HasValue ? (TargetCoordinate.Value.X * Scale) + OffsetX : 0;
    public double TargetMarkerScreenY => TargetCoordinate.HasValue ? (TargetCoordinate.Value.Y * Scale) + OffsetY : 0;

    // Coordinate del Box di ricerca (centrato sul mirino e scalato con lo zoom)
    public double SearchBoxLeft => TargetMarkerScreenX - (SearchRadius * Scale);
    public double SearchBoxTop => TargetMarkerScreenY - (SearchRadius * Scale);
    public double SearchBoxWidth => (SearchRadius * 2) * Scale;
    public double SearchBoxHeight => (SearchRadius * 2) * Scale;

    // =======================================================================
    // LOGICA DI RESET VISTA (Con Margine di Sicurezza)
    // =======================================================================

    /// <summary>
    /// Adatta l'immagine alla finestra aggiungendo un margine del 10% 
    /// per evitare che tocchi i bordi della viewport.
    /// </summary>
    public override void ResetView()
    {
        // Se non abbiamo dimensioni valide, non possiamo calcolare nulla
        if (ImageSize.Width <= 0 || ImageSize.Height <= 0 || 
            ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
            return;

        // 1. Calcola lo spazio occupabile (Scale Fit)
        double scaleX = ViewportSize.Width / ImageSize.Width;
        double scaleY = ViewportSize.Height / ImageSize.Height;
    
        // 2. Prendi il minimo (per far stare tutto dentro) e applica un margine del 10%
        // Usiamo 0.9 per lasciare il 5% di spazio vuoto su ogni lato
        Scale = Math.Min(scaleX, scaleY) * 0.96;

        // 3. Centra perfettamente l'immagine nello spazio rimanente
        OffsetX = (ViewportSize.Width - (ImageSize.Width * Scale)) / 2;
        OffsetY = (ViewportSize.Height - (ImageSize.Height * Scale)) / 2;
    }

    // =======================================================================
    // NOTIFICA DEI CAMBIAMENTI (Ricalcolo Marker)
    // =======================================================================

    /// <summary>
    /// Intercettiamo i cambiamenti per aggiornare i marker sullo schermo.
    /// Fondamentale per far "seguire" il mirino all'immagine durante Zoom e Pan.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Se cambia uno di questi fattori, la proiezione sullo schermo deve cambiare
        if (e.PropertyName == nameof(Scale) || 
            e.PropertyName == nameof(OffsetX) || 
            e.PropertyName == nameof(OffsetY) || 
            e.PropertyName == nameof(TargetCoordinate) || 
            e.PropertyName == nameof(SearchRadius))
        {
            // Notifichiamo la UI che le coordinate calcolate sono cambiate
            OnPropertyChanged(nameof(TargetMarkerScreenX));
            OnPropertyChanged(nameof(TargetMarkerScreenY));
            OnPropertyChanged(nameof(IsTargetMarkerVisible));
            OnPropertyChanged(nameof(IsSearchBoxVisible));
            OnPropertyChanged(nameof(SearchBoxLeft));
            OnPropertyChanged(nameof(SearchBoxTop));
            OnPropertyChanged(nameof(SearchBoxWidth));
            OnPropertyChanged(nameof(SearchBoxHeight));
        }
    }
}