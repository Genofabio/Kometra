using System;
using System.Collections.Generic;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits.Metadata;

/// <summary>
/// Unica autorità per l'interpretazione, la lettura e la manipolazione dei metadati FITS.
/// </summary>
public interface IFitsMetadataService
{
    // --- Accesso ai Dati (Lettura Base) ---
    int GetIntValue(FitsHeader header, string key, int defaultValue = 0);
    string GetStringValue(FitsHeader header, string key);
    double GetDoubleValue(FitsHeader header, string key, double defaultValue = 0.0);
    
    // --- Accesso ai Dati (Interpretazione Scientifica) ---
    DateTime? GetObservationDate(FitsHeader header);
    double? GetFocalLength(FitsHeader header);
    double? GetPixelSize(FitsHeader header);
    FitsBitDepth GetBitDepth(FitsHeader header);
    WcsData ExtractWcs(FitsHeader header); // Necessario per Astrometria
    GeographicLocation? GetObservatoryLocation(FitsHeader header); // Necessario per Effemeridi
    
    // --- Regole di Dominio (Validazione) ---
    bool IsStructuralKey(string key); // NECESSARIO: Il ViewModel chiede "Posso far modificare questa chiave?"
    bool AreGeometricallyCompatible(FitsHeader h1, FitsHeader h2);

    // --- Manipolazione (Scrittura) ---
    void AddValue(FitsHeader header, string key, object value, string? comment = null);
    void SetValue(FitsHeader header, string key, object value, string? comment = null);
    void TransferMetadata(FitsHeader source, FitsHeader destination);
    FitsHeader CreateHeaderFromTemplate(FitsHeader template, Array newPixels, FitsBitDepth depth);
    
    // --- Utility ---
    IEnumerable<T> SortByDate<T>(IEnumerable<T> items, Func<T, FitsHeader?> headerSelector);
}