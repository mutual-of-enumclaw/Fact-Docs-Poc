using FaCT.Rating.POC.Calculators.Farm.FarmDwelling;
using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;
using FaCT.Rating.POC.Common;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling;

public class BreakagePremiumCalculatorTests
{
    private static readonly Settings settings = new()
    {
        Farm = new()
        {
            FarmDwelling = new()
            {
                FineArts = new()
                {
                    BreakagePremiumRate = 1.5m
                }
            }
        }
    };

    private static readonly BreakagePremiumCalculator sut = new(
        settings,
        new IrpmFactor(),
        new CommissionFactor(),
        new TermFactor());

    [Theory]
    [InlineData("20240506", "20250506", 90000, "N", 0.95, 128)]
    [InlineData("20240506", "20250506", 90000, "Y", 0.95, 0)]
    public void Calculate(
        string policyEffective,
        string policyExpiration,
        decimal coverageItemLimit,
        string breakageExclusionIndicator,
        decimal irpmFactor,
        decimal expectedResult,
        string state = Constants.FactorKeyWildcard,
        string lob = Constants.FactorKeyWildcard,
        string insuranceLine = Constants.FactorKeyWildcard,
        string product = Constants.FactorKeyWildcard,
        string coverage = Constants.FactorKeyWildcard,
        string rateBook = Constants.FactorKeyWildcard,
        DateTime? nbEffectiveDate = null)
    {
        var request = new BreakagePremiumCalculator.Request
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
            BreakageExclusionIndicator = breakageExclusionIndicator,
            IrpmFactor = irpmFactor
        };

        var result = sut.Calculate(request);

        Assert.Equal(expectedResult, result);
    }
}
