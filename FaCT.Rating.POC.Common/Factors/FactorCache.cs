namespace FaCT.Rating.POC.Common.Factors;

public class FactorCache<TFactorType>
    where TFactorType : notnull
{
    private Dictionary<FactorKey, Dictionary<string, TFactorType>> cache = new Dictionary<FactorKey, Dictionary<string, TFactorType>>();

    public void AddFactor(FactorKey key, Dictionary<string, TFactorType> factor) => cache[key] = factor;

    public TFactorType GetFactor(ISearchKey searchKey)
    {
        var matchingFactors = cache
            .Where(entry => MatchesKey(entry.Key, searchKey))
            .OrderBy(entry => GetSpecificityScore(entry.Key))
            .ThenBy(entry => GetDateDifference(entry.Key, searchKey))
            .ToList();

        var factor = matchingFactors.FirstOrDefault();
        if (factor.Value == null)
        {
            return default;
        }

        return factor.Value[searchKey.KeyValue];
    }

    private bool MatchesKey(IFactorKey key, ISearchKey searchKey) => (key.State == searchKey.State || searchKey.State == "*") &&
        (key.LineOfBusiness == searchKey.LineOfBusiness || searchKey.LineOfBusiness == "*") &&
        (key.InsuranceLine == searchKey.InsuranceLine || searchKey.InsuranceLine == "*") &&
        (key.Product == searchKey.Product || searchKey.Product == "*") &&
        (key.Coverage == searchKey.Coverage || searchKey.Coverage == "*") &&
        (key.RateBook == searchKey.RateBook || searchKey.RateBook == "*") &&
        (key.GetEffectiveDate(searchKey.IsNewBusiness) <= searchKey.GetEffectiveDate(searchKey.IsNewBusiness));

    private int GetSpecificityScore(IFactorKey key)
    {
        int score = 0;

        if (key.State != "*") score++;
        if (key.LineOfBusiness != "*") score++;
        if (key.InsuranceLine != "*") score++;
        if (key.Product != "*") score++;
        if (key.Coverage != "*") score++;
        if (key.RateBook != "*") score++;

        return score;
    }

    private int GetDateDifference(IFactorKey key, ISearchKey searchKey)
    {
        return Math.Abs(key
            .GetEffectiveDate(searchKey.IsNewBusiness)
            .Subtract(searchKey.GetEffectiveDate(searchKey.IsNewBusiness)).Days);
    }
}
