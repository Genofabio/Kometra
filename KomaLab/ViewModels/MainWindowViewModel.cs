using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels;

public class MainWindowViewModel : ObservableObject
{

    public BoardViewModel BoardVm { get; }
    
    public MainWindowViewModel(BoardViewModel boardVm)
    {
        BoardVm = boardVm;
    }
}