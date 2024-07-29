using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class TheftExclusionFactorTests
{
    private static readonly Settings settings = new()
    {
        Farm = new()
        {
            FarmDwelling = new()
            {
                Factors = new()
                {
                    TheftExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.95m },
                        { "N", 1m }
                    }
                }
            }
        }
    };

    private static readonly TheftExclusionFactor sut = new(settings);

    [Theory]
    [InlineData("Y", 0.95)]
    [InlineData("N", 1)]
    public void Calculate(string theftExclusionIndicator, decimal expectedResult)
    {
        var result = sut.Calculate(new TheftExclusionFactor.Request
        {
            TheftExclusionIndicator = theftExclusionIndicator
        });

        Assert.Equal(expectedResult, result);
    }
}
