using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace WebPhone.AzureEnd;

internal static class FunctionCors
{
    public static IActionResult BuildResult(IActionResult result, string allowedMethods)
        => new CorsResult(result, allowedMethods);

    public static IActionResult? TryBuildPreflightResult(HttpRequest request, string allowedMethods)
        => HttpMethods.IsOptions(request.Method)
            ? BuildResult(new OkResult(), allowedMethods)
            : null;

    private sealed class CorsResult(IActionResult inner, string allowedMethods) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var headers = context.HttpContext.Response.Headers;
            headers["Access-Control-Allow-Origin"] = "*";
            headers["Access-Control-Allow-Headers"] = "Content-Type";
            headers["Access-Control-Allow-Methods"] = allowedMethods;
            await inner.ExecuteResultAsync(context);
        }
    }

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
