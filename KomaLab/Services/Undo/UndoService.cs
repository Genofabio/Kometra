using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.Services.Undo;

public partial class UndoService : ObservableObject, IUndoService
{
    // CONFIGURAZIONE
    private const int MaxHistoryCount = 10;

    // STRUTTURE DATI OTTIMIZZATE
    // LinkedList ci permette di fare .RemoveFirst() in O(1) quando superiamo il limite.
    private readonly LinkedList<IUndoableAction> _undoList = new();
    
    // Stack va bene per il Redo perché svuotiamo tutto appena si fa una nuova azione.
    private readonly Stack<IUndoableAction> _redoStack = new();

    public bool CanUndo => _undoList.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void RecordAction(IUndoableAction action)
    {
        // 1. Aggiungiamo la nuova azione in coda (Cima dello stack virtuale)
        _undoList.AddLast(action);

        // 2. OTTIMIZZAZIONE MEMORIA: Limite storico
        if (_undoList.Count > MaxHistoryCount)
        {
            // Rimuoviamo l'azione più vecchia (testa della lista)
            var oldestNode = _undoList.First;
            if (oldestNode != null)
            {
                _undoList.RemoveFirst();
                
                // CRITICO: Liberiamo le risorse dell'azione persa per sempre
                oldestNode.Value.Dispose();
            }
        }

        // 3. Pulizia Redo (Il futuro alternativo è perso)
        ClearRedoStack();

        NotifyChanges();
    }

    public void Undo()
    {
        if (_undoList.Count == 0) return;

        // Prendiamo l'ultima azione inserita
        var actionNode = _undoList.Last;
        if (actionNode == null) return;
        
        var action = actionNode.Value;

        // Eseguiamo l'undo
        action.Undo();

        // Spostiamo da UndoList -> RedoStack
        _undoList.RemoveLast();
        _redoStack.Push(action);
        
        NotifyChanges();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        // Prendiamo l'azione dallo stack Redo
        var action = _redoStack.Pop();

        // Rieseguiamo l'azione
        action.Execute();

        // Spostiamo da RedoStack -> UndoList
        _undoList.AddLast(action);
        
        NotifyChanges();
    }

    public void ClearHistory()
    {
        // Dispose di tutte le azioni pendenti per liberare memoria FITS
        foreach (var action in _undoList) action.Dispose();
        _undoList.Clear();

        ClearRedoStack();
        
        NotifyChanges();
    }

    private void ClearRedoStack()
    {
        if (_redoStack.Count > 0)
        {
            // Dispose delle azioni nel limbo del Redo che stiamo cancellando
            foreach (var action in _redoStack)
            {
                action.Dispose();
            }
            _redoStack.Clear();
        }
    }

    private void NotifyChanges()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
}