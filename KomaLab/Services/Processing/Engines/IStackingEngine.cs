using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenCvSharp;
using KomaLab.Models.Processing; // Assicurati di avere questo using per StackingMode

namespace KomaLab.Services.Processing.Engines;

public interface IStackingEngine
{
    // Aggiungi anche questo metodo se vuoi esporre la modalità massiva (quella non-chunked)
    Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode);

    // --- MODIFICATO: Aggiunto bool useDoublePrecision ---
    void InitializeAccumulators(
        int width, 
        int height, 
        bool useDoublePrecision, // <--- NUOVO PARAMETRO
        out Mat accumulator, 
        out Mat countMap);

    void AccumulateFrame(Mat accumulator, Mat countMap, Mat currentFrame);
    
    void FinalizeAverage(Mat accumulator, Mat countMap);

    // Per Mediana Chunked (Invariato)
    Task<Mat> ComputeMedianChunkedAsync<TSource>(
        IEnumerable<TSource> sources, 
        int width, 
        int height,
        Func<TSource, Rect, Task<Mat>> regionLoader);
}