namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class ProtectionClassFactor(Settings settings) : ICalculator<ProtectionClassFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return settings.Farm.FarmDwelling.Factors.ProtectionClass[request.ProtectionClassCode];
    }

    public class Request : BaseFactorRequest
    {
        public string ProtectionClassCode { get; set; }
    }
}
