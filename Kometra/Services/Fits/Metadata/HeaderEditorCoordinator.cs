using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;

namespace Kometra.Services.Fits.Metadata;

public class HeaderEditorCoordinator : IHeaderEditorCoordinator
{
    private readonly IFitsDataManager _dataManager;
    
    // Sandbox di Dominio: memorizziamo solo oggetti FitsHeader (Domain Models)
    private readonly Dictionary<FitsFileReference, FitsHeader> _sandbox = new();

    public bool HasChanges => _sandbox.Count > 0;

    public HeaderEditorCoordinator(IFitsDataManager dataManager)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
    }

    public async Task<FitsHeader?> GetHeaderAsync(FitsFileReference file)
    {
        if (file == null) return null;

        // 1. Se è nel sandbox, restituiamo la versione "sporca" (modificata)
        if (_sandbox.TryGetValue(file, out var bufferedHeader))
        {
            return bufferedHeader;
        }

        // 2. Altrimenti, prendiamo la versione corrente (già in RAM o da disco)
        return file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
    }

    public void SaveToBuffer(FitsFileReference file, FitsHeader header)
    {
        if (file == null || header == null) return;
        
        // Mettiamo l'header aggiornato nel limbo del sandbox
        _sandbox[file] = header;
    }

    public void CommitAll()
    {
        // Trasferiamo gli header dal sandbox al modello di dominio reale
        foreach (var kvp in _sandbox)
        {
            kvp.Key.ModifiedHeader = kvp.Value;
        }
        
        ClearSession();
    }

    public void ClearSession()
    {
        _sandbox.Clear();
    }
}