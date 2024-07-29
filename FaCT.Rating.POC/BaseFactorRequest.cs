using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC;

public class BaseFactorRequest : IFactorKey
{
    public string State { get; set; }

    public string LineOfBusiness { get; set; }

    public string InsuranceLine { get; set; }

    public string Product { get; set; }

    public string Coverage { get; set; }

    public string RateBook { get; set; }

    public DateTime EffectiveDate => DateTime.ParseExact(PolicyEffective, "yyyyMMdd", null);

    public DateTime? NbEffectiveDate { get; set; }

    public string PolicyEffective { get; set; }

    public string PolicyExpiration { get; set; }

    public TRequestType ToRequest<TRequestType>(Action<TRequestType>? initializer = null)
        where TRequestType : BaseFactorRequest, new()
    {
        var newRequest = new TRequestType
        {
            State = this.State,
            LineOfBusiness = this.LineOfBusiness,
            InsuranceLine = this.InsuranceLine,
            Product = this.Product,
            Coverage = this.Coverage,
            RateBook = this.RateBook,
            NbEffectiveDate = this.NbEffectiveDate,
            PolicyEffective = this.PolicyEffective,
            PolicyExpiration = this.PolicyExpiration
        };

        if (initializer != null)
        {
            initializer(newRequest);
        }

        return newRequest;
    }
}
