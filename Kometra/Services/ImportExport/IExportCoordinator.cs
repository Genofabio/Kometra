using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Export;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;

namespace Kometra.Services.ImportExport;

public interface IExportCoordinator
{
    Task ExecuteExportAsync(
        IEnumerable<ExportableItem> items,
        ExportJobSettings settings,
        IProgress<BatchProgressReport> progress,
        CancellationToken token);
}