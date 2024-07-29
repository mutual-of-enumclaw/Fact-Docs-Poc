namespace FaCT.Rating.POC.Configuration.Farm.FarmDwelling;

public class FarmDwellingSettings
{
    public CameraSettings Camera { get; set; } = new();

    public CoinsSettings Coins { get; set; } = new();

    public FineArtsSettings FineArts { get; set; } = new();

    public FirarmSettings Firarm { get; set; } = new();

    public FursSettings Furs { get; set; } = new();

    public GolfSettings Golf { get; set; } = new();

    public JewelSettings Jewel { get; set; } = new();

    public MusicSettings Music { get; set; } = new();

    public FarmDwellingFactors Factors { get; set; } = new();

    public class FarmDwellingFactors
    {
        public Dictionary<string, Dictionary<string, decimal>> Construction = [];

        public Dictionary<string, Dictionary<string, decimal>> FarmType = [];

        public Dictionary<string, decimal> ProtectionClass = [];

        public Dictionary<string, decimal> TheftExclusion = [];

        public Dictionary<string, decimal> WindHailExclusion = [];

        public Dictionary<string, decimal> VandalismExclusion = [];
    }
}
