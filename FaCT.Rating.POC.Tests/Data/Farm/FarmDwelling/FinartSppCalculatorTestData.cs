using FaCT.Rating.POC.Common;
using System.Collections;

namespace FaCT.Rating.POC.Tests.Data.Farm.FarmDwelling;

public class FinartSppCalculatorTestData : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return PolicyTestData.GetFinartTestPolicy(ConstructionCodes.FireResistive).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(ConstructionCodes.NonCombustible).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(ConstructionCodes.Masonry).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(ConstructionCodes.MobileHome).ToObjectArray();
        yield return PolicyTestData.GetFinartTestPolicy(ConstructionCodes.Frame).ToObjectArray();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
