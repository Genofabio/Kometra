using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using nom.tam.fits;

namespace KomaLab.Services.Data;

public interface IFitsService
{
    Task<FitsImageData?> LoadFitsFromFileAsync(string path);
    
    Task<(int Width, int Height)> GetFitsImageSizeAsync(string path);
    
    Task SaveFitsFileAsync(FitsImageData data, string destinationPath);
    
    // NormalizeData era già corretto, usa OpenCvSharp.Mat
    void NormalizeData(OpenCvSharp.Mat sourceMat, int width, int height, double black, double white, IntPtr dest, long stride, VisualizationMode mode);

    // --- MODIFICA QUI ---
    // Aggiungi 'VisualizationMode mode' con il valore di default per mantenere la compatibilità
    Task ExportVideoAsync(
        List<string> sourceFiles, 
        string outputPath, 
        double fps, 
        ContrastProfile profile, 
        VisualizationMode mode = VisualizationMode.Linear);
        
    Task<List<string>> SortFilesByObservationTimeAsync(List<string> filePaths);
    
    Task<Header?> ReadHeaderOnlyAsync(string path);
}