global using PeersPair = (string, string);

namespace WebPhone.Backend.Services;

public class PairsEqualityComparer : IEqualityComparer<PeersPair>
{
    public static readonly PairsEqualityComparer Instance = new();

    static PeersPair NormalizePair(PeersPair pair)
    {
        var (id1, id2) = pair;
        return string.CompareOrdinal(id1, id2) > 0 ? (id1, id2) : (id2, id1);
    }

    PairsEqualityComparer() { }

    public bool Equals(PeersPair pair1, PeersPair pair2) =>
        NormalizePair(pair1).Equals(NormalizePair(pair2));

    public int GetHashCode(PeersPair pair) => NormalizePair(pair).GetHashCode();
}
