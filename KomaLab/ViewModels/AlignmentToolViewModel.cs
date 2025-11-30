using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Services;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KomaLab.Models; // Qui trova AlignmentMode e AlignmentState
using KomaLab.ViewModels.Helpers;
using CoordinateEntry = KomaLab.ViewModels.Helpers.CoordinateEntry;
using Point = Avalonia.Point;
using Size = Avalonia.Size; 

namespace KomaLab.ViewModels;

public partial class AlignmentToolViewModel : ObservableObject
{
    #region Campi
    
    private readonly IFitsService _fitsService;
    private readonly IAlignmentService _alignmentService;
    
    // NUOVE DIPENDENZE
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    private readonly List<FitsImageData?> _sourceData; 
    private int _currentStackIndex;
    private readonly int _totalStackCount;
    
    private Size _viewportSize;
    
    private bool _hasLockedThresholds;
    private double _lockedKBlack; 
    private double _lockedKWhite;

    #endregion

    #region Proprietà e Stato

    public ViewportManager Viewport { get; } = new();
    public ObservableCollection<CoordinateEntry> CoordinateEntries { get; } = new();

    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            _viewportSize = value;
            Viewport.ViewportSize = value; 
        }
    }
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CorrectImageSize))]
    private FitsRenderer? _activeImage; 
    
    public Size CorrectImageSize => ActiveImage?.ImageSize ?? default;
    
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    
    public string ZoomStatusText => $"{Viewport.Scale:P0}";

    public bool IsTargetMarkerVisible => TargetCoordinate.HasValue;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
    private Point? _targetCoordinate;

    [ObservableProperty] private bool _isStack;
    [ObservableProperty] private string _stackCounterText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;
    
    public bool IsSearchRadiusVisible => SelectedMode != AlignmentMode.Automatic;

    [ObservableProperty] private int _minSearchRadius;
    [ObservableProperty] private int _maxSearchRadius = 100;
    [ObservableProperty] private int _searchRadius = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    [NotifyPropertyChangedFor(nameof(ProcessingStatusText))]
    [NotifyPropertyChangedFor(nameof(IsProcessingVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAlignmentCommand))]
    [NotifyPropertyChangedFor(nameof(IsCoordinateListVisible))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    private AlignmentState _currentState = AlignmentState.Initial;
    
    public List<FitsImageData>? FinalProcessedData { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsSearchBoxVisible => IsTargetMarkerVisible && CurrentState == AlignmentState.Initial;
    public bool IsSearchRadiusControlsVisible => IsSearchRadiusVisible && CurrentState == AlignmentState.Initial;
    public bool IsRefinementMessageVisible => IsSearchRadiusVisible && CurrentState != AlignmentState.Initial;
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating;
    
    public string ProcessingStatusText => CurrentState switch
    {
        AlignmentState.Calculating => "Calcolo centri in corso...",
        AlignmentState.Processing => "Allineamento in corso...",
        _ => "Elaborazione..."
    };

    public bool IsNavigationVisible
    {
        get
        {
            if (!IsStack) return false;
            if (CurrentState != AlignmentState.Initial) return true;
            return SelectedMode != AlignmentMode.Automatic;
        }
    }
    
    public bool IsCoordinateListVisible => CurrentState != AlignmentState.Initial;
    public IEnumerable<AlignmentMode> AvailableAlignmentModes { get; private set; }
    public TaskCompletionSource ImageLoadedTcs { get; } = new();

    #endregion

    #region Costruttore

    public AlignmentToolViewModel(
        List<FitsImageData?> sourceData,
        IFitsService fitsService,
        IAlignmentService alignmentService,
        IFitsDataConverter converter,      // <--- NUOVO
        IImageAnalysisService analysis)    // <--- NUOVO
    {
        _fitsService = fitsService;
        _alignmentService = alignmentService;
        _converter = converter;
        _analysis = analysis;
        
        _sourceData = sourceData;
        _currentStackIndex = 0;
        _totalStackCount = sourceData.Count;
        IsStack = _totalStackCount > 1;

        if (IsStack)
        {
            AvailableAlignmentModes = new[] { AlignmentMode.Automatic, AlignmentMode.Guided, AlignmentMode.Manual };
        }
        else
        {
            AvailableAlignmentModes = new[] { AlignmentMode.Automatic, AlignmentMode.Manual };
        }
        
        Viewport.SearchRadius = this.SearchRadius;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            CoordinateEntries.Clear();
            for (int i = 0; i < _totalStackCount; i++)
            {
                string? displayName = $"Immagine {i + 1}";
                double imageHeight = 0;
                if (_sourceData[i] != null)
                {
                    imageHeight = _sourceData[i]!.Height; 
                    try
                    {
                        displayName = _sourceData[i]?.FitsHeader.GetStringValue("OBJECT");
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = $"Immagine {i + 1}";
                    }
                    catch
                    {
                        // ignored
                    }
                }

                CoordinateEntries.Add(new CoordinateEntry
                {
                    Index = i,
                    DisplayName = displayName,
                    Coordinate = null,
                    ImageHeight = imageHeight
                });
            }

            await LoadStackImageAtIndexAsync(_currentStackIndex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN AlgnVM.InitializeAsync --- {ex}");
        }
        finally
        {
            ImageLoadedTcs.SetResult();
            CalculateCentersCommand.NotifyCanExecuteChanged();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            RefreshNavigationCommands();
        }
    }

   private async Task LoadStackImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _totalStackCount) return;

        _currentStackIndex = index;
        UpdateStackCounterText();

        ActiveImage?.UnloadData(); 

        try
        {
            var newModel = _sourceData[index];
            if (newModel == null) throw new Exception("Dati FITS nulli.");
            
            // CREAZIONE RENDERER CON NUOVI SERVIZI
            ActiveImage = new FitsRenderer(
                newModel, 
                _fitsService, 
                _converter, 
                _analysis
            );

            await ActiveImage.InitializeAsync();
            
            var (mean, sigma) = ActiveImage.GetImageStatistics();

            if (!_hasLockedThresholds)
            {
                double masterBlack = ActiveImage.BlackPoint;
                double masterWhite = ActiveImage.WhitePoint;
                
                _lockedKBlack = (masterBlack - mean) / sigma;
                _lockedKWhite = (masterWhite - mean) / sigma;
                _hasLockedThresholds = true;
                
                BlackPoint = masterBlack;
                WhitePoint = masterWhite;

                Viewport.ImageSize = ActiveImage.ImageSize;
                Viewport.ResetView(); 
                OnPropertyChanged(nameof(ZoomStatusText));
            }
            else
            {
                double adaptedBlack = mean + (_lockedKBlack * sigma);
                double adaptedWhite = mean + (_lockedKWhite * sigma);

                ActiveImage.BlackPoint = adaptedBlack;
                ActiveImage.WhitePoint = adaptedWhite;
                
                BlackPoint = adaptedBlack;
                WhitePoint = adaptedWhite;
            }
            
            OnPropertyChanged(nameof(CorrectImageSize)); 
            ResetThresholdsCommand.NotifyCanExecuteChanged();
            UpdateReticleVisibilityForCurrentState();
            UpdateSearchRadiusRange();
            RefreshNavigationCommands();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlgnVM] Errore caricamento immagine stack: {ex.Message}");
            ActiveImage = null;
        }
    }

    private void RefreshNavigationCommands()
    {
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
        GoToFirstImageCommand.NotifyCanExecuteChanged();
        GoToLastImageCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Comandi

    [RelayCommand] private void ZoomIn() 
    {
        Viewport.ZoomIn();
        OnPropertyChanged(nameof(ZoomStatusText));
    }

    [RelayCommand] private void ZoomOut() 
    {
        Viewport.ZoomOut();
        OnPropertyChanged(nameof(ZoomStatusText));
    }
    
    [RelayCommand] private void ResetView() 
    {
        Viewport.ResetView();
        OnPropertyChanged(nameof(ZoomStatusText));
    }
    
    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds() 
    {
        if (ActiveImage == null) return;

        await ApplyOptimalStretchAsync();
        
        var (mean, sigma) = ActiveImage.GetImageStatistics();
        _lockedKBlack = (ActiveImage.BlackPoint - mean) / sigma;
        _lockedKWhite = (ActiveImage.WhitePoint - mean) / sigma;
        _hasLockedThresholds = true; 
    }
    
    private bool CanResetThresholds() => ActiveImage != null;
    
    // --- Navigazione ---
    
    private async Task NavigateToImage(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _totalStackCount || newIndex == _currentStackIndex)
            return;
        
        TargetCoordinate = null; 
        await LoadStackImageAtIndexAsync(newIndex);
    }

    private int? GetPreviousAccessibleIndex()
    {
        for (int i = _currentStackIndex - 1; i >= 0; i--)
            if (IsIndexAccessible(i)) return i;
        return null;
    }

    private int? GetNextAccessibleIndex()
    {
        for (int i = _currentStackIndex + 1; i < _totalStackCount; i++)
            if (IsIndexAccessible(i)) return i;
        return null;
    }

    private bool CanShowPrevious() => IsStack && GetPreviousAccessibleIndex().HasValue;
    private bool CanShowNext() => IsStack && GetNextAccessibleIndex().HasValue;

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        var prevIndex = GetPreviousAccessibleIndex();
        if (prevIndex.HasValue) await NavigateToImage(prevIndex.Value);
    }

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        var nextIndex = GetNextAccessibleIndex();
        if (nextIndex.HasValue) await NavigateToImage(nextIndex.Value);
    }
    
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] 
    private async Task GoToFirstImage() => await NavigateToImage(0);

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task GoToLastImage() => await NavigateToImage(_totalStackCount - 1);
    
    // --- Allineamento ---

    [RelayCommand(CanExecute = nameof(CanCalculateCenters))]
    private async Task CalculateCenters()
    {
        CurrentState = AlignmentState.Calculating; 

        try
        {
            var currentCoords = CoordinateEntries.Select(e => e.Coordinate);

            var newCoords = await _alignmentService.CalculateCentersAsync(
                SelectedMode, 
                CenteringMethod.LocalRegion,
                _sourceData, 
                currentCoords, 
                SearchRadius
            );

            int i = 0;
            foreach (var coord in newCoords)
            {
                if (i < CoordinateEntries.Count) CoordinateEntries[i].Coordinate = coord;
                i++;
            }

            CurrentState = AlignmentState.ResultsReady;
            
            UpdateReticleVisibilityForCurrentState();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore calcolo: {ex}");
            CurrentState = AlignmentState.Initial;
        }
    }
    
    private bool CanCalculateCenters()
    {
        var currentCoords = CoordinateEntries.Select(e => e.Coordinate).ToList();
        return _alignmentService.CanCalculate(SelectedMode, currentCoords, _totalStackCount);
    }

    [RelayCommand(CanExecute = nameof(CanApplyAlignment))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
        try
        {
            var centers = CoordinateEntries.Select(e => e.Coordinate).ToList();
            FinalProcessedData = (await _alignmentService.ApplyCenteringAsync(_sourceData, centers))!;
            DialogResult = true; 
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Apply fallito: {ex.Message}");
            DialogResult = false;
            CurrentState = AlignmentState.Initial;
        }
    }
    
    [RelayCommand] private void CancelCalculation() => ResetAlignmentState();
    
    private bool CanApplyAlignment() => CurrentState == AlignmentState.ResultsReady;

    #endregion
    
    #region Metodi Pubblici

    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint)
    {
        Viewport.ApplyZoomAtPoint(scaleFactor, viewportZoomPoint);
        OnPropertyChanged(nameof(ZoomStatusText));
    }

    public void ApplyPan(double deltaX, double deltaY) => Viewport.ApplyPan(deltaX, deltaY);

    public void SetTargetCoordinate(Point imageCoordinate)
    {
        if (CurrentState == AlignmentState.Processing) return;

        if (CurrentState == AlignmentState.Initial) 
        {
            if (SelectedMode == AlignmentMode.Automatic) return; 
            if (IsStack && SelectedMode == AlignmentMode.Guided)
            {
                if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return;
            }
        }
    
        TargetCoordinate = imageCoordinate; 
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count)
        {
            CoordinateEntries[_currentStackIndex].Coordinate = imageCoordinate;
        }
    
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }
 
    public void ClearTarget()
    {
        if (CurrentState != AlignmentState.Initial)
        {
            ResetAlignmentState();
            return;
        }
    
        if (SelectedMode == AlignmentMode.Automatic) return;
        if (IsStack && SelectedMode == AlignmentMode.Guided)
        {
            if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return;
        }
    
        TargetCoordinate = null; 
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count)
        {
            CoordinateEntries[_currentStackIndex].Coordinate = null;
        }
    
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }
    
    #endregion
    
    #region Helpers

    partial void OnBlackPointChanged(double value)
    {
        if (ActiveImage != null)
        {
            ActiveImage.BlackPoint = value;
            UpdateLockedSigmaFactors();
        }
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveImage != null)
        {
            ActiveImage.WhitePoint = value;
            UpdateLockedSigmaFactors();
        }
    }
    
    partial void OnSearchRadiusChanged(int value) => Viewport.SearchRadius = value;
    partial void OnTargetCoordinateChanged(Point? value) => Viewport.TargetCoordinate = value;
    
    partial void OnSelectedModeChanged(AlignmentMode value)
    {
        ResetAlignmentState();
        RefreshNavigationCommands();
        if (!IsIndexAccessible(_currentStackIndex)) _ = NavigateToImage(0); 
        UpdateStackCounterText();
    }
    
    partial void OnCurrentStateChanged(AlignmentState value)
    {
        RefreshNavigationCommands();
        UpdateStackCounterText();
    }
    
    private void ResetAlignmentState()
    {
        CurrentState = AlignmentState.Initial;
        foreach (var entry in CoordinateEntries) entry.Coordinate = null;
        TargetCoordinate = null;
        CalculateCentersCommand.NotifyCanExecuteChanged();
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
    }
    
    private async Task ApplyOptimalStretchAsync()
    {
        if (ActiveImage == null) return;
        await ActiveImage.ResetThresholdsAsync();
        BlackPoint = ActiveImage.BlackPoint;
        WhitePoint = ActiveImage.WhitePoint;
    }
    
    private void UpdateReticleVisibilityForCurrentState()
    {
        if (_currentStackIndex < 0 || _currentStackIndex >= CoordinateEntries.Count)
        {
            TargetCoordinate = null;
            return;
        }
        var currentEntry = CoordinateEntries[_currentStackIndex];
        TargetCoordinate = currentEntry.Coordinate;
    }
    
    private void UpdateSearchRadiusRange()
    {
        if (ActiveImage == null || ActiveImage.ImageSize == default(Size))
        {
            MinSearchRadius = 0; MaxSearchRadius = 100;
        }
        else
        {
            double minDimension = Math.Min(ActiveImage.ImageSize.Width, ActiveImage.ImageSize.Height);
            MinSearchRadius = 0; 
            MaxSearchRadius = (int)Math.Floor(minDimension / 2.0);
        }
        SearchRadius = Math.Clamp(SearchRadius, MinSearchRadius, MaxSearchRadius);
    }
    
    private bool IsIndexAccessible(int index)
    {
        if (CurrentState != AlignmentState.Initial) return true;
        if (index < 0 || index >= _totalStackCount) return false;

        return SelectedMode switch
        {
            AlignmentMode.Automatic => index == 0,
            AlignmentMode.Guided => index == 0 || index == _totalStackCount - 1,
            _ => true
        };
    }
    
    private void UpdateStackCounterText()
    {
        if (CurrentState != AlignmentState.Initial || SelectedMode == AlignmentMode.Manual)
        {
            StackCounterText = $"{_currentStackIndex + 1} / {_totalStackCount}";
            return;
        }
        
        if (SelectedMode == AlignmentMode.Automatic)
        {
            StackCounterText = "1 / 1";
            return;
        }
        
        if (SelectedMode == AlignmentMode.Guided)
        {
            int visibleStep = (_currentStackIndex == 0) ? 1 : 2;
            StackCounterText = $"{visibleStep} / 2";
        }
    }
    
    private void UpdateLockedSigmaFactors()
    {
        if (ActiveImage == null) return;
        var (mean, sigma) = ActiveImage.GetImageStatistics();
        _lockedKBlack = (BlackPoint - mean) / sigma;
        _lockedKWhite = (WhitePoint - mean) / sigma;
        _hasLockedThresholds = true;
    }
    
    #endregion
}