using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.AdminService;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ControlRejectedException error) { return Results.Json(new { code = error.Code }, statusCode: 409); }
            catch (IOException) { return Results.Json(new { code = "simulator-unavailable" }, statusCode: 503); }
        });

        admin.MapGet("/status", (AdminOperations operations) => operations.Read());
        admin.MapGet("/invitation", (AdminOperations operations) => operations.ReadInvitation());
        admin.MapPost("/invitation/cycle", (AdminOperations operations) => operations.CycleInvitation());
        admin.MapPost("/invitation", (CreateInvitation body, AdminOperations operations) =>
            operations.Generate(body.AttemptId));
        admin.MapPost("/approve", (ApproveGuest body, AdminOperations operations) =>
        {
            operations.Approve(body.ConnectionId);
            return Results.NoContent();
        });
        admin.MapPost("/limits", (ControlLimits limits, AdminOperations operations) =>
        {
            operations.Limits(limits.SpeedCap, limits.MaximumDepth);
            return Results.NoContent();
        });
        admin.MapPost("/recover", (AdminOperations operations) => { operations.Recover(); return Results.NoContent(); });
        admin.MapPost("/arm", (AdminOperations operations) => { operations.Arm(); return Results.NoContent(); });
        admin.MapPost("/stop", (AdminOperations operations) => { operations.Stop(); return Results.NoContent(); });
        admin.MapPost("/end", (AdminOperations operations) => { operations.End(); return Results.NoContent(); });
        admin.MapPost("/unlock", (UnlockDevice body, AdminOperations operations) =>
        {
            operations.Unlock(body.LockRevision);
            return Results.NoContent();
        });
        admin.MapPost("/simulator/{fault}", async (string fault, AdminOperations operations) =>
        {
            await operations.Fault(fault);
            return Results.NoContent();
        });
    }
}
