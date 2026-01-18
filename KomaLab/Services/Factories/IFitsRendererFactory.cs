using System;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.Services.Factories;

public interface IFitsRendererFactory
{
    // Cambia da FitsRenderer a Task<FitsRenderer>
    Task<FitsRenderer> CreateAsync(Array pixelData, FitsHeader header);
}