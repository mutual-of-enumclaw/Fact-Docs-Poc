using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class FarmTypeFactor(FactorCache<decimal> factorCache) : ICalculator<FarmTypeFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return factorCache.GetFactor(request);
    }

    public class Request : BaseSearchFactorRequest
    {
        public override string KeyValue => FarmTypeCode;

        public string FarmTypeCode { get; set; }
    }
}
