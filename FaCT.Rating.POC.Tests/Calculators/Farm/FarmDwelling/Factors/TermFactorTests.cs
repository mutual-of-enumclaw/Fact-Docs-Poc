using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class TermFactorTests
{
    private static readonly TermFactor sut = new();

    [Theory]
    [InlineData("20240506", "20250502", 0.989)]
    [InlineData("20240506", "20250503", 0.992)]
    [InlineData("20240506", "20250504", 0.995)]
    [InlineData("20240506", "20250505", 0.997)]
    [InlineData("20240506", "20250506", 1)]
    [InlineData("20240506", "20250507", 1.003)]
    [InlineData("20240506", "20250508", 1.005)]
    [InlineData("20240506", "20250509", 1.008)]
    [InlineData("20240506", "20250510", 1.011)]
    public void Calculate(string policyEffective, string policyExpiration, decimal expectedResult)
    {
        var result = sut.Calculate(new TermFactor.Request
        {
            PolicyEffective = policyEffective,
            PolicyExpiration = policyExpiration
        });

        Assert.Equal(expectedResult, result);
    }
}