namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class VandalismExclusionFactor(Settings settings) : ICalculator<VandalismExclusionFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return settings.Farm.FarmDwelling.Factors.VandalismExclusion[request.VandalismExclusionIndicator];
    }

    public class Request : BaseFactorRequest
    {
        public string VandalismExclusionIndicator { get; set; }
    }
}
