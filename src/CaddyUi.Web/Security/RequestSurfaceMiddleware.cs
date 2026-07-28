namespace CaddyUi.Web.Security;

public sealed class RequestSurfaceMiddleware
{
    private readonly RequestDelegate _next;

    public RequestSurfaceMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        RequestSurfaceResolver resolver)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var surface = resolver.Resolve(context);
        context.Items[RequestSurfaceResolver.SurfaceItemKey] = surface;
        if (surface == RequestSurface.Rejected)
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        var portalPath =
            context.Request.Path.StartsWithSegments("/portal") ||
            context.Request.Path.StartsWithSegments("/__caddy_ui_auth");
        if (surface == RequestSurface.Portal)
        {
            if (context.Request.Path == "/favicon.ico")
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!portalPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        else if (portalPath)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }
}
