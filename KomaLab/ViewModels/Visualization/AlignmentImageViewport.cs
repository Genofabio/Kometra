using System;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels.Visualization;

// ---------------------------------------------------------------------------
// FILE: AlignmentImageViewport.cs
// RUOLO: Viewport Specializzato
// DESCRIZIONE:
// Estende il viewport generico aggiungendo la logica di proiezione per gli
// elementi visuali specifici dell'allineamento (Target Marker e Search Box).
// Si occupa di mappare le coordinate immagine (FITS) in coordinate schermo (Canvas)
// tenendo conto dello Zoom e del Pan correnti.
// ---------------------------------------------------------------------------

public partial class AlignmentImageViewport : ImageViewport
{
    // --- Stato Interno ---

    [ObservableProperty] 
    private Point? _targetCoordinate; 

    [ObservableProperty] 
    private double _searchRadius; 
    
    // --- Override per Zoom Iniziale ---
    
    /// <summary>
    /// Reimposta la vista adattando l'immagine al viewport.
    /// A differenza della classe base (che usa il 100%), qui lasciamo un margine (90%).
    /// </summary>
    public override void ResetView()
    {
        // Controlli di sicurezza standard
        if (ImageSize.Width <= 0 || ImageSize.Height <= 0 || 
            ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            base.ResetView();
            return;
        }

        // --- DIFFERENZA PRINCIPALE ---
        // Usiamo 0.90 invece di 1.0 per lasciare un po' di "respiro" attorno all'immagine.
        double paddingFactor = 0.95; 
        
        double scaleX = (ViewportSize.Width * paddingFactor) / ImageSize.Width;
        double scaleY = (ViewportSize.Height * paddingFactor) / ImageSize.Height;

        // "Uniform": scegliamo la scala minore per far stare tutto dentro
        Scale = Math.Min(scaleX, scaleY);

        // Centriamo l'immagine
        OffsetX = (ViewportSize.Width - (ImageSize.Width * Scale)) / 2.0;
        OffsetY = (ViewportSize.Height - (ImageSize.Height * Scale)) / 2.0;
    }

    // --- Proiezioni Schermo (Binding Read-Only) ---

    public double TargetMarkerScreenX => TargetCoordinate.HasValue 
        ? (TargetCoordinate.Value.X * Scale) + OffsetX 
        : 0;

    public double TargetMarkerScreenY => TargetCoordinate.HasValue 
        ? (TargetCoordinate.Value.Y * Scale) + OffsetY 
        : 0;

    public double SearchBoxLeft
    {
        get
        {
            if (!TargetCoordinate.HasValue) return 0;
            double screenCenterX = (TargetCoordinate.Value.X * Scale) + OffsetX;
            return screenCenterX - (SearchRadius * Scale);
        }
    }

    public double SearchBoxTop
    {
        get
        {
            if (!TargetCoordinate.HasValue) return 0;
            double screenCenterY = (TargetCoordinate.Value.Y * Scale) + OffsetY;
            return screenCenterY - (SearchRadius * Scale);
        }
    }

    public double SearchBoxWidth => (SearchRadius * 2) * Scale;
    
    public double SearchBoxHeight => (SearchRadius * 2) * Scale;

    // --- Gestione Cambiamenti ---

    partial void OnTargetCoordinateChanged(Point? value) => NotifyCalculatedProps();
    
    partial void OnSearchRadiusChanged(double value) => NotifyCalculatedProps();

    /// <summary>
    /// Notifica la View che le coordinate schermo sono cambiate.
    /// Questo metodo viene invocato sia quando cambiano i dati del modello (Target/Radius)
    /// sia quando cambia la vista (Zoom/Pan dal base ImageViewport).
    /// </summary>
    protected override void NotifyCalculatedProps()
    {
        // Chiamiamo la base se necessario (dipende dall'implementazione di ImageViewport),
        // ma qui aggiorniamo specificamente le proprietà derivate di questa classe.
        base.NotifyCalculatedProps();

        OnPropertyChanged(nameof(TargetMarkerScreenX));
        OnPropertyChanged(nameof(TargetMarkerScreenY));
        
        OnPropertyChanged(nameof(SearchBoxLeft));
        OnPropertyChanged(nameof(SearchBoxTop));
        OnPropertyChanged(nameof(SearchBoxWidth));
        OnPropertyChanged(nameof(SearchBoxHeight));
    }
}