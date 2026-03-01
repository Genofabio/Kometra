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

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        
        var type = Type.GetType(name);

        // MODIFICA QUI: Aggiungi il controllo "IsAssignableFrom"
        // Questo impedisce di istanziare Modelli o oggetti che non sono View.
        if (type != null && typeof(Control).IsAssignableFrom(type))
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