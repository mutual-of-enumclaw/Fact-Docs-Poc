namespace FaCT.Rating.POC.Common;

[Serializable]
public class FactorNotFoundException : Exception
{
    public FactorNotFoundException()
    {
    }

    public FactorNotFoundException(string? message) : base(message)
    {
    }

    public FactorNotFoundException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}