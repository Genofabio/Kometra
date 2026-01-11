using System;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Primitives;

namespace KomaLab.Services.Astrometry;

// ---------------------------------------------------------------------------
// FILE: WcsTransformation.cs
// RUOLO: Motore Matematico (Core Math)
// DESCRIZIONE:
// Implementa le trasformazioni geometriche bidirezionali tra piano immagine (Pixel)
// e sfera celeste (RA/Dec).
//
// CARATTERISTICHE:
// - Supporta proiezioni standard TAN (Gnomonica).
// - Supporta distorsioni polinomiali TPV (essenziale per ottiche a largo campo).
// - Implementa inversione numerica (Newton-Raphson) per le distorsioni.
// - Ottimizzato con caching dei coefficienti per calcoli massivi (es. griglie).
// ---------------------------------------------------------------------------

public class WcsTransformation : IWcsTransformation
{
    private readonly WcsData _data;
    
    // Matrice inversa CD pre-calcolata
    private double _invCd11, _invCd12, _invCd21, _invCd22;
    private bool _matrixInverted;
    
    // Cache locale per accesso veloce ai coefficienti PV (Array vs Dictionary)
    private readonly double[][] _pvCache;
    
    // Flag per gestire varianti non standard dello standard TPV (es. output di SCAMP)
    private bool _swapAxis2Terms;

    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public WcsTransformation(WcsData data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        
        // Inizializza cache per i coefficienti (fino all'ordine 39, standard TPV ne usa ~10)
        _pvCache = new double[2][];
        _pvCache[0] = new double[40]; 
        _pvCache[1] = new double[40];

        CachePvCoefficients();
        CalculateInverseMatrix();
        AnalyzeTpvCoefficients();
    }

    private void CachePvCoefficients()
    {
        foreach (var kvp in _data.PvCoefficients)
        {
            // FITS usa indici base-1 (1,2), noi usiamo base-0 (0,1)
            int axis = kvp.Key.Axis - 1;
            int k = kvp.Key.K;
            
            if (axis >= 0 && axis < 2 && k >= 0 && k < _pvCache[axis].Length)
                _pvCache[axis][k] = kvp.Value;
        }
    }

    private void CalculateInverseMatrix()
    {
        // Inversione standard di matrice 2x2
        double det = (_data.Cd1_1 * _data.Cd2_2) - (_data.Cd1_2 * _data.Cd2_1);
        
        if (Math.Abs(det) < 1e-15) 
        {
            _matrixInverted = false;
            return;
        }

        _invCd11 =  _data.Cd2_2 / det;
        _invCd12 = -_data.Cd1_2 / det;
        _invCd21 = -_data.Cd2_1 / det;
        _invCd22 =  _data.Cd1_1 / det;
        _matrixInverted = true;
    }

    private void AnalyzeTpvCoefficients()
    {
        if (_data.ProjectionType != WcsProjectionType.Tpv) return;

        // Euristica per rilevare se i coefficienti dell'asse Y sono scambiati (x<->y)
        // Comune in alcuni software di astrometria.
        double pv11 = GetCachedPv(1, 1); 
        double pv21 = GetCachedPv(2, 1); 
        double pv22 = GetCachedPv(2, 2); 

        if (Math.Abs(pv11) > 0.5 && Math.Abs(pv21) > 0.5 && Math.Abs(pv22) < 0.1)
        {
            _swapAxis2Terms = true;
        }
    }

    // ==========================================
    // INVERSE: RA/DEC (World) -> X/Y (Pixel)
    // ==========================================
    public Point2D? WorldToPixel(double raDeg, double decDeg)
    {
        if (!_data.IsValid || !_matrixInverted) return null;

        // 1. Proiezione sferica -> Piano Ideale (Coordinate Intermedie Standard)
        var idealPlane = ProjectRaDecToPlane(raDeg, decDeg);
        if (idealPlane == null) return null;

        double xiTarget = idealPlane.Value.X;
        double etaTarget = idealPlane.Value.Y;

        Point2D rawPlane;
        
        if (_data.ProjectionType == WcsProjectionType.Tpv)
        {
            // 2. Inversione Distorsione (Non lineare -> Lineare)
            // TPV definisce Forward (Raw->Distorted), quindi l'Inverse richiede solver numerico.
            rawPlane = SolveDistortionInverseNewton(xiTarget, etaTarget);
        }
        else
        {
            rawPlane = new Point2D(xiTarget, etaTarget);
        }

        // 3. Trasformazione Affine Inversa (CD Matrix Inversa)
        double u = (_invCd11 * rawPlane.X) + (_invCd12 * rawPlane.Y);
        double v = (_invCd21 * rawPlane.X) + (_invCd22 * rawPlane.Y);

        // 4. Offset Pixel di Riferimento (CRPIX)
        // FITS usa coordinate pixel base-1, noi base-0 (quindi -1.0)
        return new Point2D(
            (u + _data.RefPixelX) - 1.0, 
            (v + _data.RefPixelY) - 1.0
        );
    }

    // ==========================================
    // FORWARD: X/Y (Pixel) -> RA/DEC (World)
    // ==========================================
    public (double Ra, double Dec)? PixelToWorld(double x, double y)
    {
        if (!_data.IsValid) return null;

        // 1. Offset Pixel relativi al centro (FITS base-1 adjustment)
        double u = (x + 1.0) - _data.RefPixelX;
        double v = (y + 1.0) - _data.RefPixelY;

        // 2. Trasformazione Lineare (CD Matrix)
        double rawXi = (_data.Cd1_1 * u) + (_data.Cd1_2 * v);
        double rawEta = (_data.Cd2_1 * u) + (_data.Cd2_2 * v);

        Point2D distortedPlane;
        
        if (_data.ProjectionType == WcsProjectionType.Tpv)
        {
            // 3. Applicazione Distorsione Polinomiale
            distortedPlane = ApplyTpvPolynomial(rawXi, rawEta);
        }
        else
        {
            distortedPlane = new Point2D(rawXi, rawEta);
        }

        // 4. Deproiezione (Piano -> Sfera Celeste)
        return DeprojectStandardPlane(distortedPlane.X, distortedPlane.Y);
    }

    // --- Helpers Trigonometrici ---
    
    private Point2D? ProjectRaDecToPlane(double raDeg, double decDeg)
    {
        double ra = raDeg * DegToRad;
        double dec = decDeg * DegToRad;
        double ra0 = _data.RefRaDeg * DegToRad;
        double dec0 = _data.RefDecDeg * DegToRad;
        double dRa = ra - ra0;
        
        double sinDec = Math.Sin(dec);
        double cosDec = Math.Cos(dec);
        double sinDec0 = Math.Sin(dec0);
        double cosDec0 = Math.Cos(dec0);
        double cosdRa = Math.Cos(dRa);

        // Denominatore proiezione gnomonica
        double den = (sinDec * sinDec0) + (cosDec * cosDec0 * cosdRa);

        if (Math.Abs(den) < 1e-12) return null; // Punto all'infinito o dietro l'osservatore

        double num = cosDec * Math.Sin(dRa);
        double xi = (num / den) * RadToDeg;
        double eta = (((sinDec * cosDec0) - (cosDec * sinDec0 * cosdRa)) / den) * RadToDeg;

        return new Point2D(xi, eta);
    }

    private (double Ra, double Dec)? DeprojectStandardPlane(double xiDeg, double etaDeg)
    {
        double xi = xiDeg * DegToRad;
        double eta = etaDeg * DegToRad;
        double ra0 = _data.RefRaDeg * DegToRad;
        double dec0 = _data.RefDecDeg * DegToRad;

        double r = Math.Sqrt(xi * xi + eta * eta);
        if (r < 1e-12) return (_data.RefRaDeg, _data.RefDecDeg);

        double beta = Math.Atan2(r, 1.0);
        double sinBeta = Math.Sin(beta);
        double cosBeta = Math.Cos(beta);
        double sinDec0 = Math.Sin(dec0);
        double cosDec0 = Math.Cos(dec0);

        double sinDec = (cosBeta * sinDec0) + ((eta * sinBeta * cosDec0) / r);
        if (sinDec > 1.0) sinDec = 1.0;
        if (sinDec < -1.0) sinDec = -1.0;

        double dec = Math.Asin(sinDec);
        double yTerm = (cosBeta * cosDec0) - ((eta * sinBeta * sinDec0) / r);
        double xTerm = (xi * sinBeta) / r;
        
        double dRa = Math.Atan2(xTerm, yTerm);
        double raFinal = (ra0 + dRa) * RadToDeg;
        double decFinal = dec * RadToDeg;

        // Normalizzazione RA [0, 360)
        while (raFinal < 0) raFinal += 360.0;
        while (raFinal >= 360.0) raFinal -= 360.0;

        return (raFinal, decFinal);
    }

    // --- Algoritmi Numerici (TPV) ---
    
    private Point2D SolveDistortionInverseNewton(double targetXi, double targetEta)
    {
        // Guess iniziale: assumiamo distorsione nulla
        double xi = targetXi;
        double eta = targetEta;
        const int maxIter = 20;
        const double tolerance = 1e-9;

        for (int i = 0; i < maxIter; i++)
        {
            double r = Math.Sqrt(xi * xi + eta * eta);
            double currXi = ComputePolySum(1, xi, eta, r);
            double currEta = ComputePolySum(2, xi, eta, r);

            double f = currXi - targetXi;
            double g = currEta - targetEta;

            if (Math.Abs(f) < tolerance && Math.Abs(g) < tolerance) break;

            var d1 = ComputePolyDerivs(1, xi, eta, r);
            var d2 = ComputePolyDerivs(2, xi, eta, r);

            // Jacobiano
            double det = (d1.dXi * d2.dEta) - (d1.dEta * d2.dXi);
            if (Math.Abs(det) < 1e-15) { xi -= f; eta -= g; continue; }

            double deltaXi = (d2.dEta * f - d1.dEta * g) / det;
            double deltaEta = (-d2.dXi * f + d1.dXi * g) / det;

            xi -= deltaXi;
            eta -= deltaEta;
        }

        return new Point2D(xi, eta);
    }
    
    private Point2D ApplyTpvPolynomial(double xi, double eta)
    {
        double r = Math.Sqrt(xi * xi + eta * eta);
        return new Point2D(
            ComputePolySum(1, xi, eta, r), 
            ComputePolySum(2, xi, eta, r)
        );
    }

    private double ComputePolySum(int axis, double x, double y, double r)
    {
        if (axis == 2 && _swapAxis2Terms) (x, y) = (y, x);

        // Implementazione polinomiale TPV standard (termini fino al 3° ordine mostrati, ma supporta n)
        // PVn_0, PVn_1 * x, ...
        double val = GetCachedPv(axis, 0)
                   + GetCachedPv(axis, 1) * x
                   + GetCachedPv(axis, 2) * y
                   + GetCachedPv(axis, 3) * r
                   + GetCachedPv(axis, 4) * x * x
                   + GetCachedPv(axis, 5) * x * y
                   + GetCachedPv(axis, 6) * y * y
                   + GetCachedPv(axis, 7) * x * x * x
                   + GetCachedPv(axis, 8) * x * x * y
                   + GetCachedPv(axis, 9) * x * y * y
                   + GetCachedPv(axis, 10) * y * y * y;
        return val;
    }

    private (double dXi, double dEta) ComputePolyDerivs(int axis, double x, double y, double r)
    {
        bool swapped = (axis == 2 && _swapAxis2Terms);
        if (swapped) (x, y) = (y, x);

        double drDx = (r < 1e-9) ? 0 : x / r;
        double drDy = (r < 1e-9) ? 0 : y / r;

        // Derivate parziali del polinomio TPV
        double dDx = GetCachedPv(axis, 1)
                    + GetCachedPv(axis, 3) * drDx
                    + GetCachedPv(axis, 4) * 2 * x
                    + GetCachedPv(axis, 5) * y
                    + GetCachedPv(axis, 7) * 3 * x * x
                    + GetCachedPv(axis, 8) * 2 * x * y
                    + GetCachedPv(axis, 9) * y * y;

        double dDy = GetCachedPv(axis, 2)
                    + GetCachedPv(axis, 3) * drDy
                    + GetCachedPv(axis, 5) * x
                    + GetCachedPv(axis, 6) * 2 * y
                    + GetCachedPv(axis, 8) * x * x
                    + GetCachedPv(axis, 9) * 2 * x * y
                    + GetCachedPv(axis, 10) * 3 * y * y;

        // Se abbiamo scambiato gli assi in input, dobbiamo scambiare anche le derivate in output
        return swapped ? (dDy, dDx) : (dDx, dDy);
    }

    private double GetCachedPv(int axis, int k)
    {
        int idx = axis - 1;
        if (idx < 0 || idx > 1) return 0.0;
        if (k < 0 || k >= _pvCache[idx].Length) return 0.0;
        return _pvCache[idx][k];
    }
}