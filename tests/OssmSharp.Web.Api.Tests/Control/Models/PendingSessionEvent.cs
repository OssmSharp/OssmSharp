namespace OssmSharp.Web.Api.Tests.Control.Models;

internal sealed record PendingSessionEvent(PendingSessionEventKind Kind, int Speed, int Stroke);
