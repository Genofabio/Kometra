using Avalonia.Controls;

namespace KomaLab.Views;

public partial class PlateSolvingToolView : Window
{
    public PlateSolvingToolView()
    {
        InitializeComponent();
    }
    
    // Il metodo InitializeComponent() è gestito automaticamente dal compilatore 
    // nelle versioni recenti di Avalonia, ma tenerlo non guasta.

    // Se vuoi davvero che il log sia sempre a fuoco per permettere lo scroll con la tastiera:
    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Possiamo dare il focus al log o al pulsante principale
        var startBtn = this.Find<Button>("StartBtn"); // Se gli dai un Nome nello XAML
        startBtn?.Focus();
    }
}