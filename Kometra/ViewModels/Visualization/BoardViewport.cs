using System;
using System.Collections.Generic;
using System.Linq;
using Kometra.ViewModels.Nodes;

namespace Kometra.ViewModels.Visualization;

/// <summary>
/// Viewport specializzato per la visualizzazione del grafo dei nodi.
/// Gestisce la logica di puntamento della telecamera globale.
/// </summary>
public class BoardViewport : BaseViewport
{
    // Applichiamo i limiti specifici solo per la Board
    protected override double MinZoomLimit => 0.05; // 5% per vedere tutto il grafo
    protected override double MaxZoomLimit => 2.0;  // 200% per non sgranare troppo i nodi
    protected override double ZoomStep => 1.15;    // Zoom leggermente più dolce sulla board

    public BoardViewport()
    {
        Scale = 0.4; 
        OffsetX = 0;
        OffsetY = 0;
    }

    /// <summary>
    /// Ripristina la vista allo stato predefinito (Zoom 100% e origine centrata).
    /// </summary>
    public void ResetView()
    {
        Scale = 0.4;
        OffsetX = 0;
        OffsetY = 0;
    }

    /// <summary>
    /// Calcola Zoom e Pan in modo che tutti i nodi presenti siano contenuti 
    /// nell'area visibile della finestra.
    /// </summary>
    public void ZoomToFit(IEnumerable<BaseNodeViewModel> nodes)
    {
        var nodeList = nodes.ToList();
        
        // Se non ci sono nodi o la finestra è ridotta a zero, non facciamo nulla
        if (nodeList.Count == 0 || ViewportSize.Width <= 0 || ViewportSize.Height <= 0) return;

        // 1. Calcolo Bounding Box del contenuto (area occupata dai nodi)
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var node in nodeList)
        {
            if (node.X < minX) minX = node.X;
            if (node.Y < minY) minY = node.Y;
            
            // Usiamo la dimensione stimata del nodo per includere tutto il rettangolo del controllo
            double right = node.X + node.EstimatedTotalSize.Width;
            double bottom = node.Y + node.EstimatedTotalSize.Height;
            
            if (right > maxX) maxX = right;
            if (bottom > maxY) maxY = bottom;
        }

        double contentW = maxX - minX;
        double contentH = maxY - minY;
        
        // 2. Aggiunta Padding (margine di sicurezza dai bordi della finestra)
        double padding = 60;
        
        // 3. Calcolo della Scala ottimale per far stare il contenuto nel viewport
        double scaleX = ViewportSize.Width / (contentW + padding * 2);
        double scaleY = ViewportSize.Height / (contentH + padding * 2);
        
        // Scegliamo la scala minore per non tagliare nulla e applichiamo dei limiti
        Scale = Math.Min(scaleX, scaleY);
        Scale = Math.Clamp(Scale, MinZoomLimit, 1.0); // Evitiamo uno zoom eccessivo (>100%) se c'è un solo nodo

        // 4. Centratura (Pan)
        // Calcoliamo l'offset necessario per spostare il centro del Bounding Box 
        // nel centro esatto della finestra
        OffsetX = (ViewportSize.Width - (contentW * Scale)) / 2.0 - (minX * Scale);
        OffsetY = (ViewportSize.Height - (contentH * Scale)) / 2.0 - (minY * Scale);
    }
}