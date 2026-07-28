namespace CaddyUi.Web.Security;

public sealed class OriginValidationMiddleware
{
    private readonly RequestDelegate _next;

    public OriginValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        RequestSurfaceResolver resolver)
    {
        if (HttpMethods.IsPost(context.Request.Method) ||
            HttpMethods.IsPut(context.Request.Method) ||
            HttpMethods.IsPatch(context.Request.Method) ||
            HttpMethods.IsDelete(context.Request.Method))
        {
            if (!resolver.IsOriginAllowed(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new { error = "The request origin is not allowed." },
                    context.RequestAborted);
                return;
            }
        }

        await _next(context);
    }
}
