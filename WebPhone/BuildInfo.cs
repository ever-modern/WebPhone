using System.Reflection;

namespace WebPhone.UI;

public static class BuildInfo
{
    static DateTime? GetBuildDate()
    {
        var attr = Assembly
            .GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>();

        var stringResult = attr.FirstOrDefault(a => a.Key == "BuildDate")?.Value;

        if (DateTime.TryParse(stringResult, out var result) is false)
        {
            return null;
        }

        return result;
    }

    public static string BuildVersion
    {
        get
        {
            var buildDate = GetBuildDate();
            if (buildDate is null)
                return "";

            var minusYears = GetBuildDate().Value.AddYears(-26);

            var result =  minusYears.ToString("y.MM.dd.HH.mm") ?? "";
            return result;
        }
    }
}
