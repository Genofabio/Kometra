using System;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.ViewModels.Visualization;

namespace Kometra.Services.Factories;

public interface IFitsRendererFactory
{
    // Cambia da FitsRenderer a Task<FitsRenderer>
    Task<FitsRenderer> CreateAsync(Array pixelData, FitsHeader header);
}