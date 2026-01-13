using System;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.Services.Factories;

public class FitsRendererFactory : IFitsRendererFactory
{
    // NOTA: IFitsIoService è stato rimosso perché FitsRenderer non lo usa più.
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IMediaExportService _mediaExport;

    public FitsRendererFactory(
        IFitsImageDataConverter converter,
        IImageAnalysisService analysis,
        IMediaExportService mediaExport)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _mediaExport = mediaExport ?? throw new ArgumentNullException(nameof(mediaExport));
    }

    public FitsRenderer Create(FitsImageData data)
    {
        // La firma del costruttore ora corrisponde a quella ottimizzata di FitsRenderer
        return new FitsRenderer(data, _converter, _analysis, _mediaExport);
    }
}