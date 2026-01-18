using System;
using KomaLab.Models.Fits;
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

    public FitsRenderer Create(Array pixelData, FitsHeader header)
    {
        // 1. Estrazione parametri (Responsabilità della Factory preparare i dati)
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

        // 2. Creazione del Renderer con le nuove dipendenze snellite
        return new FitsRenderer(
            pixelData, 
            bScale, 
            bZero, 
            _converter, 
            _presentationService);
    }
}