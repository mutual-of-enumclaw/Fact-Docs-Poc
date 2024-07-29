using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class WindHailExclusionFactorTests
{
    private static readonly Settings settings = new()
    {
        Farm = new()
        {
            FarmDwelling = new()
            {
                Factors = new()
                {
                    WindHailExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.9m },
                        { "N", 1m }
                    }
                }
            }
        }
    };

    private static readonly WindHailExclusionFactor sut = new(settings);

    [Theory]
    [InlineData("Y", 0.9)]
    [InlineData("N", 1)]
    public void Calculate(string windHailExclusionIndicator, decimal expectedResult)
    {
        var result = sut.Calculate(new WindHailExclusionFactor.Request
        {
            WindHailExclusionIndicator = windHailExclusionIndicator
        });

        Assert.Equal(expectedResult, result);
    }
}
