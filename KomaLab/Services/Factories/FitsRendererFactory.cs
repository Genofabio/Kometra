using System;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Rendering;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.Services.Factories;

public class FitsRendererFactory : IFitsRendererFactory
{
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImagePresentationService _presentationService;
    private readonly IFitsMetadataService _metadataService;

    public FitsRendererFactory(
        IFitsOpenCvConverter converter,
        IImagePresentationService presentationService,
        IFitsMetadataService metadataService)
    {
        _converter = converter;
        _presentationService = presentationService;
        _metadataService = metadataService;
    }

    public async Task<FitsRenderer> CreateAsync(Array pixelData, FitsHeader header)
    {
        // 1. Estrazione metadati (sincrona)
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
        
        // 2. Istanziazione (veloce, sincrona)
        var renderer = new FitsRenderer(
            pixelData, 
            bScale, 
            bZero, 
            _converter, 
            _presentationService
        );

        // 3. Inizializzazione pesante (asincrona)
        // Qui garantiamo che l'oggetto sia "idratato" prima di restituirlo
        await renderer.InitializeAsync();

        return renderer;
    }
}