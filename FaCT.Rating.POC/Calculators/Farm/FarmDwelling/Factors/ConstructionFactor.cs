using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class ConstructionFactor(FactorCache<decimal> factorCache) : ICalculator<ConstructionFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return factorCache.GetFactor(request);
    }

    public class Request : BaseSearchFactorRequest
    {
        public override string KeyValue => ConstructionCode;

        public string ConstructionCode { get; set; }
    }
}
