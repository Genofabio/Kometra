using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: IAlignmentService.cs
// RUOLO: Orchestratore del Workflow (High-Level)
// DESCRIZIONE:
// Gestisce il ciclo di vita del processo di allineamento su liste di file.
// Non esegue calcoli matematici diretti sui pixel, ma coordina:
// 1. La scelta della Strategia (Stelle vs Comete).
// 2. L'esecuzione asincrona su più file (concorrenza e progressi).
// 3. L'applicazione finale dei risultati (I/O su disco).
// Agisce come ponte tra la UI (ViewModel) e i servizi di basso livello.
// ---------------------------------------------------------------------------

public interface IAlignmentService
{
    /// <summary>
    /// Calcola i centroidi di allineamento per una sequenza di immagini.
    /// Supporta allineamento Stellare (FFT) e Cometario (Pattern Matching).
    /// </summary>
    /// <param name="target">Tipo di target (Stelle o Cometa).</param>
    /// <param name="mode">Modalità (Auto, Guidata, Manuale).</param>
    /// <param name="method">Algoritmo di centering (Gaussian, Centroid, Peak).</param>
    /// <param name="sourcePaths">Percorsi file.</param>
    /// <param name="currentCoordinates">Coordinate note/hint (WCS o input utente).</param>
    /// <param name="searchRadius">Raggio di ricerca (ROI) in pixel.</param>
    /// <param name="progress">Reporter opzionale.</param>
    Task<IEnumerable<Point2D?>> CalculateCentersAsync(
        AlignmentTarget target, 
        AlignmentMode mode,
        CenteringMethod method,
        List<string> sourcePaths,
        IEnumerable<Point2D?> currentCoordinates,
        int searchRadius,
        IProgress<(int Index, Point2D? Center)>? progress = null);

    /// <summary>
    /// Verifica se ci sono abbastanza dati per avviare il calcolo.
    /// </summary>
    bool CanCalculate(AlignmentTarget target, AlignmentMode mode, IEnumerable<Point2D?> coords, int totalCount);
    
    /// <summary>
    /// Applica la traslazione finale, centra le immagini su un canvas comune (Union/Intersection)
    /// e salva i risultati temporanei su disco.
    /// </summary>
    Task<List<string>> ApplyCenteringAndSaveAsync(
        List<string> sourcePaths, 
        List<Point2D?> centers,
        string tempFolderPath,
        AlignmentTarget target);
}