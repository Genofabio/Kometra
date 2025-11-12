using Avalonia;
using KomaLab.ViewModels; // Per AlignmentMode
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KomaLab.Services;

/// <summary>
/// Implementazione del servizio di calcolo dell'allineamento.
/// Contiene la logica di business pura.
/// </summary>
public class AlignmentService : IAlignmentService
{
    public async Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        Size imageSize)
    {
        // Trasforma l'input in una lista modificabile
        var newCoordinates = currentCoordinates.ToList();
        
        // --- QUESTA È LA LOGICA COPIATA DAL TUO VIEWMODEL ---
        
        await Task.Delay(500); // Simula lavoro
        Random rand = new Random();

        if (mode == AlignmentMode.Automatic)
        {
            // In Automatico, sovrascriviamo tutti i punti
            for(int i = 0; i < newCoordinates.Count; i++)
            {
                double x = rand.NextDouble() * imageSize.Width;
                double y = rand.NextDouble() * imageSize.Height;
                newCoordinates[i] = new Point(x, y);
            }
        }
        else if (mode == AlignmentMode.Guided)
        {
            // In Guidato, riempiamo solo i 'null'
            for(int i = 0; i < newCoordinates.Count; i++)
            {
                if (newCoordinates[i] == null)
                {
                    // (Qui andrebbe la logica di interpolazione)
                    double x = rand.NextDouble() * imageSize.Width;
                    double y = rand.NextDouble() * imageSize.Height;
                    newCoordinates[i] = new Point(x, y);
                }
            }
        }
        else if (mode == AlignmentMode.Manual)
        {
             // In Manuale, non facciamo nulla. Restituiamo i punti così come sono
             // (il pulsante "Calcola" serve solo a confermare).
        }
        
        return newCoordinates;
    }

    public bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount)
    {
        var coordinateList = currentCoordinates.ToList();
        if (coordinateList.Count == 0)
            return false;

        // --- QUESTA È LA LOGICA COPIATA DAL TUO VIEWMODEL ---
        
        switch (mode)
        {
            case AlignmentMode.Automatic:
                return true; 

            case AlignmentMode.Guided:
                if (totalCount == 1)
                {
                    // Immagine singola: basta che la prima (e unica) sia impostata
                    return coordinateList[0].HasValue;
                }
                
                // Stack (totalCount > 1)
                bool hasFirstAndLast = coordinateList.FirstOrDefault().HasValue && 
                                       coordinateList.LastOrDefault().HasValue;
                
                bool hasAllGuided = coordinateList.All(e => e.HasValue);
                return hasFirstAndLast || hasAllGuided;

            case AlignmentMode.Manual:
                bool hasAllManual = coordinateList.All(e => e.HasValue);
                return hasAllManual;

            default:
                return false;
        }
    }
}