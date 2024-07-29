using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;
using FaCT.Rating.POC.Common;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class FarmTypeFactorTests
{
    private static readonly FarmTypeFactor sut = new(FactorCacheRepository.GetFarmTypeFactorCache());

    [Theory]
    [InlineData("1", 1.17)]
    [InlineData("2", 1.2)]
    [InlineData("3", 1.02)]
    [InlineData("4", 1.02)]
    public void Calculate(
        string farmTypeCode,
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
        var result = sut.Calculate(new FarmTypeFactor.Request
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
            FarmTypeCode = farmTypeCode
        });

        Assert.Equal(expectedResult, result);
    }
}
