using System;
using System.Collections.Generic;
using Kometra.Models.Astrometry;
using Kometra.Models.Astrometry.Wcs;
using Kometra.Models.Primitives;

namespace Kometra.Services.Astrometry;

// ---------------------------------------------------------------------------
// FILE: WcsTransformation.cs
// DESCRIZIONE:
// Gestore unificato per le trasformazioni WCS.
// Supporta proiezioni: TAN (Lineare), TPV (Distorsione PV), SIP (Distorsione UV).
// ---------------------------------------------------------------------------

public class WcsTransformation : IWcsTransformation
{
    private readonly WcsData _data;
    private readonly int _imageHeight; // Necessario per inversione asse Y (UI vs FITS)

    // --- MATRICI LINEARI (CD / PC) ---
    private double _invCd11, _invCd12, _invCd21, _invCd22;
    private bool _matrixInverted;

    // --- CACHE TPV (PV Keywords) ---
    // Array jagged per accesso veloce [asse][k]
    private readonly double[][] _pvCache;
    private bool _swapAxis2Terms; // Flag per varianti non standard TPV

    // --- CACHE SIP (A/B e AP/BP Keywords) ---
    // Matrici dense [p, q] per accesso veloce
    private double[,] _sipA;
    private double[,] _sipB;
    private double[,] _sipAp; // Reverse A (opzionale)
    private double[,] _sipBp; // Reverse B (opzionale)
    private int _sipOrderA, _sipOrderB, _sipOrderAp, _sipOrderBp;
    private bool _hasReverseSip; // True se il FITS contiene AP/BP

    // --- COSTANTI ---
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public WcsTransformation(WcsData data, int imageHeight)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _imageHeight = imageHeight;

        // 1. Inizializza Cache TPV (fino a ordine ~39, standard TPV ~10)
        _pvCache = new double[2][];
        _pvCache[0] = new double[40];
        _pvCache[1] = new double[40];

        // 2. Popola Cache e Pre-calcoli
        CachePvCoefficients();
        CacheSipCoefficients();
        CalculateInverseMatrix();
        AnalyzeTpvCoefficients();
    }

    #region Initialization

    private void CachePvCoefficients()
    {
        if (_data.PvCoefficients == null) return;
        
        foreach (var kvp in _data.PvCoefficients)
        {
            // FITS usa indici asse 1,2 -> noi usiamo 0,1
            int axis = kvp.Key.Axis - 1;
            int k = kvp.Key.K;

            if (axis >= 0 && axis < 2 && k >= 0 && k < _pvCache[axis].Length)
                _pvCache[axis][k] = kvp.Value;
        }
    }

    private void CacheSipCoefficients()
    {
        // Se non è SIP, usciamo subito per risparmiare memoria
        if (_data.ProjectionType != WcsProjectionType.Sip) return;

        // NOTA: Assumo che WcsData esponga i dizionari SipXCoefficients e gli int SipOrderX.
        // Se mancano nel modello, verranno trattati come null/0.
        
        _sipOrderA = _data.SipOrderA;
        _sipOrderB = _data.SipOrderB;
        _sipA = ConvertDictToMatrix(_data.SipACoefficients, _sipOrderA);
        _sipB = ConvertDictToMatrix(_data.SipBCoefficients, _sipOrderB);

        // Coefficienti inversi (AP/BP) - Utili per WorldToPixel veloce
        _sipOrderAp = _data.SipOrderAp;
        _sipOrderBp = _data.SipOrderBp;
        _sipAp = ConvertDictToMatrix(_data.SipApCoefficients, _sipOrderAp);
        _sipBp = ConvertDictToMatrix(_data.SipBpCoefficients, _sipOrderBp);

        _hasReverseSip = (_sipAp != null && _sipBp != null);
    }

    private double[,] ConvertDictToMatrix(Dictionary<(int, int), double> dict, int order)
    {
        if (dict == null || dict.Count == 0 || order <= 0) return null;
        
        // Matrice quadrata [order+1, order+1]
        var matrix = new double[order + 1, order + 1];
        foreach (var kvp in dict)
        {
            int p = kvp.Key.Item1;
            int q = kvp.Key.Item2;
            if (p <= order && q <= order)
            {
                matrix[p, q] = kvp.Value;
            }
        }
        return matrix;
    }

    private void CalculateInverseMatrix()
    {
        // Inversione standard 2x2
        double det = (_data.Cd1_1 * _data.Cd2_2) - (_data.Cd1_2 * _data.Cd2_1);

        if (Math.Abs(det) < 1e-15)
        {
            _matrixInverted = false;
            return;
        }

        _invCd11 = _data.Cd2_2 / det;
        _invCd12 = -_data.Cd1_2 / det;
        _invCd21 = -_data.Cd2_1 / det;
        _invCd22 = _data.Cd1_1 / det;
        _matrixInverted = true;
    }

    private void AnalyzeTpvCoefficients()
    {
        if (_data.ProjectionType != WcsProjectionType.Tpv) return;

        // Euristica per rilevare coordinate scambiate nel TPV (es. SCAMP output)
        double pv11 = GetCachedPv(1, 1);
        double pv21 = GetCachedPv(2, 1);
        double pv22 = GetCachedPv(2, 2);

        if (Math.Abs(pv11) > 0.5 && Math.Abs(pv21) > 0.5 && Math.Abs(pv22) < 0.1)
        {
            _swapAxis2Terms = true;
        }
    }

    #endregion

    // ==========================================
    // INVERSE: RA/DEC (World) -> X/Y (Pixel UI)
    // ==========================================
    public Point2D? WorldToPixel(double raDeg, double decDeg)
    {
        if (!_data.IsValid || !_matrixInverted) return null;

        // 1. Proiezione Sferica -> Piano Intermedio Ideale (Xi, Eta)
        var idealPlane = ProjectRaDecToPlane(raDeg, decDeg);
        if (idealPlane == null) return null;

        double xi = idealPlane.Value.X;
        double eta = idealPlane.Value.Y;

        // 2. Gestione TPV (Distorsione su Xi/Eta)
        // Se è TPV, dobbiamo invertire la distorsione per trovare Xi/Eta lineari
        if (_data.ProjectionType == WcsProjectionType.Tpv)
        {
            var undistorted = SolveTpvInverseNewton(xi, eta);
            xi = undistorted.X;
            eta = undistorted.Y;
        }

        // 3. Trasformazione Lineare Inversa (CD Matrix Inv) -> U/V
        double u = (_invCd11 * xi) + (_invCd12 * eta);
        double v = (_invCd21 * xi) + (_invCd22 * eta);

        // 4. Gestione SIP (Distorsione su U/V)
        // Se è SIP, dobbiamo invertire la distorsione per trovare U/V pixel reali
        if (_data.ProjectionType == WcsProjectionType.Sip)
        {
            if (_hasReverseSip)
            {
                // Via Veloce: Polinomi inversi AP/BP disponibili
                double deltaU = ComputeSipPoly(u, v, _sipAp, _sipOrderAp);
                double deltaV = ComputeSipPoly(u, v, _sipBp, _sipOrderBp);
                u += deltaU;
                v += deltaV;
            }
            else
            {
                // Via Lenta: Inversione iterativa dei polinomi A/B
                var solved = SolveSipInverseIterative(u, v);
                u = solved.X;
                v = solved.Y;
            }
        }

        // 5. Conversione FITS Pixel -> UI Pixel
        double fitsX = (u + _data.RefPixelX) - 1.0;
        double fitsY = (v + _data.RefPixelY) - 1.0;

        return new Point2D(fitsX, _imageHeight - fitsY);
    }

    // ==========================================
    // FORWARD: X/Y (Pixel UI) -> RA/DEC (World)
    // ==========================================
    public (double Ra, double Dec)? PixelToWorld(double x, double y)
    {
        if (!_data.IsValid) return null;

        // 1. UI -> FITS Coordinate Relative (U, V)
        double fitsY = _imageHeight - y;
        double u = (x + 1.0) - _data.RefPixelX;
        double v = (fitsY + 1.0) - _data.RefPixelY;

        // 2. Gestione SIP (Distorsione su U/V)
        // SIP applica le correzioni PRIMA della rotazione
        if (_data.ProjectionType == WcsProjectionType.Sip)
        {
            double deltaU = ComputeSipPoly(u, v, _sipA, _sipOrderA);
            double deltaV = ComputeSipPoly(u, v, _sipB, _sipOrderB);
            u += deltaU;
            v += deltaV;
        }

        // 3. Trasformazione Lineare (CD Matrix) -> Xi/Eta
        double xi = (_data.Cd1_1 * u) + (_data.Cd1_2 * v);
        double eta = (_data.Cd2_1 * u) + (_data.Cd2_2 * v);

        // 4. Gestione TPV (Distorsione su Xi/Eta)
        // TPV applica le correzioni DOPO la rotazione
        if (_data.ProjectionType == WcsProjectionType.Tpv)
        {
            var distorted = ApplyTpvPolynomial(xi, eta);
            xi = distorted.X;
            eta = distorted.Y;
        }

        // 5. Deproiezione (Piano -> Sfera Celeste)
        return DeprojectStandardPlane(xi, eta);
    }

    #region Spherical Trigonometry Helpers

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

        double den = (sinDec * sinDec0) + (cosDec * cosDec0 * cosdRa);
        if (Math.Abs(den) < 1e-12) return null;

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
        // Clamp per errori di floating point
        if (sinDec > 1.0) sinDec = 1.0;
        if (sinDec < -1.0) sinDec = -1.0;

        double dec = Math.Asin(sinDec);
        double yTerm = (cosBeta * cosDec0) - ((eta * sinBeta * sinDec0) / r);
        double xTerm = (xi * sinBeta) / r;

        double dRa = Math.Atan2(xTerm, yTerm);
        double raFinal = (ra0 + dRa) * RadToDeg;
        double decFinal = dec * RadToDeg;

        while (raFinal < 0) raFinal += 360.0;
        while (raFinal >= 360.0) raFinal -= 360.0;

        return (raFinal, decFinal);
    }

    #endregion

    #region TPV Algorithms (Newton-Raphson & Poly)

    private Point2D SolveTpvInverseNewton(double targetXi, double targetEta)
    {
        double xi = targetXi;
        double eta = targetEta;
        const int maxIter = 20;
        const double tolerance = 1e-9;

        for (int i = 0; i < maxIter; i++)
        {
            double r = Math.Sqrt(xi * xi + eta * eta);
            double currXi = ComputePvSum(1, xi, eta, r);
            double currEta = ComputePvSum(2, xi, eta, r);

            double f = currXi - targetXi;
            double g = currEta - targetEta;

            if (Math.Abs(f) < tolerance && Math.Abs(g) < tolerance) break;

            var d1 = ComputePvDerivs(1, xi, eta, r);
            var d2 = ComputePvDerivs(2, xi, eta, r);

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
            ComputePvSum(1, xi, eta, r),
            ComputePvSum(2, xi, eta, r)
        );
    }

    private double ComputePvSum(int axis, double x, double y, double r)
    {
        if (axis == 2 && _swapAxis2Terms) (x, y) = (y, x);

        // Polinomio standard TPV fino all'ordine cubico + termine radiale r
        return GetCachedPv(axis, 0)
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
    }

    private (double dXi, double dEta) ComputePvDerivs(int axis, double x, double y, double r)
    {
        bool swapped = (axis == 2 && _swapAxis2Terms);
        if (swapped) (x, y) = (y, x);

        double drDx = (r < 1e-9) ? 0 : x / r;
        double drDy = (r < 1e-9) ? 0 : y / r;

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

        return swapped ? (dDy, dDx) : (dDx, dDy);
    }

    private double GetCachedPv(int axis, int k)
    {
        int idx = axis - 1;
        if (idx < 0 || idx > 1) return 0.0;
        if (k < 0 || k >= _pvCache[idx].Length) return 0.0;
        return _pvCache[idx][k];
    }

    #endregion

    #region SIP Algorithms (Iterative & Poly)

    private double ComputeSipPoly(double u, double v, double[,] coeffs, int order)
    {
        if (coeffs == null) return 0.0;

        double sum = 0.0;
        // SIP definisce f(u,v) = sum(Coeff * u^p * v^q) con p+q <= order
        for (int p = 0; p <= order; p++)
        {
            for (int q = 0; q <= order; q++)
            {
                // Skip termini ordine troppo alto o termine zero (0,0)
                if ((p + q > order) || (p == 0 && q == 0)) continue;

                double val = coeffs[p, q];
                if (val == 0.0) continue;

                sum += val * Math.Pow(u, p) * Math.Pow(v, q);
            }
        }
        return sum;
    }

    private Point2D SolveSipInverseIterative(double uTarget, double vTarget)
    {
        // Se mancano AP/BP, risolviamo u_target = u + A(u, v) per u.
        // Usiamo Fixed Point Iteration: u_next = u_target - A(u_curr, v_curr)
        
        double u = uTarget;
        double v = vTarget;
        const int maxIter = 10; 
        const double tol = 1e-5; // Precisione sub-pixel

        for (int i = 0; i < maxIter; i++)
        {
            double correctionU = ComputeSipPoly(u, v, _sipA, _sipOrderA);
            double correctionV = ComputeSipPoly(u, v, _sipB, _sipOrderB);

            // Calcoliamo dove saremmo con i valori attuali
            double estimateU = u + correctionU;
            double estimateV = v + correctionV;

            double diffU = estimateU - uTarget;
            double diffV = estimateV - vTarget;

            if (Math.Abs(diffU) < tol && Math.Abs(diffV) < tol) break;

            // Correggiamo la stima
            u -= diffU;
            v -= diffV;
        }

        return new Point2D(u, v);
    }

    #endregion
}