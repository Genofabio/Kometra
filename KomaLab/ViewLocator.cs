using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // Logica di sostituzione nome: BoardViewModel -> BoardView
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        
        // (Opzionale) Se le View sono in un namespace diverso (es. .Views), potresti dover aggiustare 'name' qui.
        // Ma di solito la struttura di default di Avalonia le mette in parallelo o gestisce i namespace in modo semplice.
        
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ObservableObject; 
    }
}