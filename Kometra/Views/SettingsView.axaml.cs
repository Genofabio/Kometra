using Avalonia.Controls;
using Avalonia.Interactivity;
using Kometra.ViewModels;

namespace Kometra.Views;

public partial class SettingsView : Window
{
    public SettingsView()
    {
        InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            // Sottoscrizione all'evento di chiusura richiesto dal ViewModel
            vm.RequestClose += Close;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (DataContext is SettingsViewModel vm)
        {
            // Pulizia del riferimento per evitare memory leak
            vm.RequestClose -= Close;
        }
    }
}