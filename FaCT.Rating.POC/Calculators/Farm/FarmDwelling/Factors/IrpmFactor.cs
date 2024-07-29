namespace FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;

public class IrpmFactor : ICalculator<IrpmFactor.Request>
{
    public decimal Calculate(Request request)
    {
        return request.IrpmFactor;
    }

    public class Request : BaseFactorRequest
    {
        public decimal IrpmFactor { get; set; }
    }
}
