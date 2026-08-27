using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Fetchit.Storage.Service.Authorizations;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAuthorize : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = config.GetValue<string>("Storage:ApiKey");

        if (string.IsNullOrEmpty(expectedKey))
        {
            context.Result = new StatusCodeResult(500);
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-API-Key", out var providedKey)
            || !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
