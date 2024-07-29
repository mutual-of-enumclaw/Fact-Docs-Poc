namespace FaCT.Rating.POC.Calculators;

public interface ICalculator<TRequest>
    where TRequest : class
{
    decimal Calculate(TRequest request);
}
