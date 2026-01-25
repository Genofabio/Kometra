using System;

namespace KomaLab.ViewModels.Visualization;

public class EnhancementImageViewport : ImageViewport
{
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
}