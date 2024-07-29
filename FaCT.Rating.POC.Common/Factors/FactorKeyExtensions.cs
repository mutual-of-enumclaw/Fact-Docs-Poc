namespace FaCT.Rating.POC.Common.Factors;

public static class FactorKeyExtensions
{
    public static DateTime GetEffectiveDate(this IFactorKey key, bool isNewBusiness)
    {
        return isNewBusiness && key.NbEffectiveDate.HasValue ? key.NbEffectiveDate.Value : key.EffectiveDate;
    }
}
