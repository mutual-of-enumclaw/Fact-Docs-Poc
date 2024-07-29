using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;
using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling;

public class FinartSppCalculator(
    Settings settings,
    ICalculator<ConstructionFactor.Request> constructionFactor,
    ICalculator<ProtectionClassFactor.Request> protectionClassFactor,
    ICalculator<FarmTypeFactor.Request> farmTypeFactor,
    ICalculator<TheftExclusionFactor.Request> theftExclusionFactor,
    ICalculator<WindHailExclusionFactor.Request> windHailExclusionFactor,
    ICalculator<VandalismExclusionFactor.Request> vandalismExclusionFactor,
    ICalculator<IrpmFactor.Request> irpmFactor,
    ICalculator<CommissionFactor.Request> commissionFactor,
    ICalculator<TermFactor.Request> termFactor,
    ICalculator<BreakagePremiumCalculator.Request> breakagePremiumCalculator) : ICalculator<FinartSppCalculator.Request>
{
    public decimal Calculate(Request request)
    {
        if (request.ManualPremium > 0)
        {
            return request.ManualPremium;
        }

        var baseRate = GetBaseRate(request);
        var construction = constructionFactor.Calculate(request.ToRequest<ConstructionFactor.Request>(req => req.ConstructionCode = request.ConstructionCode));
        var protectionClass = protectionClassFactor.Calculate(request.ToRequest<ProtectionClassFactor.Request>(req => req.ProtectionClassCode = request.ProtectionClassCode));
        var farmType = farmTypeFactor.Calculate(request.ToRequest<FarmTypeFactor.Request>(req => req.FarmTypeCode = request.FarmTypeCode));
        var theftExclusion = theftExclusionFactor.Calculate(request.ToRequest<TheftExclusionFactor.Request>(req => req.TheftExclusionIndicator = request.TheftExclusionIndicator));
        var windHailExclusion = windHailExclusionFactor.Calculate(request.ToRequest<WindHailExclusionFactor.Request>(req => req.WindHailExclusionIndicator = request.WindHailExclusionIndicator));
        var vandalismExclusion = vandalismExclusionFactor.Calculate(request.ToRequest<VandalismExclusionFactor.Request>(req => req.VandalismExclusionIndicator = request.VandalismExclusionIndicator));
        var irpm = irpmFactor.Calculate(request.ToRequest<IrpmFactor.Request>(req => req.IrpmFactor = request.IrpmFactor));
        var commission = commissionFactor.Calculate(request.ToRequest<CommissionFactor.Request>());
        var term = termFactor.Calculate(request.ToRequest<TermFactor.Request>());

        var breakagePremium = breakagePremiumCalculator.Calculate(new()
        {
            State = request.State,
            LineOfBusiness = request.LineOfBusiness,
            InsuranceLine = request.InsuranceLine,
            Product = request.Product,
            Coverage = request.Coverage,
            RateBook = request.RateBook,
            NbEffectiveDate = request.NbEffectiveDate,
            PolicyEffective = request.PolicyEffective,
            PolicyExpiration = request.PolicyExpiration,
            CoverageItemLimit = request.CoverageItemLimit,
            BreakageExclusionIndicator = request.BreakageExclusionIndicator,
            IrpmFactor = request.IrpmFactor
        });

        return Math.Round(
            (baseRate *
            construction *
            protectionClass *
            farmType *
            theftExclusion *
            windHailExclusion *
            vandalismExclusion *
            irpm *
            commission *
            term) + breakagePremium);
    }

    private decimal GetBaseRate(Request request)
    {
        if (request.CoverageItemLimit > settings.Farm.FarmDwelling.FineArts.UpperLimit)
        {
            return 0;
        }

        if (request.CoverageItemLimit == settings.Farm.FarmDwelling.FineArts.UpperLimit)
        {
            return settings.Farm.FarmDwelling.FineArts.UpperRate;
        }

        // Find the index of the target value or the closest greater value
        var limitArray = settings.Farm.FarmDwelling.FineArts.Factors.Rate.Keys.ToArray();
        int index = Array.FindIndex(limitArray, x => x >= request.CoverageItemLimit);

        if (index == -1 || index == 0)
        {
            index = limitArray.Length - 1;
        }
        else
        {
            index--;
        }

        var rateArray = settings.Farm.FarmDwelling.FineArts.Factors.Rate.Values.ToArray();

        // Get the sub-array for forecasting
        decimal[] yValues = rateArray.Skip(index).Take(2).ToArray();
        decimal[] xValues = limitArray.Skip(index).Take(2).ToArray();

        return request.CoverageItemLimit.Forecast(yValues, xValues);
    }

    public class Request : BaseFactorRequest
    {
        public required decimal CoverageItemLimit { get; set; }

        public required string ConstructionCode { get; set; }

        public required string ProtectionClassCode { get; set; }

        public required string FarmTypeCode { get; set; }

        public required string TheftExclusionIndicator { get; set; }

        public required string WindHailExclusionIndicator { get; set; }

        public required string VandalismExclusionIndicator { get; set; }

        public required decimal IrpmFactor { get; set; }

        public decimal CommissionReduction { get; set; }

        public required string BreakageExclusionIndicator { get; set; }

        public decimal ManualPremium { get; set; }
    }
}
