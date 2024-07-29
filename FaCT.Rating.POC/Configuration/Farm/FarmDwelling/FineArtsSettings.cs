namespace FaCT.Rating.POC.Configuration.Farm.FarmDwelling;

public class FineArtsSettings
{
    public decimal BreakagePremiumRate { get; set; }

    public int UpperLimit { get; set; }

    public int UpperRate { get; set; }

    public FineArtsFactors Factors { get; set; } = new();

    public class FineArtsFactors
    {
        public Dictionary<decimal, decimal> Rate { get; set; } = [];
    }
}
