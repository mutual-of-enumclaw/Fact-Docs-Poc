using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;
using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling;

public class FursSppCalculator(
    Settings settings,
    ICalculator<CoverageLimitFactor.Request> coverageLimitFactor,
    ICalculator<FarmTypeFactor.Request> farmTypeFactor,
    ICalculator<TheftExclusionFactor.Request> theftExclusionFactor,
    ICalculator<WindHailExclusionFactor.Request> windHailExclusionFactor,
    ICalculator<VandalismExclusionFactor.Request> vandalismExclusionFactor,
    ICalculator<IrpmFactor.Request> irpmFactor,
    ICalculator<CommissionFactor.Request> commissionFactor,
    ICalculator<TermFactor.Request> termFactor) : ICalculator<FursSppCalculator.Request>
{
    public decimal Calculate(Request request)
    {
        if (request.ManualPremium > 0)
        {
            return request.ManualPremium;
        }

        var coverageLimit = coverageLimitFactor.Calculate(request.ToRequest<CoverageLimitFactor.Request>(req => req.CoverageItemLimit = request.CoverageItemLimit));
        var farmType = farmTypeFactor.Calculate(request.ToRequest<FarmTypeFactor.Request>(req => req.FarmTypeCode = request.FarmTypeCode));
        var theftExclusion = theftExclusionFactor.Calculate(request.ToRequest<TheftExclusionFactor.Request>(req => req.TheftExclusionIndicator = request.TheftExclusionIndicator));
        var windHailExclusion = windHailExclusionFactor.Calculate(request.ToRequest<WindHailExclusionFactor.Request>(req => req.WindHailExclusionIndicator = request.WindHailExclusionIndicator));
        var vandalismExclusion = vandalismExclusionFactor.Calculate(request.ToRequest<VandalismExclusionFactor.Request>(req => req.VandalismExclusionIndicator = request.VandalismExclusionIndicator));
        var irpm = irpmFactor.Calculate(request.ToRequest<IrpmFactor.Request>(req => req.IrpmFactor = request.IrpmFactor));
        var commission = commissionFactor.Calculate(request.ToRequest<CommissionFactor.Request>());
        var term = termFactor.Calculate(request.ToRequest<TermFactor.Request>());

        return Math.Round(
            settings.Farm.FarmDwelling.Furs.BaseRate *
            coverageLimit *
            farmType *
            theftExclusion *
            windHailExclusion *
            vandalismExclusion *
            irpm *
            commission *
            term);
    }

    public class Request : BaseFactorRequest
    {
        public required decimal CoverageItemLimit { get; set; }

        public required string FarmTypeCode { get; set; }

        public required string TheftExclusionIndicator { get; set; }

        public required string WindHailExclusionIndicator { get; set; }

        public required string VandalismExclusionIndicator { get; set; }

        public required decimal IrpmFactor { get; set; }

        public decimal CommissionReduction { get; set; }

        public decimal ManualPremium { get; set; }
    }
}
