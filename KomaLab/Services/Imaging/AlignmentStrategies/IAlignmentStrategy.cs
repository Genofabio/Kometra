using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Primitives; // Usa le primitive pure (Point2D)

namespace KomaLab.Services.Imaging.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: IAlignmentStrategy.cs
// DESCRIZIONE:
// Interfaccia Strategy Pattern per gli algoritmi di allineamento.
// Ogni implementazione gestisce una logica specifica (Stelle, Comete, ROI Manuale).
// ---------------------------------------------------------------------------

public interface IAlignmentStrategy
{
    /// <summary>
    /// Esegue il calcolo dei centroidi di allineamento.
    /// </summary>
    /// <param name="sourcePaths">Lista dei percorsi dei file FITS.</param>
    /// <param name="guesses">Coordinate suggerite (WCS o input utente). Può contenere null.</param>
    /// <param name="searchRadius">Raggio di ricerca in pixel (per le strategie ROI/Pattern).</param>
    /// <param name="progress">Oggetto per il report del progresso alla UI.</param>
    /// <returns>Array di punti calcolati (null se il calcolo fallisce per un frame).</returns>
    Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths,
        List<Point2D?> guesses,
        int searchRadius,
        IProgress<(int Index, Point2D? Center)>? progress);
}