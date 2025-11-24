using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;

namespace KomaLab.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{

    public BoardViewModel BoardVm { get; }
    
    public MainWindowViewModel(BoardViewModel boardVm)
    {
        BoardVm = boardVm;
    }
    
    [RelayCommand]
    private void ExitApp()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}