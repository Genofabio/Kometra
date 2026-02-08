using System;

namespace KomaLab.Services.Fits.IO
{
    /// <summary>
    /// Traduzione fedele (1:1) di cfitsio/quantize.c.
    /// Implementa la stima del rumore statistico (MAD) e il Subtractive Dithering.
    /// </summary>
    public static class FitsQuantizerHighLevel
    {
        // =========================================================
        // COSTANTI E MACRO CFITSIO
        // =========================================================
        
        // Nearest Integer Function: ((x >= 0.) ? (int) (x + 0.5) : (int) (x - 0.5))
        private static int NINT(double x) => x >= 0.0 ? (int)(x + 0.5) : (int)(x - 0.5);

        public const int NULL_VALUE = -2147483647; // Rappresenta i pixel NaN
        public const int ZERO_VALUE = -2147483646; // Rappresenta i pixel zero (in dithering 2)
        private const int N_RESERVED_VALUES = 10;
        private const double SIGMA_CLIP = 5.0;
        private const int NITER = 3;
        
        // Dithering Constants
        public const int NO_DITHER = -1;
        public const int SUBTRACTIVE_DITHER_1 = 1;
        public const int SUBTRACTIVE_DITHER_2 = 2;
        private const int N_RANDOM = 10000;
        private static float[] _fitsRandValue; // Buffer statico per i numeri casuali

        static FitsQuantizerHighLevel()
        {
            InitializeRandoms();
        }

        // Simula fits_init_randoms di CFITSIO (Algoritmo Portable)
        // cfitsio/quantize.c: lines 580-620
        private static void InitializeRandoms()
        {
            _fitsRandValue = new float[N_RANDOM];
            
            // Costanti definite in quantize.c
            double a = 16807.0;
            double m = 2147483647.0;
            double seed = 1.0; // Seed iniziale fisso standard
            double temp;

            for (int i = 0; i < N_RANDOM; i++)
            {
                temp = a * seed;
                seed = temp - m * (int)(temp / m);
                _fitsRandValue[i] = (float)(seed / m);
            }
        }

        // =========================================================
        // ENTRY POINTS PRINCIPALI (FLOAT & DOUBLE)
        // =========================================================

        public static bool QuantizeFloat(
            long row, float[] fdata, int nxpix, int nypix, 
            bool nullcheck, float in_null_value, float qlevel, int dither_method,
            out int[] idata, out double bscale, out double bzero, 
            out int iminval, out int imaxval)
        {
            idata = new int[fdata.Length];
            long ngood = 0;
            double stdev;
            double noise2 = 0, noise3 = 0, noise5 = 0;
            float minval = 0f, maxval = 0f;
            double delta, zeropt;
            int nextrand = 0;
            int iseed = 0;

            long nx = (long)nxpix * nypix;
            
            // 1. Caso banale (immagine vuota o 1 pixel)
            if (nx <= 1)
            {
                bscale = 1.0; bzero = 0.0;
                iminval = 0; imaxval = 0;
                return false;
            }

            // 2. Calcolo Statistiche Rumore (Se qlevel >= 0)
            if (qlevel >= 0f)
            {
                // Stima rumore usando differenze di ordine 5 (FnNoise5)
                FnNoise5_Float(fdata, nxpix, nypix, nullcheck, in_null_value, 
                               out ngood, out minval, out maxval, 
                               out noise2, out noise3, out noise5);

                if (nullcheck && ngood == 0)
                {
                    minval = 0f; maxval = 1f; stdev = 1.0;
                }
                else
                {
                    // Usa il minimo tra i rumori stimati
                    stdev = noise3;
                    if (noise2 != 0.0 && noise2 < stdev) stdev = noise2;
                    if (noise5 != 0.0 && noise5 < stdev) stdev = noise5;
                }

                if (qlevel == 0f) delta = stdev / 4.0;
                else delta = stdev / qlevel;

                if (delta == 0.0)
                {
                    bscale = 1.0; bzero = 0.0; iminval = 0; imaxval = 0;
                    return false; // Don't quantize
                }
            }
            else
            {
                // qlevel negativo: Livello assoluto imposto
                delta = -qlevel;
                // Calcoliamo solo min/max
                FnNoise3_Float(fdata, nxpix, nypix, nullcheck, in_null_value,
                               out ngood, out minval, out maxval, out _);
            }

            // 3. Verifica Range Interi
            // Formula cfitsio: (max - min) / delta > 2 * INT_MAX - RESERVED
            if ((maxval - minval) / delta > 2.0 * 2147483647.0 - N_RESERVED_VALUES)
            {
                bscale = 1.0; bzero = 0.0; iminval = 0; imaxval = 0;
                return false; // Range troppo grande per int32
            }

            // 4. Inizializzazione Dithering
            if (row > 0)
            {
                // Row è usato come seed base per variare il pattern tra le righe
                iseed = (int)((row - 1) % N_RANDOM);
                nextrand = (int)(_fitsRandValue[iseed] * 500.0);
            }

            // 5. Calcolo Zero Point (bzero)
            if (ngood == nx) // Nessun NULL
            {
                if (dither_method == SUBTRACTIVE_DITHER_2)
                {
                    zeropt = minval - delta * (NULL_VALUE + N_RESERVED_VALUES);
                }
                else if ((maxval - minval) / delta < 2147483647.0 - N_RESERVED_VALUES)
                {
                    zeropt = minval;
                    // "Fudge" per allineare zeropt a un multiplo di delta (per consistenza)
                    long iqfactor = (long)(zeropt / delta + 0.5);
                    zeropt = iqfactor * delta;
                }
                else
                {
                    zeropt = (minval + maxval) / 2.0;
                }
            }
            else // Ci sono NULL
            {
                zeropt = minval - delta * (NULL_VALUE + N_RESERVED_VALUES);
            }

            // 6. LOOP DI QUANTIZZAZIONE
            for (int i = 0; i < nx; i++)
            {
                float val = fdata[i];
                // Check NaN e valori speciali
                bool isNull = nullcheck && (val == in_null_value || float.IsNaN(val));

                if (!isNull)
                {
                    if (dither_method == SUBTRACTIVE_DITHER_2 && val == 0.0f)
                    {
                        idata[i] = ZERO_VALUE;
                    }
                    else
                    {
                        if (row > 0) // Applica Dithering
                        {
                            // FORMULA CHIAVE DI FPACK:
                            // Value = NINT( (Val - Zero)/Delta + RND - 0.5 )
                            double dithered = ((double)val - zeropt) / delta + _fitsRandValue[nextrand] - 0.5;
                            idata[i] = NINT(dithered);
                        }
                        else // No Dithering
                        {
                            idata[i] = NINT(((double)val - zeropt) / delta);
                        }
                    }
                }
                else
                {
                    idata[i] = NULL_VALUE;
                }

                // Avanzamento Random
                if (row > 0)
                {
                    nextrand++;
                    if (nextrand == N_RANDOM)
                    {
                        iseed++;
                        if (iseed == N_RANDOM) iseed = 0;
                        nextrand = (int)(_fitsRandValue[iseed] * 500.0);
                    }
                }
            }

            // 7. Finalizzazione
            bscale = delta;
            bzero = zeropt;
            iminval = NINT((minval - zeropt) / delta);
            imaxval = NINT((maxval - zeropt) / delta);

            return true;
        }

        // =========================================================
        // HELPERS STATISTICI (Porting di FnNoise5, FnNoise3, etc.)
        // =========================================================

        // --- FnNoise5 (Stima rumore ordine 5) ---
        private static void FnNoise5_Float(float[] array, int nx, int ny, bool nullcheck, float nullvalue,
            out long ngood, out float minval, out float maxval,
            out double noise2, out double noise3, out double noise5)
        {
            // Inizializzazione output
            ngood = 0; minval = float.MaxValue; maxval = float.MinValue;
            noise2 = 0; noise3 = 0; noise5 = 0;

            if (nx < 9) return; // Troppo piccoli per Noise5

            // Array temporanei
            float[] diffs2 = new float[nx];
            float[] diffs3 = new float[nx];
            float[] diffs5 = new float[nx];
            
            // Array per raccogliere le mediane di ogni riga
            double[] rowMedians2 = new double[ny];
            double[] rowMedians3 = new double[ny];
            double[] rowMedians5 = new double[ny];

            int validRows = 0;
            int validRows2 = 0;
            
            // Loop sulle righe (tiling verticale simulato se ny > 1)
            for (int jj = 0; jj < ny; jj++)
            {
                int rowOffset = jj * nx;
                int nvals = 0, nvals2 = 0;
                long ngoodRow = 0;
                
                // Variabili buffer per i pixel (v1..v9)
                float v1 = 0, v2 = 0, v3 = 0, v4 = 0, v5 = 0, v6 = 0, v7 = 0, v8 = 0, v9 = 0;
                
                // Riempimento iniziale buffer (salta null)
                int ii = 0; 
                
                // Helper locale per leggere prossimo pixel valido
                bool GetNext(ref int idx, out float val) {
                    while (idx < nx) {
                        float p = array[rowOffset + idx];
                        idx++;
                        if (!nullcheck || (p != nullvalue && !float.IsNaN(p))) {
                            val = p; return true;
                        }
                    }
                    val = 0; return false;
                }

                // Carica primi 8 pixel
                if(!GetNext(ref ii, out v1)) continue;
                if(v1 < minval) minval = v1; if(v1 > maxval) maxval = v1; ngoodRow++;
                
                if(!GetNext(ref ii, out v2)) continue;
                if(v2 < minval) minval = v2; if(v2 > maxval) maxval = v2; ngoodRow++;

                if(!GetNext(ref ii, out v3)) continue;
                if(v3 < minval) minval = v3; if(v3 > maxval) maxval = v3; ngoodRow++;
                
                if(!GetNext(ref ii, out v4)) continue;
                if(v4 < minval) minval = v4; if(v4 > maxval) maxval = v4; ngoodRow++;
                
                if(!GetNext(ref ii, out v5)) continue;
                if(v5 < minval) minval = v5; if(v5 > maxval) maxval = v5; ngoodRow++;
                
                if(!GetNext(ref ii, out v6)) continue;
                if(v6 < minval) minval = v6; if(v6 > maxval) maxval = v6; ngoodRow++;

                if(!GetNext(ref ii, out v7)) continue;
                if(v7 < minval) minval = v7; if(v7 > maxval) maxval = v7; ngoodRow++;

                if(!GetNext(ref ii, out v8)) continue;
                if(v8 < minval) minval = v8; if(v8 > maxval) maxval = v8; ngoodRow++;

                // Loop sul resto della riga
                while (GetNext(ref ii, out v9))
                {
                    if(v9 < minval) minval = v9; if(v9 > maxval) maxval = v9;
                    
                    // Calcolo Differenze
                    // Ordine 2: abs(v5 - v7)
                    if (!(v5 == v6 && v6 == v7)) {
                        diffs2[nvals2++] = Math.Abs(v5 - v7);
                    }

                    // Ordine 3 e 5
                    if (!(v3 == v4 && v4 == v5 && v5 == v6 && v6 == v7)) {
                        diffs3[nvals] = Math.Abs((2 * v5) - v3 - v7);
                        diffs5[nvals] = Math.Abs((6 * v5) - (4 * v3) - (4 * v7) + v1 + v9);
                        nvals++;
                    } else {
                        ngoodRow++; // Ignore constant background
                    }

                    // Shift
                    v1=v2; v2=v3; v3=v4; v4=v5; v5=v6; v6=v7; v7=v8; v8=v9;
                }
                
                ngood += (ngoodRow + nvals);

                // Calcolo Mediana per la riga usando QuickSelect
                if (nvals > 0) {
                    rowMedians3[validRows] = QuickSelectFloat(diffs3, nvals);
                    rowMedians5[validRows] = QuickSelectFloat(diffs5, nvals);
                    validRows++;
                }
                if (nvals2 > 0) {
                    rowMedians2[validRows2] = QuickSelectFloat(diffs2, nvals2);
                    validRows2++;
                }
            }

            // Calcolo Mediana Finale delle Mediane di Riga
            if (validRows > 0) {
                Array.Sort(rowMedians3, 0, validRows);
                Array.Sort(rowMedians5, 0, validRows);
                double med3 = (rowMedians3[(validRows-1)/2] + rowMedians3[validRows/2]) / 2.0;
                double med5 = (rowMedians5[(validRows-1)/2] + rowMedians5[validRows/2]) / 2.0;
                
                // Fattori di scala costanti (da cfitsio)
                noise3 = 0.6052697 * med3;
                noise5 = 0.1772048 * med5;
            }

            if (validRows2 > 0) {
                Array.Sort(rowMedians2, 0, validRows2);
                double med2 = (rowMedians2[(validRows2-1)/2] + rowMedians2[validRows2/2]) / 2.0;
                noise2 = 1.0483579 * med2;
            }
        }

        // --- FnNoise3 (Stima rumore ordine 3 - e Min/Max) ---
        private static void FnNoise3_Float(float[] array, int nx, int ny, bool nullcheck, float nullvalue,
           out long ngood, out float minval, out float maxval, out double noise)
        {
            // Versione semplificata per calcolare Min/Max quando qlevel < 0
            ngood = 0; minval = float.MaxValue; maxval = float.MinValue; noise = 0;
            
            for (int i = 0; i < array.Length; i++)
            {
                float val = array[i];
                if (nullcheck && (val == nullvalue || float.IsNaN(val))) continue;
                
                if (val < minval) minval = val;
                if (val > maxval) maxval = val;
                ngood++;
            }
        }

        // =========================================================
        // QUICK SELECT ALGORITHM (Porting di quick_select_float)
        // =========================================================
        private static float QuickSelectFloat(float[] arr, int n)
        {
            int low = 0, high = n - 1;
            int median = (low + high) / 2;
            
            while (true)
            {
                if (high <= low) return arr[median];
                if (high == low + 1)
                {
                    if (arr[low] > arr[high]) Swap(arr, low, high);
                    return arr[median];
                }

                int middle = (low + high) / 2;
                if (arr[middle] > arr[high]) Swap(arr, middle, high);
                if (arr[low] > arr[high]) Swap(arr, low, high);
                if (arr[middle] > arr[low]) Swap(arr, middle, low);

                Swap(arr, middle, low + 1);
                int ll = low + 1;
                int hh = high;

                while (true)
                {
                    do ll++; while (arr[low] > arr[ll]);
                    do hh--; while (arr[hh] > arr[low]);
                    if (hh < ll) break;
                    Swap(arr, ll, hh);
                }
                Swap(arr, low, hh);
                if (hh <= median) low = ll;
                if (hh >= median) high = hh - 1;
            }
        }

        private static void Swap(float[] arr, int a, int b) { float t = arr[a]; arr[a] = arr[b]; arr[b] = t; }
    }
}