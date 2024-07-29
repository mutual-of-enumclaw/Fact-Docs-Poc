namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class CommissionFactor : ICalculator<CommissionFactor.Request>
{
    private const decimal DefaultCommissionReduction = 9.99999m;

    public decimal Calculate(Request request)
    {
        if (request.CommissionReduction == DefaultCommissionReduction)
        {
            return 1;
        }

        return (100 - request.CommissionReduction) / 100;
    }

    public class Request : BaseFactorRequest
    {
        public decimal CommissionReduction { get; set; }
    }
}
