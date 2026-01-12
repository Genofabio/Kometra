using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace KomaLab.Views;

public partial class PlateSolvingToolView : Window
{
    public PlateSolvingToolView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnBackgroundClicked(object? sender, PointerPressedEventArgs e)
    {
        // Questo metodo forza il focus sulla Finestra principale.
        // Se la TextBox aveva il focus (cursore attivo), lo perderà immediatamente.
        this.Focus();
    }
}