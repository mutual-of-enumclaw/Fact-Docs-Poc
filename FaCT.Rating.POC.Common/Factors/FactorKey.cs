namespace FaCT.Rating.POC.Common.Factors;

public class FactorKey : IFactorKey, ISearchKey
{
    public required string State { get; set; }

    public required string LineOfBusiness { get; set; }

    public required string InsuranceLine { get; set; }

    public required string Product { get; set; }

    public required string Coverage { get; set; }

    public required string RateBook { get; set; }

    public DateTime EffectiveDate { get; set; }

    public DateTime? NbEffectiveDate { get; set; }

    public string? KeyValue { get; }

    public bool IsNewBusiness { get; set; }

    public override bool Equals(object? obj) => obj is FactorKey key &&
        State == key.State &&
        LineOfBusiness == key.LineOfBusiness &&
        InsuranceLine == key.InsuranceLine &&
        Product == key.Product &&
        Coverage == key.Coverage &&
        RateBook == key.RateBook &&
        EffectiveDate == key.EffectiveDate &&
        NbEffectiveDate == key.NbEffectiveDate &&
        IsNewBusiness == key.IsNewBusiness;

    public override int GetHashCode() => HashCode.Combine(State, LineOfBusiness, InsuranceLine, Product, Coverage, RateBook, EffectiveDate, NbEffectiveDate);
}
