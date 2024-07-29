namespace FaCT.Rating.POC;

public static class DateTimeExtensions
{
    public static string ToPolicyEffective(this string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            return DateTime.Today.ToString("yyyyMMdd");
        }

        return val;
    }

    public static DateTime ToEffectiveDate(this string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            return DateTime.Today;
        }

        if (!DateTime.TryParseExact(val, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var result))
        {
            return DateTime.Today;
        }

        return result;
    }

    public static DateTime? ToNbEffectiveDate(this string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            return null;
        }

        if (!DateTime.TryParseExact(val, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var result))
        {
            return null;
        }

        return result;
    }
}
