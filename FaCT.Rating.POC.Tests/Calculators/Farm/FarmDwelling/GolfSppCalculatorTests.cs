using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;
using FaCT.Rating.POC.Calculators.Farm.FarmDwelling;
using FaCT.Rating.POC.Common;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling;

public class GolfSppCalculatorTests
{
    private const decimal DefaultCommissionReduction = 9.99999m;

    private static readonly Settings settings = new()
    {
        Farm = new()
        {
            FarmDwelling = new()
            {
                Golf = new()
                {
                    BaseRate = 20m
                },
                Factors = new()
                {
                    TheftExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.95m },
                        { "N", 1m }
                    },
                    VandalismExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.99m },
                        { "N", 1m }
                    },
                    WindHailExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.9m },
                        { "N", 1m }
                    }
                }
            }
        }
    };

    private static readonly GolfSppCalculator sut = new(
        settings,
        new CoverageLimitFactor(),
        new FarmTypeFactor(FactorCacheRepository.GetFarmTypeFactorCache()),
        new TheftExclusionFactor(settings),
        new WindHailExclusionFactor(settings),
        new VandalismExclusionFactor(settings),
        new IrpmFactor(),
        new CommissionFactor(),
        new TermFactor());

    [Theory]
    [InlineData("20240506", "20250506", 6000, "1", "N", "N", "N", 0.95, 9.99999, 0, 133)]
    [InlineData("20240506", "20250506", 1000, "1", "N", "N", "N", 0.95, 9.99999, 500, 500)]
    public void Calculate(
        string policyEffective,
        string policyExpiration,
        decimal coverageItemLimit,
        string farmTypeCode,
        string theftExclusionIndicator,
        string windHailExclusionIndicator,
        string vandalismExclusionIndicator,
        decimal irpmFactor,
        decimal commissionReduction,
        decimal manualPremium,
        decimal expectedResult,
        string state = Constants.FactorKeyWildcard,
        string lob = Constants.FactorKeyWildcard,
        string insuranceLine = Constants.FactorKeyWildcard,
        string product = Constants.FactorKeyWildcard,
        string coverage = Constants.FactorKeyWildcard,
        string rateBook = Constants.FactorKeyWildcard,
        DateTime? nbEffectiveDate = null)
    {
        var request = new GolfSppCalculator.Request
        {
            State = state,
            LineOfBusiness = lob,
            InsuranceLine = insuranceLine,
            Product = product,
            Coverage = coverage,
            RateBook = rateBook,
            NbEffectiveDate = nbEffectiveDate,
            PolicyEffective = policyEffective,
            PolicyExpiration = policyExpiration,
            CoverageItemLimit = coverageItemLimit,
            FarmTypeCode = farmTypeCode,
            TheftExclusionIndicator = theftExclusionIndicator,
            WindHailExclusionIndicator = windHailExclusionIndicator,
            VandalismExclusionIndicator = vandalismExclusionIndicator,
            IrpmFactor = irpmFactor,
            CommissionReduction = commissionReduction,
            ManualPremium = manualPremium
        };

        var result = sut.Calculate(request);

        Assert.Equal(expectedResult, result);
    }
}
