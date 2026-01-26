using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public class StructureShapeEngine : IStructureShapeEngine
{
    private readonly SemaphoreSlim _memSemaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));

    // =========================================================
    // 1. FRANGI VESSELNESS
    // =========================================================
    public async Task ApplyFrangiVesselnessAsync(Mat src, Mat dst, double sigma, double beta, double c, IProgress<double> progress = null)
    {
        await _memSemaphore.WaitAsync();
        try {
            await Task.Run(() => {
                dst.Create(src.Size(), src.Type());
                
                // Conversione temporanea a 64-bit per precisione nei calcoli delle derivate (Sobel)
                using Mat work = new Mat();
                src.ConvertTo(work, MatType.CV_64F); 

                using Mat dxx = new Mat(); using Mat dyy = new Mat(); using Mat dxy = new Mat();
                
                // Calcolo Hessiana (Gaussian Smoothing + Derivate Seconde)
                int kSize = (int)Math.Ceiling(sigma * 3) * 2 + 1;
                Cv2.GaussianBlur(work, work, new Size(kSize, kSize), sigma);
                Cv2.Sobel(work, dxx, MatType.CV_64F, 2, 0, 3);
                Cv2.Sobel(work, dyy, MatType.CV_64F, 0, 2, 3);
                Cv2.Sobel(work, dxy, MatType.CV_64F, 1, 1, 3);

                // Dispatching dinamico: calcoliamo in double, salviamo nel formato originale (T)
                if (src.Depth() == MatType.CV_64F) RunFrangiCore<double>(src, dxx, dyy, dxy, dst, beta, c);
                else RunFrangiCore<float>(src, dxx, dyy, dxy, dst, beta, c);
                
                progress?.Report(100);
            });
        }
        finally { _memSemaphore.Release(); }
    }

    // =========================================================
    // 2. STRUCTURE TENSOR COHERENCE
    // =========================================================
    public async Task ApplyStructureTensorEnhancementAsync(Mat src, Mat dst, int sigma, int rho, IProgress<double> progress = null)
    {
        await _memSemaphore.WaitAsync();
        try {
            await Task.Run(() => {
                dst.Create(src.Size(), src.Type());
                
                // Calcolo del Tensore di Struttura (J = Grad * Grad^T smussato)
                using Mat dx = new Mat(); using Mat dy = new Mat();
                Cv2.Sobel(src, dx, src.Type(), 1, 0, 3);
                Cv2.Sobel(src, dy, src.Type(), 0, 1, 3);

                using Mat dxx = new Mat(); using Mat dyy = new Mat(); using Mat dxy = new Mat();
                Cv2.Multiply(dx, dx, dxx); 
                Cv2.Multiply(dy, dy, dyy); 
                Cv2.Multiply(dx, dy, dxy);

                // Smoothing del tensore (integrazione locale)
                int kSize = (rho * 2) | 1;
                Cv2.GaussianBlur(dxx, dxx, new Size(kSize, kSize), sigma);
                Cv2.GaussianBlur(dyy, dyy, new Size(kSize, kSize), sigma);
                Cv2.GaussianBlur(dxy, dxy, new Size(kSize, kSize), sigma);

                if (src.Depth() == MatType.CV_64F) RunTensorCoherenceCore<double>(src, dxx, dyy, dxy, dst);
                else RunTensorCoherenceCore<float>(src, dxx, dyy, dxy, dst);
                
                progress?.Report(100);
            });
        } finally { _memSemaphore.Release(); }
    }

    // =========================================================
    // 3. WHITE TOP-HAT
    // =========================================================
    public async Task ApplyWhiteTopHatAsync(Mat src, Mat dst, int kernelSize)
    {
        await Task.Run(() => {
            using Mat blurred = new Mat();
            // Pre-smoothing leggero per ridurre il rumore puntiforme
            Cv2.GaussianBlur(src, blurred, new Size(3, 3), 0.8);
            
            int k = Math.Max(3, kernelSize | 1); 
            using Mat element = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
            
            // TopHat = Src - Open(Src). Estrae dettagli luminosi più piccoli dell'elemento strutturante.
            Cv2.MorphologyEx(blurred, dst, MorphTypes.TopHat, element);
            
            // Assicuriamoci di non avere valori negativi (anche se TopHat è tipicamente positivo)
            Cv2.Max(dst, Scalar.All(0), dst);
        });
    }

    // =========================================================
    // PRIVATE HELPERS (GENERICS)
    // =========================================================
    
    private void RunFrangiCore<T>(Mat src, Mat dxx, Mat dyy, Mat dxy, Mat dst, double beta, double c) where T : struct
    {
        var idxXX = dxx.GetGenericIndexer<double>(); 
        var idxYY = dyy.GetGenericIndexer<double>();
        var idxXY = dxy.GetGenericIndexer<double>(); 
        var idxDst = dst.GetGenericIndexer<T>();
        
        double betaSq = 2.0 * beta * beta;
        double cSq = 2.0 * c * c + 1e-9; // Safety fix per evitare division by zero

        Parallel.For(0, src.Rows, y => {
            for (int x = 0; x < src.Cols; x++) {
                double fxx = idxXX[y, x]; double fyy = idxYY[y, x]; double fxy = idxXY[y, x];
                
                // Calcolo Autovalori Hessiana 2x2 analiticamente
                double trace = fxx + fyy;
                double det = fxx * fyy - fxy * fxy;
                double disc = Math.Sqrt(Math.Max(0, trace * trace - 4.0 * det));
                double l1 = (trace + disc) / 2.0; 
                double l2 = (trace - disc) / 2.0;
                
                // Ordina per magnitudine (|l1| < |l2|)
                if (Math.Abs(l1) > Math.Abs(l2)) (l1, l2) = (l2, l1);
                
                // Per strutture brillanti su fondo scuro, l2 deve essere negativo (curvatura convessa)
                if (l2 >= 0) { idxDst[y, x] = default; continue; }
                
                double Rb = Math.Abs(l1) / Math.Abs(l2); 
                double S = Math.Sqrt(l1 * l1 + l2 * l2); 
                
                double vesselness = Math.Exp(-(Rb * Rb) / betaSq) * (1.0 - Math.Exp(-(S * S) / cSq));
                
                idxDst[y, x] = (T)Convert.ChangeType(vesselness, typeof(T));
            }
        });
    }

    private void RunTensorCoherenceCore<T>(Mat src, Mat dxx, Mat dyy, Mat dxy, Mat dst) where T : struct
    {
        var idxXX = dxx.GetGenericIndexer<T>(); 
        var idxYY = dyy.GetGenericIndexer<T>();
        var idxXY = dxy.GetGenericIndexer<T>(); 
        var idxSrc = src.GetGenericIndexer<T>();
        var idxDst = dst.GetGenericIndexer<T>();

        Parallel.For(0, src.Rows, y => {
            for (int x = 0; x < src.Cols; x++) {
                double j11 = Convert.ToDouble(idxXX[y, x]);
                double j22 = Convert.ToDouble(idxYY[y, x]);
                double j12 = Convert.ToDouble(idxXY[y, x]);
                
                // Autovalori del Structure Tensor
                double trace = j11 + j22;
                double det = j11 * j22 - j12 * j12;
                double sqrtTerm = Math.Sqrt(Math.Max(0, Math.Pow(trace / 2.0, 2) - det));
                double l1 = (trace / 2.0) + sqrtTerm;
                double l2 = (trace / 2.0) - sqrtTerm;
                
                // Coherence = (l1-l2)/(l1+l2)^2. Misura l'anisotropia locale.
                double coherence = (l1 + l2) > 1e-12 ? Math.Pow((l1 - l2) / (l1 + l2), 2) : 0;
                
                // Moduliamo l'intensità originale con la coerenza
                double result = Convert.ToDouble(idxSrc[y, x]) * coherence;
                idxDst[y, x] = (T)Convert.ChangeType(result, typeof(T));
            }
        });
    }
}