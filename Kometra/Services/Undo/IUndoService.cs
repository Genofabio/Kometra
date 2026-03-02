using System.Collections.Generic;

namespace Kometra.Services.Undo;

// ---------------------------------------------------------------------------
// FILE: IUndoService.cs
// RUOLO: Gestore della Cronologia Operazioni
// DESCRIZIONE:
// Definisce il contratto per il monitoraggio e l'esecuzione delle azioni reversibili.
// Essenziale per mantenere l'integrità dei dati FITS durante le modifiche.
// ---------------------------------------------------------------------------

public interface IUndoService
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    
    /// <summary>
    /// Registra una nuova azione nella cronologia. 
    /// Nota: L'azione deve essere già stata eseguita o verrà eseguita subito dopo.
    /// </summary>
    void RecordAction(IUndoableAction action);
    
    /// <summary>
    /// Annulla l'ultima azione registrata.
    /// </summary>
    void Undo();
    
    /// <summary>
    /// Ripristina l'ultima azione annullata.
    /// </summary>
    void Redo();

    /// <summary>
    /// Svuota la cronologia e invoca Dispose su tutte le azioni per liberare RAM/File.
    /// </summary>
    void ClearHistory();
}