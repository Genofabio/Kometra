using System;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Rendering;
using Kometra.ViewModels.Visualization;

namespace Kometra.Services.Factories;

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
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _presentationService = presentationService ?? throw new ArgumentNullException(nameof(presentationService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    }

    public async Task<FitsRenderer> CreateAsync(Array pixelData, FitsHeader header)
    {
        // 1. ANALISI SCIENTIFICA DEL BIT-DEPTH (Decision Making)
        // Leggiamo BITPIX per capire la natura del dato originale
        int bitpix = (int)_metadataService.GetDoubleValue(header, "BITPIX", 16);
        
        // REGOLA DI OTTIMIZZAZIONE:
        // - Se 8 o 16 bit (standard): usiamo Float32 (CV_32F). Risparmio 50% RAM, precisione perfetta.
        // - Se -32 (già float 32): manteniamo Float32.
        // - Se 32 (Interi lunghi) o -64 (Double): usiamo Float64 (CV_64F). 
        //   Evitiamo arrotondamenti della mantissa su numeri interi molto grandi.
        FitsBitDepth targetDepth = bitpix switch
        {
            32   => FitsBitDepth.Double, // Interi 32bit -> Double per evitare overflow/precisione
            -64  => FitsBitDepth.Double, 
            _    => FitsBitDepth.Float   // Default sicuro per 8, 16 e -32 bit
        };

        // 2. ESTRAZIONE METADATI DI SCALING
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
        
        // 3. ISTANZIAZIONE
        // Passiamo targetDepth al Renderer così saprà cosa chiedere al convertitore
        var renderer = new FitsRenderer(
            pixelData, 
            bScale, 
            bZero, 
            targetDepth, // <--- Nuovo parametro
            _converter, 
            _presentationService
        );

        // 4. INIZIALIZZAZIONE (Pesante)
        // Il renderer chiamerà il convertitore con il targetDepth scelto,
        // creerà la Matrice OpenCV e poi metterà a null pixelData (FREE RAM!).
        await renderer.InitializeAsync();

        return renderer;
    }
}