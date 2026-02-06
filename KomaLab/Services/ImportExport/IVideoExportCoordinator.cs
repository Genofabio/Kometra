using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Export;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;

namespace KomaLab.Services.ImportExport;

public interface IVideoExportCoordinator
{
    Task ExportVideoAsync(
        IEnumerable<FitsFileReference> sourceFiles, 
        VideoExportSettings settings,
        AbsoluteContrastProfile initialProfile,
        IProgress<double>? progress = null, // Supporto per la UI
        CancellationToken token = default);
}