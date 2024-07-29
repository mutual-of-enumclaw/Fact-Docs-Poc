using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class VandalismExclusionFactorTests
{
    private static readonly Settings settings = new()
    {
        Farm = new()
        {
            FarmDwelling = new()
            {
                Factors = new()
                {
                    VandalismExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.99m },
                        { "N", 1m }
                    }
                }
            }
        }
    };

    private static readonly VandalismExclusionFactor sut = new(settings);

    [Theory]
    [InlineData("Y", 0.99)]
    [InlineData("N", 1)]
    public void Calculate(string vandalismExclusionIndicator, decimal expectedResult)
    {
        var result = sut.Calculate(new VandalismExclusionFactor.Request
        {
            VandalismExclusionIndicator = vandalismExclusionIndicator
        });

        Assert.Equal(expectedResult, result);
    }
}
