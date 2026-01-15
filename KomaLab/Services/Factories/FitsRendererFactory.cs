using System;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.Services.Factories;

public class FitsRendererFactory : IFitsRendererFactory
{
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IMediaExportService _mediaExport;

    public FitsRendererFactory(
        IFitsOpenCvConverter converter,
        IImageAnalysisService analysis,
        IMediaExportService mediaExport)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _mediaExport = mediaExport ?? throw new ArgumentNullException(nameof(mediaExport));
    }

    public FitsRenderer Create(Array pixelData, FitsHeader header)
    {
        // Iniezione delle dipendenze + Dati
        return new FitsRenderer(pixelData, header, _converter, _analysis, _mediaExport);
    }
}