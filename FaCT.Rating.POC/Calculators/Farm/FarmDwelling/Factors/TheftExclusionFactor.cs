namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class TheftExclusionFactor(Settings settings) : ICalculator<TheftExclusionFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return settings.Farm.FarmDwelling.Factors.TheftExclusion[request.TheftExclusionIndicator];
    }

    public class Request : BaseFactorRequest
    {
        public string TheftExclusionIndicator { get; set; }
    }
}
