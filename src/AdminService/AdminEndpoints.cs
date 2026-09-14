using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.AdminService;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter(async (context, next) =>
        {
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            try
            {
                context.HttpContext.RequestServices.GetRequiredService<OperatorCapability>()
                    .Demand(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null);
            }
            catch (ControlRejectedException error)
            {
                return Results.Json(new { code = error.Code }, statusCode: 401);
            }
            try { return await next(context); }
            catch (ControlRejectedException error) { return Results.Json(new { code = error.Code }, statusCode: 409); }
            catch (IOException) { return Results.Json(new { code = "simulator-unavailable" }, statusCode: 503); }
        });

        admin.MapGet("/status", (HttpContext context, AdminOperations operations) => operations.Read(Token(context)));
        admin.MapPost("/invitation", (CreateInvitation body, HttpContext context, AdminOperations operations) =>
            operations.Generate(Token(context), body.AttemptId));
        admin.MapPost("/approve", (ApproveGuest body, HttpContext context, AdminOperations operations) =>
        {
            operations.Approve(Token(context), body.ConnectionId);
            return Results.NoContent();
        });
        admin.MapPost("/limits", (ControlLimits limits, HttpContext context, AdminOperations operations) =>
        {
            operations.Limits(Token(context), limits.SpeedCap, limits.MaximumDepth);
            return Results.NoContent();
        });
        admin.MapPost("/recover", (HttpContext context, AdminOperations operations) => { operations.Recover(Token(context)); return Results.NoContent(); });
        admin.MapPost("/arm", (HttpContext context, AdminOperations operations) => { operations.Arm(Token(context)); return Results.NoContent(); });
        admin.MapPost("/stop", (HttpContext context, AdminOperations operations) => { operations.Stop(Token(context)); return Results.NoContent(); });
        admin.MapPost("/end", (HttpContext context, AdminOperations operations) => { operations.End(Token(context)); return Results.NoContent(); });
        admin.MapPost("/unlock", (UnlockDevice body, HttpContext context, AdminOperations operations) =>
        {
            operations.Unlock(Token(context), body.LockRevision);
            return Results.NoContent();
        });
        admin.MapPost("/simulator/{fault}", async (string fault, HttpContext context, AdminOperations operations) =>
        {
            await operations.Fault(Token(context), fault);
            return Results.NoContent();
        });
    }

    private static string Token(HttpContext context) => context.Request.Headers.Authorization.ToString()[7..];
}
