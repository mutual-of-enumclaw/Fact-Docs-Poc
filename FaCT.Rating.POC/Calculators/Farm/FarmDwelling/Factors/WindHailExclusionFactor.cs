namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class WindHailExclusionFactor(Settings settings) : ICalculator<WindHailExclusionFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return settings.Farm.FarmDwelling.Factors.WindHailExclusion[request.WindHailExclusionIndicator];
    }

    public class Request : BaseFactorRequest
    {
        public string WindHailExclusionIndicator { get; set; }
    }
}
