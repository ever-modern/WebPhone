using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
}
