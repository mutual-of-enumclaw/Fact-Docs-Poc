using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC;

public class BaseSearchFactorRequest : BaseFactorRequest, ISearchKey
{
    public virtual string KeyValue => throw new NotImplementedException();

    public bool IsNewBusiness { get; set; }

}
