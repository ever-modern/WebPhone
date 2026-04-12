using EverModern.WheelProtection.DataWorks;

namespace WebPhone.Contract;

public static class CommonIdsGenerator
{
    static readonly IdsGenerator _idsGenerator = new(new(2026, 3, 30));
    public static long NewId() => _idsGenerator.Generate();
}
