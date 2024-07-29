using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class ProtectionClassFactorTests
{
    private static readonly Settings settings = new()
    {
        Farm = new()
        {
            FarmDwelling = new()
            {
                Factors = new()
                {
                    ProtectionClass = new Dictionary<string, decimal>
                    {
                        { "01", 0.55m },
                        { "02", 0.55m },
                        { "03", 0.55m },
                        { "04", 0.55m },
                        { "05", 0.55m },
                        { "06", 0.55m },
                        { "07", 0.6m },
                        { "08", 0.65m },
                        { "8B", 0.8m },
                        { "09", 1m },
                        { "10", 1m }
                    }
                }
            }
        }
    };

    private static readonly ProtectionClassFactor sut = new(settings);

    [Theory]
    [InlineData("01", 0.55)]
    [InlineData("02", 0.55)]
    [InlineData("03", 0.55)]
    [InlineData("04", 0.55)]
    [InlineData("05", 0.55)]
    [InlineData("06", 0.55)]
    [InlineData("07", 0.6)]
    [InlineData("08", 0.65)]
    [InlineData("8B", 0.8)]
    [InlineData("09", 1)]
    [InlineData("10", 1)]
    public void Calculate(string protectionClassCode, decimal expectedResult)
    {
        var result = sut.Calculate(new ProtectionClassFactor.Request
        {
            ProtectionClassCode = protectionClassCode
        });

        Assert.Equal(expectedResult, result);
    }
}
