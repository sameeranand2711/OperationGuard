using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OperationGuard.AspNetCore.Metadata;

namespace OperationGuard.AspNetCore.Tests;

[ApiController]
[Route("hosted-mvc")]
public sealed class HostedGuardController(InvocationCounter invocations) : ControllerBase
{
    [HttpPost]
    [OperationGuard("Hosted.Mvc")]
    public IActionResult Post()
    {
        invocations.Value++;
        return new ContentResult
        {
            Content = "accepted",
            ContentType = "text/plain; charset=utf-8",
            StatusCode = StatusCodes.Status202Accepted,
        };
    }
}
