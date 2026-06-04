using Npgsql;

namespace WebPhone.Backend.Storage;

internal static class NpgsqlParameterCollectionExtensions
{
    public static NpgsqlParameterCollection Add<T>(this NpgsqlParameterCollection parameterCollection, string parameterName, T? value)
        where T : struct
    {
        if (value is null)
        {
            parameterCollection.AddWithValue(parameterName, DBNull.Value);
        }
        else
        {
            parameterCollection.AddWithValue(parameterName, value);
        }

        return parameterCollection;
    }

    public static NpgsqlParameterCollection Add<T>(this NpgsqlParameterCollection parameterCollection, string parameterName, T? value)
    {
        if (typeof(T) == typeof(string) && value is null)
        {
            parameterCollection.Add(parameterName, NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)value ?? DBNull.Value;
        }
        else
        {
            parameterCollection.AddWithValue(parameterName, value is null ? DBNull.Value : value);
        }

        return parameterCollection;
    }
}
