using FaCT.Rating.POC.Common;
using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class DeductibleFactor(
    FactorCache<List<RangeFactor>> factorCache) : ICalculator<DeductibleFactor.Request>
{
    public decimal Calculate(Request request)
    {
        var factor = factorCache.GetFactor(request);

        if (factor == null)
        {
            throw new FactorNotFoundException();
        }

        var rangeFactor = factor.FirstOrDefault(f => f.Min <= request.LocationLimitTotal && f.Max >= request.LocationLimitTotal);

        if (rangeFactor == null)
        {
            throw new FactorNotFoundException();
        }

        return rangeFactor.Factor;
    }

    public class Request : BaseSearchFactorRequest
    {
        public override string KeyValue => base.KeyValue;

        public required string DeductibleCode { get; set; }

        public required decimal LocationLimitTotal { get; set; }
    }
}
