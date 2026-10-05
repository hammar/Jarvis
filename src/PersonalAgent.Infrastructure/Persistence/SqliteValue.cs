using System.Globalization;

namespace PersonalAgent.Infrastructure.Persistence;

internal static class SqliteValue
{
    internal static string Guid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    internal static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset DateTimeOffset(string value) =>
        System.DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
