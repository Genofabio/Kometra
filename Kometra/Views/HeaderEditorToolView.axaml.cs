using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading; // Necessario per Dispatcher
using System;
using Kometra.ViewModels.Fits;

namespace Kometra.Views;

public partial class HeaderEditorToolView : Window
{
    // Utile se vuoi sapere dall'esterno come è stata chiusa la finestra (opzionale)
    public bool IsSaved { get; private set; } = false;

    public HeaderEditorToolView()
    {
        InitializeComponent();
    }

    // -----------------------------------------------------------------------
    // GESTIONE EVENTI VIEWMODEL (Scroll)
    // -----------------------------------------------------------------------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Best Practice: Rimuovi sempre l'handler vecchio prima di aggiungerne uno nuovo
        // per evitare memory leak o chiamate doppie se il DataContext cambiasse.
        if (DataContext is HeaderEditorToolViewModel oldVm)
        {
            oldVm.RequestScrollToSelection -= OnRequestScroll;
        }

        if (DataContext is HeaderEditorToolViewModel newVm)
        {
            newVm.RequestScrollToSelection += OnRequestScroll;
        }
    }

    private void OnRequestScroll()
    {
        // Dispatcher.UIThread.Post è FONDAMENTALE.
        // Mette l'azione in coda: aspetta che Avalonia abbia finito di renderizzare la nuova riga,
        // poi esegue lo scroll. Senza questo, proverebbe a scrollare verso una riga che "non esiste ancora" visivamente.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is HeaderEditorToolViewModel vm && vm.SelectedItem != null)
            {
                var grid = this.FindControl<DataGrid>("TheDataGrid");
                grid?.ScrollIntoView(vm.SelectedItem, null);
            }
        });
    }

    // -----------------------------------------------------------------------
    // GESTIONE PULSANTI (Salvataggio e Chiusura)
    // -----------------------------------------------------------------------

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // 1. FORZA IL FOCUS SULLA WINDOW
        // Questo è il trucco cruciale. Se l'utente ha appena digitato un numero nella DataGrid
        // e clicca "Applica" senza premere Invio, il valore è ancora "in volo".
        // Togliendo il focus dalla griglia, forziamo il Binding a scrivere il valore nel ViewModel.
        this.Focus();

        // 2. CHIAMA IL METODO DEL VIEWMODEL
        // Qui avviene la magia: le righe vengono trasformate in Header FITS e salvate nel FileReference.
        if (DataContext is HeaderEditorToolViewModel vm)
        {
            vm.ApplyChanges();
        }

        // 3. IMPOSTA STATO E CHIUDI
        IsSaved = true;
        Close(true); // Restituisce 'true' se la finestra è stata aperta con ShowDialog<bool>
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsSaved = false;
        Close(false); // Restituisce 'false'
    }
}