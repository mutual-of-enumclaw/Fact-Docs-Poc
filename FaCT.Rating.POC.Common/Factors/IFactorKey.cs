namespace FaCT.Rating.POC.Common.Factors;

public interface IFactorKey
{
    string State { get; set; }

    string LineOfBusiness { get; set; }

    string InsuranceLine { get; set; }

    string Product { get; set; }

    string Coverage { get; set; }

    string RateBook { get; set; }

    public DateTime EffectiveDate { get; }

    public DateTime? NbEffectiveDate { get; set; }
}
