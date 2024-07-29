using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling.Factors;

public class CommissionFactorTests
{
    private static readonly CommissionFactor sut = new();

    [Theory]
    [InlineData(9.99999, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 0.99)]
    [InlineData(2, 0.98)]
    [InlineData(3, 0.97)]
    [InlineData(4, 0.96)]
    [InlineData(5, 0.95)]
    [InlineData(6, 0.94)]
    [InlineData(7, 0.93)]
    [InlineData(8, 0.92)]
    [InlineData(9, 0.91)]
    [InlineData(10, 0.9)]
    public void Calculate(decimal commissionReduction, decimal expectedResult)
    {
        var result = sut.Calculate(new CommissionFactor.Request
        {
            CommissionReduction = commissionReduction
        });

        Assert.Equal(expectedResult, result);
    }
}
