using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.Services.Undo;

// ---------------------------------------------------------------------------
// FILE: UndoService.cs
// RUOLO: Orchestratore dello Stato Reversibile
// DESCRIZIONE:
// Implementazione Enterprise del servizio Undo.
// Utilizza una LinkedList per la cronologia di Undo (permettendo la rimozione 
// efficiente degli elementi più vecchi) e uno Stack per il Redo.
//
// GESTIONE MEMORIA:
// Poiché le azioni FITS possono occupare centinaia di MB, il servizio impone
// un limite rigido (MaxHistoryCount). Al superamento, le azioni rimosse
// vengono disposate immediatamente.
// ---------------------------------------------------------------------------

public class UndoService : ObservableObject, IUndoService
{
    private const int MaxHistoryCount = 20; // Bilanciamento tra UX e RAM
    private readonly Lock _lock = new();

    private readonly LinkedList<IUndoableAction> _undoList = new();
    private readonly Stack<IUndoableAction> _redoStack = new();

    // Proprietà calcolate per il binding della UI
    public bool CanUndo => _undoList.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Registra un'azione e invalida il futuro alternativo (Redo Stack).
    /// </summary>
    public void RecordAction(IUndoableAction action)
    {

        lock (_lock)
        {
            // 1. Aggiungiamo l'azione alla fine della lista (punto più recente)
            _undoList.AddLast(action);

            // 2. Controllo saturazione cronologia
            if (_undoList.Count > MaxHistoryCount)
            {
                var oldest = _undoList.First;
                if (oldest != null)
                {
                    _undoList.RemoveFirst();
                    oldest.Value.Dispose(); // Libera risorse FITS/Mat/File
                }
            }

            // 3. Quando si compie una nuova azione, la catena del Redo si spezza
            ClearRedoStackInternal();
        }

        NotifyStatusChanged();
    }

    public void Undo()
    {
        lock (_lock)
        {
            if (_undoList.Count == 0) return;

            var actionNode = _undoList.Last;
            if (actionNode == null) return;

            var action = actionNode.Value;
            
            try
            {
                action.Undo();
                
                _undoList.RemoveLast();
                _redoStack.Push(action);
            }
            catch (Exception ex)
            {
                // In un sistema enterprise qui andrebbe un log serio
                System.Diagnostics.Debug.WriteLine($"[UndoService] Fallimento Undo: {ex.Message}");
            }
        }

        NotifyStatusChanged();
    }

    public void Redo()
    {
        lock (_lock)
        {
            if (_redoStack.Count == 0) return;

            var action = _redoStack.Pop();

            try
            {
                action.Execute();
                _undoList.AddLast(action);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UndoService] Fallimento Redo: {ex.Message}");
            }
        }

        NotifyStatusChanged();
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            foreach (var action in _undoList)
            {
                action.Dispose();
            }
            _undoList.Clear();

            ClearRedoStackInternal();
        }

        NotifyStatusChanged();
    }

    private void ClearRedoStackInternal()
    {
        // Metodo interno senza lock (da chiamare dentro un blocco lock)
        while (_redoStack.Count > 0)
        {
            var action = _redoStack.Pop();
            action.Dispose();
        }
    }

    private void NotifyStatusChanged()
    {
        // Notifica la UI che i comandi Undo/Redo devono essere ri-valutati
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
}