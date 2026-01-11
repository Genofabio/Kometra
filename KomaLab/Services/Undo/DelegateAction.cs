// ---------------------------------------------------------------------------
// FILE: DelegateAction.cs
// DESCRIZIONE:
// Implementazione generica del pattern Command. 
// Permette di incapsulare logica di Undo/Redo al volo tramite delegati.
// 
// NOTA ENTERPRISE: 
// Ideale per azioni semplici (cambio proprietà, spostamento). 
// Per operazioni pesanti (es. Stacking), meglio creare classi dedicate 
// che implementano IUndoableAction per gestire meglio lo stato della memoria.
// ---------------------------------------------------------------------------

using System;
using KomaLab.Services.Undo;

public class DelegateAction : IUndoableAction
{
    private readonly Action _execute;
    private readonly Action _undo;
    private readonly Action? _onDispose;
    private bool _isDisposed;

    public string Name { get; }

    public DelegateAction(string name, Action execute, Action undo, Action? onDispose = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        _onDispose = onDispose;
    }

    public void Execute() 
    {
        ThrowIfDisposed();
        _execute();
    }

    public void Undo() 
    {
        ThrowIfDisposed();
        _undo();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        // Esegue la pulizia specifica (es. cancellazione file temporanei FITS 
        // associati a questo specifico step della cronologia)
        _onDispose?.Invoke();
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed) 
            throw new ObjectDisposedException(Name, "L'azione è stata rimossa dalla cronologia e le sue risorse sono state liberate.");
    }
}