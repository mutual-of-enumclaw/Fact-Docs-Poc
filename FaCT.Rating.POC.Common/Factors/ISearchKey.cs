namespace FaCT.Rating.POC.Common.Factors;

public interface ISearchKey : IFactorKey
{
    string KeyValue { get; }

    bool IsNewBusiness { get; }
}
