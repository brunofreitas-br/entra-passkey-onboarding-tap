using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TapPasskeyOnboarding.Core.Graph;
using TapPasskeyOnboarding.Core.Models;
using TapPasskeyOnboarding.Core.Services;
using TapPasskeyOnboarding.Web.Security;

namespace TapPasskeyOnboarding.Web.Pages;

public sealed class ConfirmModel(
    OnboardingService onboarding,
    IActionNonceStore nonces) : PageModel
{
    public string ActionNonce { get; private set; } = string.Empty;
    public CompletionDecision? Result { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() => ActionNonce = nonces.Issue(RequireOid());

    public async Task<IActionResult> OnPostVerifyAsync(string actionNonce, CancellationToken cancellationToken)
    {
        var oid = RequireOid();
        if (!nonces.TryConsume(oid, actionNonce)) return new ObjectResult(new { error = new { code = "replay_detected", message = "Action token is invalid or already used." } }) { StatusCode = StatusCodes.Status409Conflict };
        try
        {
            Result = await onboarding.VerifyAndCompleteAsync(oid, HttpContext.TraceIdentifier, cancellationToken);
            ActionNonce = nonces.Issue(oid);
            return Page();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (GraphOperationException ex) when (ex.StatusCode == StatusCodes.Status429TooManyRequests)
        {
            Error = "Microsoft Graph asked us to retry. No membership was changed.";
            ActionNonce = nonces.Issue(oid);
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return Page();
        }
    }

    private string RequireOid() =>
        User.FindFirstValue("oid") ?? throw new InvalidOperationException("Authenticated principal has no oid claim.");
}
