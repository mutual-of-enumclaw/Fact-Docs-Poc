using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling;

public class BreakagePremiumCalculator(
    Settings settings,
    ICalculator<IrpmFactor.Request> irpmFactor,
    ICalculator<CommissionFactor.Request> commissionFactor,
    ICalculator<TermFactor.Request> termFactor) : ICalculator<BreakagePremiumCalculator.Request>
{
    public decimal Calculate(Request request)
    {
        if (request.BreakageExclusionIndicator == "Y")
        {
            return 0;
        }

        var irpm = irpmFactor.Calculate(request.ToRequest<IrpmFactor.Request>(req => req.IrpmFactor = request.IrpmFactor));
        var commission = commissionFactor.Calculate(request.ToRequest<CommissionFactor.Request>());
        var term = termFactor.Calculate(request.ToRequest<TermFactor.Request>());

        var thousandsMultiple = Math.Abs(request.CoverageItemLimit / 1000);
        var baseRate = thousandsMultiple * settings.Farm.FarmDwelling.FineArts.BreakagePremiumRate;

        return Math.Round(baseRate * irpm * commission * term);
    }

    public class Request : BaseFactorRequest
    {
        public required decimal CoverageItemLimit { get; set; }

        public required string BreakageExclusionIndicator { get; set; }

        public required decimal IrpmFactor { get; set; }
    }
}
