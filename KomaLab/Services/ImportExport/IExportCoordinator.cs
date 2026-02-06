using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Export;
using KomaLab.Models.Processing;

namespace KomaLab.Services.ImportExport;

public interface IExportCoordinator
{
    Task ExecuteExportAsync(
        IEnumerable<ExportableItem> items,
        ExportJobSettings settings,
        IProgress<BatchProgressReport> progress,
        CancellationToken token);
}