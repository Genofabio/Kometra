using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
// <-- Importa l'interfaccia della Factory
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Services;

namespace KomaLab.ViewModels;

public partial class BoardViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly INodeViewModelFactory _nodeFactory; // <-- Nuova dipendenza

    // --- Proprietà (Stato della "Telecamera") ---
    [ObservableProperty]
    private double _offsetX;
    [ObservableProperty]
    private double _offsetY;
    [ObservableProperty]
    private double _scale = 0.5;
    [ObservableProperty]
    private Rect _viewBounds;

    // --- Proprietà (Stato della Scena) ---
    [ObservableProperty]
    private NodeViewModel? _selectedNode;
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();

    // --- Costruttore ---
    public BoardViewModel(INodeViewModelFactory nodeFactory) 
    {
        _nodeFactory = nodeFactory;
        
        // Avvia il caricamento dei nodi iniziali
        _ = LoadInitialNodesAsync();
    }

    /// <summary>
    /// Metodo helper per caricare i nodi di default all'avvio.
    /// </summary>
    private async Task LoadInitialNodesAsync()
    {
        // Usa il nuovo metodo AddNodeAsync che ora si affida alla factory
        await AddNodeAsync("avares://KomaLab/Assets/summed_clean.fits", 200, 100);
        await AddNodeAsync("avares://KomaLab/Assets/summed_clean.fits", 200, 300);
    }
    
    // --- Implementazione Comandi (generati da [RelayCommand]) ---

    [RelayCommand]
    private async Task AddNode()
    {
        // Calcola il centro del mondo visibile
        double screenCenterX = ViewBounds.Width / 2;
        double screenCenterY = ViewBounds.Height / 2;
        if (Scale == 0) return;
        
        double worldX = (screenCenterX - OffsetX) / Scale;
        double worldY = (screenCenterY - OffsetY) / Scale;
        
        string imagePath = "avares://KomaLab/Assets/summed_clean.fits";

        // Delega la creazione e aggiunta
        await AddNodeAsync(imagePath, worldX, worldY, centerOnPosition: true);
    }
    
    [RelayCommand(CanExecute = nameof(CanResetNormalization))]
    private async Task ResetNormalization()
    {
        if (SelectedNode != null)
        {
            await SelectedNode.ResetThresholds();
        }
    }
    private bool CanResetNormalization() => SelectedNode != null;

    [RelayCommand]
    private void IncrementOffset()
    {
        OffsetX += 20;
    }

    [RelayCommand]
    private void ResetBoard()
    {
        OffsetX = 0.0;
        OffsetY = 0.0;
        Scale = 0.5;
    }

    // --- Metodi Pubblici (Logica di Interazione) ---

    /// <summary>
    /// Logica per il Panning, chiamata dal code-behind.
    /// </summary>
    public void Pan(Vector delta)
    {
        OffsetX += delta.X;
        OffsetY += delta.Y;
    }

    /// <summary>
    /// Logica per lo Zoom, chiamata dal code-behind.
    /// </summary>
    public void Zoom(double deltaY, Point mousePosition)
    {
        double oldScale = Scale;
        double zoomFactor = deltaY > 0 ? 1.1 : 1 / 1.1;
        double newScale = Math.Clamp(oldScale * zoomFactor, 0.1, 10);

        // La logica matematica di "zoom-verso-il-cursore" vive ora qui
        OffsetX = mousePosition.X - (mousePosition.X - OffsetX) * (newScale / oldScale);
        OffsetY = mousePosition.Y - (mousePosition.Y - OffsetY) * (newScale / oldScale);
        Scale = newScale;
    }

    /// <summary>
    /// Logica per la Selezione/Deselezione.
    /// </summary>
    public void SetSelectedNode(NodeViewModel nodeToSelect)
    {
        if (SelectedNode == nodeToSelect) return;

        if (SelectedNode != null)
        {
            SelectedNode.IsSelected = false;
        }
        SelectedNode = nodeToSelect;
        if (SelectedNode != null)
        {
            SelectedNode.IsSelected = true;
        }
        
        // Notifica al comando di rivalutare il suo stato
        ResetNormalizationCommand.NotifyCanExecuteChanged();
    }
    
    public void DeselectAllNodes()
    {
        if (SelectedNode != null)
        {
            SelectedNode.IsSelected = false;
        }
        SelectedNode = null;
        
        ResetNormalizationCommand.NotifyCanExecuteChanged();
    }

    // --- Metodo "Factory" (ora DELEGA) ---

    /// <summary>
    /// Chiede alla factory di creare un nuovo nodo e lo aggiunge alla collezione.
    /// </summary>
    private async Task AddNodeAsync(string imagePath, double x, double y, bool centerOnPosition = false)
    {
        try
        {
            // --- MODIFICA CHIAVE ---
            // 1. Il VM non "crea" più, "chiede".
            var newNodeViewModel = await _nodeFactory.CreateNodeAsync(this, imagePath, x, y, centerOnPosition);
            
            // 2. Il VM aggiunge il risultato alla sua lista.
            Nodes.Add(newNodeViewModel);
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"ERRORE Aggiunta Nodo: {ex.Message}");
        }
    }
}