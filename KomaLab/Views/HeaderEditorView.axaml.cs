using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KomaLab.ViewModels; // Assicurati di avere questo
using System.ComponentModel;

namespace KomaLab.Views;

public partial class HeaderEditorView : Window
{
    public bool IsSaved { get; private set; } = false;

    public HeaderEditorView()
    {
        InitializeComponent();

        // LOGICA SCROLL AUTOMATICO
        // Ci agganciamo al cambio di proprietà del ViewModel
        this.DataContextChanged += (s, e) =>
        {
            if (this.DataContext is HeaderEditorViewModel vm)
            {
                vm.PropertyChanged += ViewModel_PropertyChanged;
            }
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Se la proprietà cambiata è "SelectedItem" e non è nullo...
        if (e.PropertyName == nameof(HeaderEditorViewModel.SelectedItem))
        {
            var vm = sender as HeaderEditorViewModel;
            if (vm?.SelectedItem != null)
            {
                // ...diciamo al DataGrid di scorrere fino a quell'elemento
                var grid = this.FindControl<DataGrid>("TheDataGrid");
                grid?.ScrollIntoView(vm.SelectedItem, null);
            }
        }
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