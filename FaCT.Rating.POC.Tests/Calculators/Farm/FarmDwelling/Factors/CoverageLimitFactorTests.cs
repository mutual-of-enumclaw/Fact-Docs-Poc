using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class CoverageLimitFactorTests
{
    private static readonly CoverageLimitFactor sut = new();

    [Theory]
    [InlineData(1000, 1)]
    [InlineData(2000, 2)]
    [InlineData(3000, 3)]
    [InlineData(3500, 3.5)]
    public void Calculate(decimal coverageItemLimit, decimal expectedResult)
    {
        var result = sut.Calculate(new CoverageLimitFactor.Request
        {
            CoverageItemLimit = coverageItemLimit
        });

        Assert.Equal(expectedResult, result);
    }
}
