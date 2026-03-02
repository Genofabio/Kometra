using System;

namespace Kometra.Services.Undo;

public interface IUndoableAction : IDisposable
{
    string Name { get; }
    
    bool IsExecuted { get; }

    void Execute(); // Esegue o Ripristina (Redo)
    void Undo();    // Annulla
}