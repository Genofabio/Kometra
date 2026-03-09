using Kometra.Models.Processing.Arithmetic;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

/// <summary>
/// Engine responsabile delle operazioni matematiche pixel-per-pixel tra matrici.
/// Gestisce automaticamente l'allineamento geometrico al centro e la promozione dei tipi di dati.
/// </summary>
public interface IArithmeticEngine
{
    /// <summary>
    /// Esegue l'operazione aritmetica specificata tra due matrici OpenCV.
    /// </summary>
    /// <param name="matA">La matrice di riferimento (determina le dimensioni finali).</param>
    /// <param name="matB">La matrice operatore (verrà centrata su matA).</param>
    /// <param name="op">L'operazione da eseguire (Somma, Sottrazione, Moltiplicazione, Divisione).</param>
    /// <returns>Una nuova matrice contenente il risultato dell'operazione, tipicamente a 32 o 64 bit float.</returns>
    Mat Execute(Mat matA, Mat matB, ArithmeticOperation op);
}