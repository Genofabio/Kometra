using Avalonia.Controls;
using Avalonia.Interactivity;
using Kometra.ViewModels.Settings;

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
            vm.RequestClose += Close;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.RequestClose -= Close;
        }
    }
}