using System;
using System.Collections.Generic;
using Avalonia; 
using KomaLab.Models;
using KomaLab.Models.Astrometry; // Assicurati che WcsProjectionType sia visibile qui

namespace KomaLab.Services.Astrometry;

public class WcsTransformation
{
    private readonly WcsData _data;
    
    private double _invCd1_1, _invCd1_2, _invCd2_1, _invCd2_2;
    private bool _matrixInverted;
    
    // Flag per gestire la variante TPV di SCAMP (Assi scambiati)
    private bool _swapAxis2Terms;

    public WcsTransformation(WcsData data)
    {
        _data = data;
        CalculateInverseMatrix();
        AnalyzeTpvCoefficients();
    }

    private void CalculateInverseMatrix()
    {
        double det = (_data.Cd1_1 * _data.Cd2_2) - (_data.Cd1_2 * _data.Cd2_1);
        // Tolleranza più stretta per evitare singolarità numeriche
        if (Math.Abs(det) < 1e-15) return; 

        _invCd1_1 =  _data.Cd2_2 / det;
        _invCd1_2 = -_data.Cd1_2 / det;
        _invCd2_1 = -_data.Cd2_1 / det;
        _invCd2_2 =  _data.Cd1_1 / det;
        _matrixInverted = true;
    }

    /// <summary>
    /// Analizza i coefficienti PV per capire se siamo nel caso "SCAMP TPV"
    /// dove per l'asse 2, il termine 1 è Y invece di X.
    /// </summary>
    private void AnalyzeTpvCoefficients()
    {
        // CORREZIONE: Uso dell'Enum invece della stringa "TPV"
        if (_data.ProjectionType != WcsProjectionType.Tpv) return;

        double pv1_1 = GetPv(1, 1); // Coeff X per asse 1
        double pv2_1 = GetPv(2, 1); // Coeff X per asse 2
        double pv2_2 = GetPv(2, 2); // Coeff Y per asse 2

        // CASO SINGOLARE:
        // Se PV1_1 è grande (~1.0) e PV2_1 è grande (~1.0), mentre PV2_2 è piccolo (~0),
        // allora entrambi gli assi dipendono da X. L'immagine collassa in una linea.
        // Questo implica che per l'asse 2, il "Termine 1" è in realtà Y.
        if (Math.Abs(pv1_1) > 0.5 && Math.Abs(pv2_1) > 0.5 && Math.Abs(pv2_2) < 0.1)
        {
            _swapAxis2Terms = true;
            System.Diagnostics.Debug.WriteLine("[WCS] Rilevata variante SCAMP TPV: Attivato Swap X/Y per Asse 2.");
        }
    }

    // ==========================================
    // INVERSE: RA/DEC (World) -> X/Y (Pixel)
    // ==========================================
    public Point? WorldToPixel(double raDeg, double decDeg)
    {
        if (!_data.IsValid || !_matrixInverted) return null;

        var idealPlane = ProjectRaDecToPlane(raDeg, decDeg);
        if (idealPlane == null) return null;

        double xi_target = idealPlane.Value.X;
        double eta_target = idealPlane.Value.Y;

        Point rawPlane;
        
        // CORREZIONE: Uso dell'Enum
        if (_data.ProjectionType == WcsProjectionType.Tpv)
        {
            // Solver Newton-Raphson per distorsione
            rawPlane = SolveDistortionInverseNewton(xi_target, eta_target);
        }
        else
        {
            rawPlane = new Point(xi_target, eta_target);
        }

        // Matrice Inversa CD
        double u = (_invCd1_1 * rawPlane.X) + (_invCd1_2 * rawPlane.Y);
        double v = (_invCd2_1 * rawPlane.X) + (_invCd2_2 * rawPlane.Y);

        // Offset CRPIX (0-based)
        return new Point(
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

        double u = (x + 1.0) - _data.RefPixelX;
        double v = (y + 1.0) - _data.RefPixelY;

        double rawXi = (_data.Cd1_1 * u) + (_data.Cd1_2 * v);
        double rawEta = (_data.Cd2_1 * u) + (_data.Cd2_2 * v);

        Point distortedPlane;
        
        // CORREZIONE: Uso dell'Enum
        if (_data.ProjectionType == WcsProjectionType.Tpv)
        {
            distortedPlane = ApplyTpvPolynomial(rawXi, rawEta);
        }
        else
        {
            distortedPlane = new Point(rawXi, rawEta);
        }

        return DeprojectStandardPlane(distortedPlane.X, distortedPlane.Y);
    }

    // --- Helpers Trigonometrici Standard ---
    private Point? ProjectRaDecToPlane(double raDeg, double decDeg)
    {
        double rad = Math.PI / 180.0;
        double deg = 180.0 / Math.PI;
        double ra = raDeg * rad;
        double dec = decDeg * rad;
        double ra0 = _data.RefRaDeg * rad;
        double dec0 = _data.RefDecDeg * rad;
        double dRa = ra - ra0;
        
        double num = Math.Cos(dec) * Math.Sin(dRa);
        double den = (Math.Sin(dec) * Math.Sin(dec0)) + (Math.Cos(dec) * Math.Cos(dec0) * Math.Cos(dRa));

        if (Math.Abs(den) < 1e-12) return null;

        double xi = (num / den) * deg;
        double eta = (((Math.Sin(dec) * Math.Cos(dec0)) - (Math.Cos(dec) * Math.Sin(dec0) * Math.Cos(dRa))) / den) * deg;

        return new Point(xi, eta);
    }

    private (double Ra, double Dec)? DeprojectStandardPlane(double xiDeg, double etaDeg)
    {
        double rad = Math.PI / 180.0;
        double deg = 180.0 / Math.PI;
        double xi = xiDeg * rad;
        double eta = etaDeg * rad;
        double ra0 = _data.RefRaDeg * rad;
        double dec0 = _data.RefDecDeg * rad;

        double r = Math.Sqrt(xi * xi + eta * eta);
        if (r < 1e-12) return (_data.RefRaDeg, _data.RefDecDeg);

        double beta = Math.Atan2(r, 1.0);
        double sinDec = (Math.Cos(beta) * Math.Sin(dec0)) + ((eta * Math.Sin(beta) * Math.Cos(dec0)) / r);
        if (sinDec > 1.0) sinDec = 1.0;
        if (sinDec < -1.0) sinDec = -1.0;

        double dec = Math.Asin(sinDec);
        double yTerm = (Math.Cos(beta) * Math.Cos(dec0)) - ((eta * Math.Sin(beta) * Math.Sin(dec0)) / r);
        double xTerm = (xi * Math.Sin(beta)) / r;
        
        double dRa = Math.Atan2(xTerm, yTerm);
        double raFinal = (ra0 + dRa) * deg;
        double decFinal = dec * deg;

        while (raFinal < 0) raFinal += 360.0;
        while (raFinal >= 360.0) raFinal -= 360.0;

        return (raFinal, decFinal);
    }

    // --- NEWTON-RAPHSON SOLVER ---
    private Point SolveDistortionInverseNewton(double targetXi, double targetEta)
    {
        double xi = targetXi;
        double eta = targetEta;

        for (int i = 0; i < 20; i++)
        {
            double r = Math.Sqrt(xi * xi + eta * eta);
            double currXi = ComputePolySum(1, xi, eta, r);
            double currEta = ComputePolySum(2, xi, eta, r);

            double f = currXi - targetXi;
            double g = currEta - targetEta;

            if (Math.Abs(f) < 1e-9 && Math.Abs(g) < 1e-9) break;

            var d1 = ComputePolyDerivs(1, xi, eta, r);
            var d2 = ComputePolyDerivs(2, xi, eta, r);

            double det = (d1.dXi * d2.dEta) - (d1.dEta * d2.dXi);
            if (Math.Abs(det) < 1e-15) { xi -= f; eta -= g; continue; }

            double deltaXi = (d2.dEta * f - d1.dEta * g) / det;
            double deltaEta = (-d2.dXi * f + d1.dXi * g) / det;

            xi -= deltaXi;
            eta -= deltaEta;
        }

        return new Point(xi, eta);
    }

    private Point ApplyTpvPolynomial(double xi, double eta)
    {
        double r = Math.Sqrt(xi * xi + eta * eta);
        return new Point(ComputePolySum(1, xi, eta, r), ComputePolySum(2, xi, eta, r));
    }

    // Calcolo Polinomio con logica SWAP
    private double ComputePolySum(int axis, double x, double y, double r)
    {
        if (axis == 2 && _swapAxis2Terms)
        {
            double temp = x; x = y; y = temp;
        }

        if (!_data.PvCoefficients.ContainsKey((axis, 1)) && !_data.PvCoefficients.ContainsKey((axis, 0))) 
            return (axis == 1) ? x : y; 

        double sum = 0;
        sum += GetPv(axis, 0);          
        sum += GetPv(axis, 1) * x;      
        sum += GetPv(axis, 2) * y;      
        sum += GetPv(axis, 3) * r;      
        sum += GetPv(axis, 4) * x * x;  
        sum += GetPv(axis, 5) * x * y;  
        sum += GetPv(axis, 6) * y * y;  
        sum += GetPv(axis, 7) * x * x * x; 
        sum += GetPv(axis, 8) * x * x * y; 
        sum += GetPv(axis, 9) * x * y * y; 
        sum += GetPv(axis, 10) * y * y * y;
        return sum;
    }

    // Calcolo Derivate con logica SWAP
    private (double dXi, double dEta) ComputePolyDerivs(int axis, double x, double y, double r)
    {
        bool swapped = (axis == 2 && _swapAxis2Terms);
        if (swapped)
        {
            double temp = x; x = y; y = temp;
        }

        if (!_data.PvCoefficients.ContainsKey((axis, 1)) && !_data.PvCoefficients.ContainsKey((axis, 0))) 
        {
            if (swapped) return (0.0, 1.0); // Se invertito: d/dY=1, d/dX=0
            return (axis == 1) ? (1.0, 0.0) : (0.0, 1.0);
        }

        double d_dx = 0;
        double d_dy = 0;
        double dr_dx = (r < 1e-9) ? 0 : x / r;
        double dr_dy = (r < 1e-9) ? 0 : y / r;

        d_dx += GetPv(axis, 1);
        d_dy += GetPv(axis, 2);
        
        double c3 = GetPv(axis, 3);
        d_dx += c3 * dr_dx; d_dy += c3 * dr_dy;

        d_dx += GetPv(axis, 4) * 2 * x;
        
        double c5 = GetPv(axis, 5);
        d_dx += c5 * y; d_dy += c5 * x;

        d_dy += GetPv(axis, 6) * 2 * y;
        d_dx += GetPv(axis, 7) * 3 * x * x;
        
        double c8 = GetPv(axis, 8);
        d_dx += c8 * 2 * x * y; d_dy += c8 * x * x;

        double c9 = GetPv(axis, 9);
        d_dx += c9 * y * y; d_dy += c9 * 2 * x * y;

        d_dy += GetPv(axis, 10) * 3 * y * y;

        if (swapped)
        {
            return (d_dy, d_dx);
        }
        return (d_dx, d_dy);
    }

    private double GetPv(int axis, int k) => _data.PvCoefficients.GetValueOrDefault((axis, k), 0.0);
}