using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Export;
using Kometra.Models.Fits;
using Kometra.Models.Visualization;

namespace Kometra.Services.ImportExport;

public interface IVideoExportCoordinator
{
    Task ExportVideoAsync(
        IEnumerable<FitsFileReference> sourceFiles, 
        VideoExportSettings settings,
        AbsoluteContrastProfile initialProfile,
        IProgress<double>? progress = null, // Supporto per la UI
        CancellationToken token = default);
}