using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Arithmetic;

namespace Kometra.Services.Processing.Coordinators;

public interface IArithmeticCoordinator
{
    // Verifica se le due liste sono compatibili secondo le regole di business
    bool CanProcess(List<FitsFileReference> listA, List<FitsFileReference> listB);
    
    // Esegue l'operazione
    Task<List<string>> ProcessAsync(List<FitsFileReference> listA, List<FitsFileReference> listB, ArithmeticOperation op);
}