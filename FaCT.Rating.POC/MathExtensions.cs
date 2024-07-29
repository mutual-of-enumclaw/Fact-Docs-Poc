namespace FaCT.Rating.POC;

public static class MathExtensions
{
    public static decimal Forecast(this decimal val, decimal[] yValues, decimal[] xValues)
    {
        var n = yValues.Length;
        var sumX = xValues.Sum();
        var sumY = yValues.Sum();
        var sumXSquare = xValues.Select(x => x * x).Sum();
        var sumXY = xValues.Zip(yValues, (x, y) => x * y).Sum();

        var slope = (n * sumXY - sumX * sumY) / (n * sumXSquare - sumX * sumX);
        var intercept = (sumY - slope * sumX) / n;

        return slope * val + intercept;
    }
}
