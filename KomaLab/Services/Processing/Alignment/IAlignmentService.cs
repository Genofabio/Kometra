using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Alignment;

public interface IAlignmentService
{
    /// <summary>
    /// Fase Scientifica: Calcola i centroidi raffinati basandosi sui file e sui punti suggeriti (guesses).
    /// </summary>
    Task<IEnumerable<Point2D?>> CalculateCentersAsync(
        AlignmentTarget target, 
        AlignmentMode mode,
        CenteringMethod method,
        List<FitsFileReference> files,
        IEnumerable<Point2D?> guesses,
        int searchRadius,
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default);

    /// <summary>
    /// Fase Geometrica: Calcola le dimensioni del canvas finale e gli offset di correzione.
    /// </summary>
    Task<AlignmentMap> GenerateMapAsync(
        List<FitsFileReference> files, 
        List<Point2D?> centers, 
        AlignmentTarget target);
    
    /// <summary>
    /// Restituisce la funzione di processing pronta per essere eseguita dal BatchService.
    /// Il Service configura internamente la logica (Stelle vs Comete) e l'uso dell'Engine Geometrico.
    /// </summary>
    Action<Mat, Mat, FitsHeader, int> GetWarpingProcessor(
        AlignmentMap map, 
        Func<double, double, string>? historyGenerator = null) ;
}