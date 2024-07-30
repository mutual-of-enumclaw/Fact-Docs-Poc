using FaCT.Rating.POC.Common;
using System.Collections;

namespace FaCT.Rating.POC.Tests.Data.Farm.FarmDwelling;

public class FinartSppCalculatorTestData : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return PolicyTestData.GetFinartTestPolicy(183, ConstructionCodes.FireResistive, 90000).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(183, ConstructionCodes.NonCombustible, 90000).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(214, ConstructionCodes.Masonry, 90000).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(294, ConstructionCodes.MobileHome, 90000).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(229, ConstructionCodes.Frame, 90000).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(163, ConstructionCodes.FireResistive, 80000).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(500, ConstructionCodes.FireResistive, 80000, 500).ToObjectArray();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
