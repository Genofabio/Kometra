using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading; // Necessario per il Dispatcher
using KomaLab.ViewModels;
using System;

namespace KomaLab.Views;

public partial class HeaderEditorView : Window
{
    public bool IsSaved { get; private set; } = false;

    public HeaderEditorView()
    {
        InitializeComponent();
    }

    // Usiamo OnDataContextChanged per agganciare gli eventi del ViewModel
    // Questo è più sicuro rispetto al costruttore perché il DataContext potrebbe cambiare.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is HeaderEditorViewModel vm)
        {
            // Rimuoviamo per sicurezza (evita doppi agganci se il context cambia)
            vm.RequestScrollToSelection -= OnRequestScroll;
            
            // Agganciamo l'evento specifico definito nel ViewModel
            vm.RequestScrollToSelection += OnRequestScroll;
        }
    }

    private void OnRequestScroll()
    {
        // Dispatcher.UIThread.Post è FONDAMENTALE.
        // Mette l'azione in coda alla fine delle operazioni UI correnti.
        // Questo dà tempo alla DataGrid di creare visivamente la nuova riga prima di provare a scorrerla.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is HeaderEditorViewModel vm && vm.SelectedItem != null)
            {
                var grid = this.FindControl<DataGrid>("TheDataGrid");
                grid?.ScrollIntoView(vm.SelectedItem, null);
            }
        });
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        IsSaved = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsSaved = false;
        Close();
    }
}