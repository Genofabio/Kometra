using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;

namespace KomaLab.Services.Data;

public interface IFitsService
{
    Task<FitsImageData?> LoadFitsFromFileAsync(string path);
    Task<(int Width, int Height)> GetFitsImageSizeAsync(string path);
    Task SaveFitsFileAsync(FitsImageData data, string destinationPath);
    void NormalizeData(OpenCvSharp.Mat sourceMat, int width, int height, double black, double white, IntPtr dest, long stride);
    Task ExportVideoAsync(List<string> sourceFiles, string outputPath, double fps, ContrastProfile profile);
    Task<List<string>> SortFilesByObservationTimeAsync(List<string> filePaths);
}