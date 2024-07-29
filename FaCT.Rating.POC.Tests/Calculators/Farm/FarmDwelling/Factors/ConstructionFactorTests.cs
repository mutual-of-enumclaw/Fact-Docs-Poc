using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;
using FaCT.Rating.POC.Common;
using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class ConstructionFactorTests
{
    private static readonly ConstructionFactor sut = new ConstructionFactor(FactorCacheRepository.GetConstructionFactorCache());

    [Theory]
    [InlineData("1", 1, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A)]
    [InlineData("L", 1, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A)]
    [InlineData("M", 1.65, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A)]
    [InlineData("2", 0.85, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A)]
    [InlineData("3", 0.55, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A)]
    [InlineData("6", 0.55, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A)]
    [InlineData("M", 1.5, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A, "20240101")]
    [InlineData("M", 1, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A, "20230121")]
    [InlineData("M", 1.5, "AZ", "FRM", "FD", Constants.FactorKeyWildcard, Constants.FactorKeyWildcard, RateBooks.A, "20230121", "20230121", true)]
    public void Calculate(
        string constructionCode,
        decimal expectedResult,
        string state = Constants.FactorKeyWildcard,
        string lob = Constants.FactorKeyWildcard,
        string insuranceLine = Constants.FactorKeyWildcard,
        string product = Constants.FactorKeyWildcard,
        string coverage = Constants.FactorKeyWildcard,
        string rateBook = Constants.FactorKeyWildcard,
        string? effectiveDate = null,
        string? nbEffectiveDate = null,
        bool isNewBusiness = false)
    {
        var result = sut.Calculate(new ConstructionFactor.Request
        {
            State = state,
            LineOfBusiness = lob,
            InsuranceLine = insuranceLine,
            Product = product,
            Coverage = coverage,
            RateBook = rateBook,
            PolicyEffective = effectiveDate.ToPolicyEffective(),
            NbEffectiveDate = nbEffectiveDate.ToNbEffectiveDate(),
            IsNewBusiness = isNewBusiness,
            ConstructionCode = constructionCode
        });

        Assert.Equal(expectedResult, result);
    }
}
