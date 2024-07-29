using FaCT.Rating.POC.Common;

namespace FaCT.Rating.POC.Tests.Data;

internal class Policy<TResult>
{
    public TResult ExpectedResult { get; set; }

    public string PolicyEffective { get; set; }

    public string PolicyExpiration { get; set; }

    public decimal CoverageItemLimit { get; set; }

    public string ConstructionCode { get; set; }

    public string ProtectionClassCode { get; set; }

    public string FarmTypeCode { get; set; }

    public string TheftExclusionIndicator { get; set; }

    public string WindHailExclusionIndicator { get; set; }

    public string VandalismExclusionIndicator { get; set; }

    public decimal IrpmFactor { get; set; }

    public decimal CommissionReduction { get; set; }

    public string BreakageExclusionIndicator { get; set; }

    public decimal ManualPremium { get; set; }

    public string State { get; set; } = Constants.FactorKeyWildcard;

    public string Lob { get; set; } = Constants.FactorKeyWildcard;

    public string InsuranceLine { get; set; } = Constants.FactorKeyWildcard;

    public string Product { get; set; } = Constants.FactorKeyWildcard;

    public string Coverage { get; set; } = Constants.FactorKeyWildcard;

    public string RateBook { get; set; } = Constants.FactorKeyWildcard;

    public DateTime? NbEffectiveDate { get; set; }

    public object[] ToObjectArray() =>
        [
            this.ExpectedResult,
            this.PolicyEffective,
            this.PolicyExpiration,
            this.CoverageItemLimit,
            this.ConstructionCode,
            this.ProtectionClassCode,
            this.FarmTypeCode,
            this.TheftExclusionIndicator,
            this.WindHailExclusionIndicator,
            this.VandalismExclusionIndicator,
            this.IrpmFactor,
            this.CommissionReduction,
            this.BreakageExclusionIndicator,
            this.ManualPremium,
            this.State,
            this.Lob,
            this.InsuranceLine,
            this.Product,
            this.Coverage,
            this.RateBook,
            this.NbEffectiveDate,
        ];
}
