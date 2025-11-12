using KomaLab.ViewModels; // Per AlignmentMode
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;
using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace KomaLab.Services;

/// <summary>
/// Definisce i metodi di centraggio disponibili.
/// </summary>
public enum CenteringMethod
{
    /// <summary>
    /// Calcola il centro di massa (baricentro).
    /// </summary>
    Centroid,
    
    /// <summary>
    /// Trova il picco sub-pixel (centroide 3x3).
    /// </summary>
    Peak,
    
    /// <summary>
    /// Esegue un fit Gaussiano 2D sulla regione.
    /// </summary>
    GaussianFit
}

/// <summary>
/// Definisce il contratto per il servizio di calcolo dell'allineamento.
/// </summary>
public interface IAlignmentService
{
    #region Logica di Business (Alto Livello)

    /// <summary>
    /// Calcola i centri delle immagini in base alla modalità e alle coordinate fornite.
    /// </summary>
    /// <param name="mode">La modalità di allineamento (Automatic, Guided, Manual).</param>
    /// <param name="currentCoordinates">L'elenco delle coordinate correnti.</param>
    /// <param name="imageSize">La dimensione dell'immagine (necessaria per il calcolo automatico).</param>
    /// <returns>Un nuovo elenco di coordinate calcolate.</returns>
    Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        Size imageSize);

    /// <summary>
    /// Determina se il calcolo può essere eseguito in base allo stato corrente.
    /// </summary>
    /// <param name="mode">La modalità di allineamento.</param>
    /// <param name="currentCoordinates">L'elenco delle coordinate correnti.</param>
    /// <param name="totalCount">Il numero totale di immagini.</param>
    /// <returns>True se il calcolo è abilitato, altrimenti False.</returns>
    bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount);

    #endregion

    #region Primitive di Image Processing (Basso Livello)

    /// <summary>
    /// Carica i dati FITS grezzi in un oggetto Mat di OpenCV, mantenendo la precisione.
    /// </summary>
    /// <param name="fitsData">I dati FITS da caricare.</param>
    /// <returns>Una Mat di OpenCV (es. CV_32FC1 o CV_64FC1).</returns>
    Mat LoadFitsDataAsMat(FitsImageData fitsData);

    /// <summary>
    /// Calcola il centroide (centro di massa) di un'immagine Mat.
    /// </summary>
    /// <param name="imageMat">L'immagine Mat (in float/double) su cui calcolare.</param>
    /// <param name="sigma">La deviazione standard per il filtro Gaussiano.</param>
    /// <returns>Le coordinate (X, Y) del centroide.</returns>
    Point GetCenterByCentroid(Mat imageMat, double sigma = 5.0);
    
    /// <summary>
    /// Trova il picco sub-pixel (centroide 3x3) di un'immagine Mat.
    /// </summary>
    /// <param name="imageMat">L'immagine Mat (in float/double) su cui calcolare.</param>
    /// <param name="sigma">La deviazione standard per il filtro Gaussiano.</param>
    /// <returns>Le coordinate (X, Y) del picco.</returns>
    Point GetCenterByPeak(Mat imageMat, double sigma = 1.0);
    
    /// <summary>
    /// Trova il centro di un oggetto in una Mat tramite un fit Gaussiano 2D.
    /// </summary>
    /// <param name="imageMat">L'immagine Mat (in float/double) su cui calcolare.</param>
    /// <param name="thresholdRatio">Soglia (relativa al max) per isolare l'oggetto.</param>
    /// <param name="sigma">Sigma del filtro gaussiano pre-fitting.</param>
    /// <returns>Le coordinate (X, Y) del centro fittato.</returns>
    Point GetCenterByGaussianFit(Mat imageMat, double thresholdRatio = 0.5, double sigma = 3.0);
    
    /// <summary>
    /// Trova la regione locale più grande e ne calcola il centro.
    /// </summary>
    /// <param name="fitsData">I dati FITS originali da cui caricare l'immagine.</param>
    /// <param name="centerFunc">Il metodo da usare per trovare il centro (Centroid, Peak, GaussianFit).</param>
    /// <param name="thresholdRatio">Soglia (relativa alla media) per trovare gli oggetti.</param>
    /// <param name="minArea">Area minima in pixel per considerare un oggetto.</param>
    /// <param name="padding">Pixel di padding da aggiungere al ritaglio.</param>
    /// <returns>Le coordinate (X, Y) globali del centro dell'oggetto.</returns>
    Point GetCenterOfLocalRegion(
        FitsImageData fitsData,
        CenteringMethod centerFunc,
        double thresholdRatio = 0.1,
        int minArea = 10,
        int padding = 0);
    
    /// <summary>
    /// Sposta (shifta) un'immagine (Mat) in modo che un punto specifico diventi il centro del frame.
    /// </summary>
    /// <param name="imageMat">L'immagine Mat da spostare.</param>
    /// <param name="centerPoint">Il punto (X, Y) che deve diventare il nuovo centro.</param>
    /// <returns>Una nuova Mat centrata (e interpolata).</returns>
    Mat CenterImageByCoords(Mat imageMat, Point centerPoint);

    #endregion
}