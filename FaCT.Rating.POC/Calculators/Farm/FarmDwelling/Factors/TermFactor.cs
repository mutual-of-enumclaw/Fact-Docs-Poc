namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class TermFactor : ICalculator<TermFactor.Request>
{
    public decimal Calculate(Request request)
    {
        DateTime expirationDate = DateTime.ParseExact(request.PolicyExpiration, "yyyyMMdd", null);
        DateTime effectiveDate = DateTime.ParseExact(request.PolicyEffective, "yyyyMMdd", null);

        double durationInDays = (expirationDate - effectiveDate).TotalDays;
        double durationInYears = durationInDays / 365.0;

        return (decimal)Math.Round(durationInYears, 3);
    }

    public class Request : BaseFactorRequest
    {
    }
}
