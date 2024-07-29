using FaCT.Rating.POC.Common.Factors;

namespace FaCT.Rating.POC.Tests;

public static class FactorCacheRepository
{
    public static FactorCache<decimal> GetConstructionFactorCache()
    {
        var factorCache = new FactorCache<decimal>();

        factorCache.AddFactor(
            new FactorKey
            {
                State = "AZ",
                LineOfBusiness = "FRM",
                InsuranceLine = "FD",
                Product = "*",
                Coverage = "*",
                RateBook = "A",
                EffectiveDate = DateTime.Parse("2014-04-25")
            },
            new Dictionary<string, decimal>
            {
                { "L", 1 },
                { "M", 1m },
                { "1", 1 },
                { "2", 0.85m },
                { "3", 0.55m },
                { "6", 0.55m }
            }
        );

        factorCache.AddFactor(
            new FactorKey
            {
                State = "AZ",
                LineOfBusiness = "FRM",
                InsuranceLine = "FD",
                Product = "*",
                Coverage = "*",
                RateBook = "A",
                EffectiveDate = DateTime.Parse("2023-04-10"),
                NbEffectiveDate = DateTime.Parse("2023-01-20")
            },
            new Dictionary<string, decimal>
            {
                { "L", 1 },
                { "M", 1.5m },
                { "1", 1 },
                { "2", 0.85m },
                { "3", 0.55m },
                { "6", 0.55m }
            }
        );

        factorCache.AddFactor(
            new FactorKey
            {
                State = "AZ",
                LineOfBusiness = "FRM",
                InsuranceLine = "FD",
                Product = "*",
                Coverage = "*",
                RateBook = "A",
                EffectiveDate = DateTime.Parse("2024-05-06")
            },
            new Dictionary<string, decimal>
            {
                { "L", 1 },
                { "M", 1.65m },
                { "1", 1 },
                { "2", 0.85m },
                { "3", 0.55m },
                { "6", 0.55m }
            }
        );

        return factorCache;
    }

    public static FactorCache<decimal> GetFarmTypeFactorCache()
    {

       var factorCache = new FactorCache<decimal>();

        factorCache.AddFactor(
            new FactorKey
            {
                State = "AZ",
                LineOfBusiness = "FRM",
                InsuranceLine = "*",
                Product = "*",
                Coverage = "*",
                RateBook = "A",
                EffectiveDate = DateTime.Parse("2015-05-25")
            },
            new Dictionary<string, decimal>
            {
                { "1", 1.17m },
                { "2", 1.2m },
                { "3", 1.02m },
                { "4", 1.02m }
            }
        );

        factorCache.AddFactor(
            new FactorKey
            {
                State = "AZ",
                LineOfBusiness = "FRM",
                InsuranceLine = "FD",
                Product = "*",
                Coverage = "*",
                RateBook = "A",
                EffectiveDate = DateTime.Parse("2024-02-02")
            },
            new Dictionary<string, decimal>
            {
                { "1", 1.17m },
                { "2", 1.2m },
                { "3", 1.02m },
                { "4", 1.02m }
            }
        );

        return factorCache;
    }
}
