using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenCvSharp;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Processing.Engines;

public interface IStackingEngine
{
    // Per Somma/Media: Inizializza e Accumula (già ottimo per memoria)
    void InitializeAccumulators(int width, int height, out Mat accumulator, out Mat countMap);
    void AccumulateFrame(Mat accumulator, Mat countMap, Mat currentFrame);
    void FinalizeAverage(Mat accumulator, Mat countMap);

    // Per Mediana: Nuova firma "Chunked"
    // Invece di una lista di Mat, prende una lista di "Riferimenti" e una funzione per caricarne un pezzo
    Task<Mat> ComputeMedianChunkedAsync<TSource>(
        IEnumerable<TSource> sources, 
        int width, 
        int height,
        Func<TSource, Rect, Task<Mat>> regionLoader);
}