using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class IrpmFactorTests
{
    private static readonly IrpmFactor sut = new();

    [Theory]
    [InlineData(0.95, 0.95)]
    [InlineData(1, 1)]
    [InlineData(1.05, 1.05)]
    public void Calculate(decimal irpmFactor, decimal expectedResult)
    {
        var result = sut.Calculate(new IrpmFactor.Request
        {
            IrpmFactor = irpmFactor
        });

        Assert.Equal(expectedResult, result);
    }
}
