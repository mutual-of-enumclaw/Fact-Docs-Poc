using FaCT.Rating.POC.Common;

namespace FaCT.Rating.POC.Tests.Data;

internal class PolicyTestData
{
    public static Policy<decimal> GetFinartTestPolicy(
        decimal expectedResult,
        string constructionCode,
        decimal coverageItemLimit,
        decimal manualPremium = 0)
    {
        return new Policy<decimal>
        {
            ExpectedResult = expectedResult,

            State = States.Arizona,
            Lob = LinesOfBusiness.Farm,
            InsuranceLine = InsuranceLines.FarmDwelling,
            Product = Constants.FactorKeyWildcard,
            Coverage = Coverages.FINART,
            RateBook = RateBooks.A,

            PolicyEffective = "20240506",
            PolicyExpiration = "20250506",
            CoverageItemLimit = coverageItemLimit,
            ConstructionCode = constructionCode,
            ProtectionClassCode = ProtectionClassCodes.Code05,
            FarmTypeCode = FarmTypeCodes.TypeI,
            TheftExclusionIndicator = Constants.No,
            WindHailExclusionIndicator = Constants.No,
            VandalismExclusionIndicator = Constants.No,
            IrpmFactor = 0.95m,
            CommissionReduction = Constants.DefaultCommissionReduction,
            BreakageExclusionIndicator = Constants.No,
            ManualPremium = manualPremium
        };
    }
}
