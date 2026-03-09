using System;
using Kometra.Models.Primitives;
using Kometra.Models.Processing.Arithmetic;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class ArithmeticEngine : IArithmeticEngine
{
    private readonly IGeometricEngine _geometricEngine;

    public ArithmeticEngine(IGeometricEngine geometricEngine)
    {
        _geometricEngine = geometricEngine;
    }

    public Mat Execute(Mat matA, Mat matB, ArithmeticOperation op)
    {
        if (matA == null || matB == null) throw new ArgumentNullException("Input Mats cannot be null");

        // 1. DETERMINAZIONE DEL TIPO (Type Promotion)
        // Se uno dei due è 64-bit (Double), il risultato deve essere 64-bit.
        bool useDouble = (matA.Depth() == MatType.CV_64F || matB.Depth() == MatType.CV_64F);
        MatType workingType = useDouble ? MatType.CV_64FC1 : MatType.CV_32FC1;

        // 2. ALLINEAMENTO GEOMETRICO (Center-based)
        // Usiamo A come canvas. Portiamo B alla stessa dimensione di A, centrata.
        Point2D centerB = new Point2D(matB.Width / 2.0, matB.Height / 2.0);
        Size2D sizeA = new Size2D(matA.Width, matA.Height);
        
        using Mat alignedB = _geometricEngine.CropCentered(matB, centerB, sizeA);

        // 3. PREPARAZIONE DATI
        using Mat workingA = new Mat();
        using Mat workingB = new Mat();
        matA.ConvertTo(workingA, workingType);
        alignedB.ConvertTo(workingB, workingType);

        // Maschera di validità (Evitiamo operazioni dove ci sono NaN in A o B)
        using Mat mask = new Mat();
        using Mat maskA = new Mat();
        using Mat maskB = new Mat();
        Cv2.Compare(workingA, workingA, maskA, CmpType.EQ); // Check NaN in A
        Cv2.Compare(workingB, workingB, maskB, CmpType.EQ); // Check NaN in B
        Cv2.BitwiseAnd(maskA, maskB, mask);

        // Inizializziamo il risultato con NaN (rappresenta "nessun dato")
        Mat result = new Mat(matA.Size(), workingType, useDouble ? new Scalar(double.NaN) : new Scalar(float.NaN));

        // 4. ESECUZIONE OPERAZIONE
        switch (op)
        {
            case ArithmeticOperation.Add:
                Cv2.Add(workingA, workingB, result, mask);
                break;
            case ArithmeticOperation.Subtract:
                Cv2.Subtract(workingA, workingB, result, mask);
                break;
            case ArithmeticOperation.Multiply:
                // Moltiplicazione elemento per elemento
                Cv2.Multiply(workingA, workingB, result); 
                // Riapplichiamo NaN dove i dati non erano validi
                using (Mat invMask = new Mat())
                {
                    Cv2.BitwiseNot(mask, invMask);
                    result.SetTo(useDouble ? new Scalar(double.NaN) : new Scalar(float.NaN), invMask);
                }
                break;
            case ArithmeticOperation.Divide:
                // Check divisione per zero
                using (Mat zeroMask = new Mat())
                {
                    Cv2.Compare(workingB, 0, zeroMask, CmpType.EQ);
                    using Mat combinedInvalid = new Mat();
                    Cv2.BitwiseOr(zeroMask, mask, combinedInvalid); // Dove B è 0 o uno dei due è NaN
                    
                    // Applichiamo la divisione solo dove sicuro
                    Cv2.Divide(workingA, workingB, result);
                    
                    // Reset a NaN dove la divisione non era possibile
                    using Mat finalInvalidMask = new Mat();
                    Cv2.Compare(result, result, finalInvalidMask, CmpType.NE); // Trova NaN generati da OpenCV
                    Cv2.BitwiseOr(finalInvalidMask, zeroMask, finalInvalidMask);
                    result.SetTo(useDouble ? new Scalar(double.NaN) : new Scalar(float.NaN), finalInvalidMask);
                }
                break;
        }

        return result;
    }
}