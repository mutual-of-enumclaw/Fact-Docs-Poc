using FaCT.Rating.POC.Calculators.Farm.FarmDwelling;
using FaCT.Rating.POC.Calculators.Farm.FarmDwelling.Factors;
using FaCT.Rating.POC.Common;
using FaCT.Rating.POC.Tests.Data.Farm.FarmDwelling;

namespace FaCT.Rating.POC.Tests.Calculators.Farm.FarmDwelling;

public class FinartSppCalculatorTests
{
    private const decimal DefaultCommissionReduction = 9.99999m;

    private static readonly Settings settings = new()
    {
        Farm = new()
        {
            FarmDwelling = new()
            {
                Factors = new()
                {
                    Construction = new Dictionary<string, Dictionary<string, decimal>>
                    {
                        {
                            "AZ|FRM|FD|*|*|A|20140425|*",
                            new Dictionary<string, decimal>
                            {
                                { "L", 1 },
                                { "M", 1m },
                                { "1", 1 },
                                { "2", 0.85m },
                                { "3", 0.55m },
                                { "6", 0.55m }
                            }
                        },
                        {
                            "AZ|FRM|FD|*|*|A|20230410|20230120",
                            new Dictionary<string, decimal>
                            {
                                { "L", 1 },
                                { "M", 1.5m },
                                { "1", 1 },
                                { "2", 0.85m },
                                { "3", 0.55m },
                                { "6", 0.55m }
                            }
                        },
                        {
                            "AZ|FRM|FD|*|*|A|20240506|*",
                            new Dictionary<string, decimal>
                            {
                                { "L", 1 },
                                { "M", 1.65m },
                                { "1", 1 },
                                { "2", 0.85m },
                                { "3", 0.55m },
                                { "6", 0.55m }
                            }
                        }
                    },
                    ProtectionClass = new Dictionary<string, decimal>
                    {
                        { "01", 0.55m },
                        { "02", 0.55m },
                        { "03", 0.55m },
                        { "04", 0.55m },
                        { "05", 0.55m },
                        { "06", 0.55m },
                        { "07", 0.6m },
                        { "08", 0.65m },
                        { "8B", 0.8m },
                        { "09", 1m },
                        { "10", 1m }
                    },
                    TheftExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.95m },
                        { "N", 1m }
                    },
                    VandalismExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.99m },
                        { "N", 1m }
                    },
                    WindHailExclusion = new Dictionary<string, decimal>
                    {
                        { "Y", 0.9m },
                        { "N", 1m }
                    }
                },
                FineArts = new()
                {
                    BreakagePremiumRate = 1.5m,
                    UpperLimit = 100000,
                    Factors = new()
                    {
                        Rate = new Dictionary<decimal, decimal>
                        {
                            { 1000m, 5m },
                            { 2000m, 7m },
                            { 3000m, 8m },
                            { 4000m, 10m },
                            { 5000m, 12m },
                            { 6000m, 14m },
                            { 7000m, 16m },
                            { 8000m, 17m },
                            { 9000m, 19m },
                            { 10000m, 21m },
                            { 11000m, 23m },
                            { 12000m, 25m },
                            { 13000m, 26m },
                            { 14000m, 28m },
                            { 15000m, 30m },
                            { 16000m, 32m },
                            { 17000m, 34m },
                            { 18000m, 35m },
                            { 19000m, 37m },
                            { 20000m, 39m },
                            { 21000m, 41m },
                            { 22000m, 43m },
                            { 23000m, 44m },
                            { 24000m, 46m },
                            { 25000m, 48m },
                            { 30000m, 57m },
                            { 35000m, 66m },
                            { 40000m, 75m },
                            { 45000m, 84m },
                            { 50000m, 93m },
                            { 75000m, 138m },
                            { 100000m, 183m }
                        }
                    }
                }
            }
        }
    };

    private static readonly FinartSppCalculator sut = new(
        settings,
        new ConstructionFactor(FactorCacheRepository.GetConstructionFactorCache()),
        new ProtectionClassFactor(settings),
        new FarmTypeFactor(FactorCacheRepository.GetFarmTypeFactorCache()),
        new TheftExclusionFactor(settings),
        new WindHailExclusionFactor(settings),
        new VandalismExclusionFactor(settings),
        new IrpmFactor(),
        new CommissionFactor(),
        new TermFactor(),
        new BreakagePremiumCalculator(
            settings,
            new IrpmFactor(),
            new CommissionFactor(),
            new TermFactor()));

    [Theory]
    [ClassData(typeof(FinartSppCalculatorTestData))]
    [InlineData("20240506", "20250506", 90000, "6", "05", "1", "N", "N", "N", 0.95, 9.99999, "N", 0, 183)]
    [InlineData("20240506", "20250506", 90000, "3", "05", "1", "N", "N", "N", 0.95, 9.99999, "N", 0, 183)]
    [InlineData("20240506", "20250506", 90000, "2", "05", "1", "N", "N", "N", 0.95, 9.99999, "N", 0, 214)]
    [InlineData("20240506", "20250506", 90000, "M", "05", "1", "N", "N", "N", 0.95, 9.99999, "N", 0, 294)]
    [InlineData("20240506", "20250506", 90000, "1", "05", "1", "N", "N", "N", 0.95, 9.99999, "N", 0, 229)]
    [InlineData("20240506", "20250506", 80000, "6", "05", "1", "N", "N", "N", 0.95, 9.99999, "N", 0, 163)]
    [InlineData("20240506", "20250506", 80000, "6", "05", "1", "N", "N", "N", 0.95, 9.99999, "N", 500, 500)]
    public void Calculate(
        string policyEffective,
        string policyExpiration,
        decimal coverageItemLimit,
        string constructionCode,
        string protectionClassCode,
        string farmTypeCode,
        string theftExclusionIndicator,
        string windHailExclusionIndicator,
        string vandalismExclusionIndicator,
        decimal irpmFactor,
        decimal commissionReduction,
        string breakageExclusionIndicator,
        decimal manualPremium,
        decimal expectedResult,
        string state = Constants.FactorKeyWildcard,
        string lob = Constants.FactorKeyWildcard,
        string insuranceLine = Constants.FactorKeyWildcard,
        string product = Constants.FactorKeyWildcard,
        string coverage = Constants.FactorKeyWildcard,
        string rateBook = Constants.FactorKeyWildcard,
        DateTime? nbEffectiveDate = null)
    {
        var request = new FinartSppCalculator.Request
        {
            State = state,
            LineOfBusiness = lob,
            InsuranceLine = insuranceLine,
            Product = product,
            Coverage = coverage,
            RateBook = rateBook,
            NbEffectiveDate = nbEffectiveDate,
            PolicyEffective = policyEffective,
            PolicyExpiration = policyExpiration,
            CoverageItemLimit = coverageItemLimit,
            ConstructionCode = constructionCode,
            ProtectionClassCode = protectionClassCode,
            FarmTypeCode = farmTypeCode,
            TheftExclusionIndicator = theftExclusionIndicator,
            WindHailExclusionIndicator = windHailExclusionIndicator,
            VandalismExclusionIndicator = vandalismExclusionIndicator,
            IrpmFactor = irpmFactor,
            CommissionReduction = commissionReduction,
            BreakageExclusionIndicator = breakageExclusionIndicator,
            ManualPremium = manualPremium
        };

        var result = sut.Calculate(request);

        Assert.Equal(expectedResult, result);
    }
}
