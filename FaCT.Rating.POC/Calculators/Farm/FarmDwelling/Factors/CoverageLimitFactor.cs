namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class CoverageLimitFactor : ICalculator<CoverageLimitFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return request.CoverageItemLimit / 1000;
    }

    public class Request : BaseFactorRequest
    {
        public decimal CoverageItemLimit { get; set; }
    }
}
