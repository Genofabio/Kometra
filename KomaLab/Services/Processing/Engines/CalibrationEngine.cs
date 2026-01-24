using System;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public class CalibrationEngine : ICalibrationEngine
{
    public Mat ApplyCalibration(Mat light, Mat? masterDark, Mat? masterFlat, Mat? masterBias)
    {
        if (light == null) throw new ArgumentNullException(nameof(light));

        // 1. RILEVAMENTO TIPO (Coerenza con FitsOpenCvConverter)
        // Se l'input è già 64-bit (Double), lavoriamo a 64-bit.
        // Altrimenti usiamo 32-bit (Float).
        MatType workingType = (light.Depth() == MatType.CV_64F) 
            ? MatType.CV_64FC1 
            : MatType.CV_32FC1;

        Mat workingMat = new Mat();
        light.ConvertTo(workingMat, workingType);

        // 2. SOTTRAZIONE DARK / BIAS
        if (masterDark != null)
        {
            using Mat darkConv = new Mat();
            masterDark.ConvertTo(darkConv, workingType);
            Cv2.Subtract(workingMat, darkConv, workingMat);
        }
        else if (masterBias != null)
        {
            using Mat biasConv = new Mat();
            masterBias.ConvertTo(biasConv, workingType);
            Cv2.Subtract(workingMat, biasConv, workingMat);
        }

        // 3. CORREZIONE FLAT FIELD
        if (masterFlat != null)
        {
            using Mat flatConv = new Mat();
            masterFlat.ConvertTo(flatConv, workingType);

            // Sottrazione Bias dal Flat (fondamentale per la linearità)
            if (masterBias != null)
            {
                using Mat biasForFlat = new Mat();
                masterBias.ConvertTo(biasForFlat, workingType);
                Cv2.Subtract(flatConv, biasForFlat, flatConv);
            }

            // Normalizzazione: Flat = Flat / Mean
            Scalar meanScalar = Cv2.Mean(flatConv);
            double meanValue = meanScalar.Val0;

            if (meanValue > 0)
            {
                Cv2.Divide(flatConv, meanValue, flatConv);

                // Divisione finale con soglia di sicurezza
                // 0.0001 è sufficiente sia per Float che per Double
                Cv2.Max(flatConv, 0.0001, flatConv); 
                Cv2.Divide(workingMat, flatConv, workingMat);
            }
        }

        // 4. Cleanup valori negativi (clipping a zero)
        Cv2.Max(workingMat, 0, workingMat);

        return workingMat;
    }
}