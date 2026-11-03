using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TapPasskeyOnboarding.Core.Eligibility;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Graph;
using TapPasskeyOnboarding.Core.Models;
using TapPasskeyOnboarding.Core.Services;
using TapPasskeyOnboarding.Web.Security;

namespace TapPasskeyOnboarding.Web.Pages;

public sealed class IndexModel(
    EligibilityService eligibility,
    OnboardingService onboarding,
    IGraphDirectoryClient graph,
    EligibilityOptions options,
    IActionNonceStore nonces,
    HtmlEncoder htmlEncoder) : PageModel
{
    public EligibilityDecision? Decision { get; private set; }
    public string ActionNonce { get; private set; } = string.Empty;
    public bool PasskeyCompleted { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var oid = RequireOid();
        if (await graph.IsMemberOfGroupAsync(oid, options.CompletionGroupId, cancellationToken))
        {
            PasskeyCompleted = true;
            return Page();
        }

        if (await graph.HasFido2MethodAsync(oid, cancellationToken))
        {
            if (await graph.IsMemberOfGroupAsync(oid, options.OnboardingGroupId, cancellationToken))
            {
                await onboarding.VerifyAndCompleteAsync(oid, HttpContext.TraceIdentifier, cancellationToken);
                PasskeyCompleted = true;
                return Page();
            }
        }

        Decision = await eligibility.EvaluateAsync(oid, cancellationToken);
        if (!Decision.IsEligible) return StatusCode(StatusCodes.Status403Forbidden);
        ActionNonce = nonces.Issue(oid);
        return Page();
    }

    public async Task<IActionResult> OnPostIssueTapAsync(string actionNonce, CancellationToken cancellationToken)
    {
        var oid = RequireOid();
        if (!nonces.TryConsume(oid, actionNonce)) return new ObjectResult(new { error = new { code = "replay_detected", message = "Action token is invalid or already used." } }) { StatusCode = StatusCodes.Status409Conflict };
        if (await graph.HasFido2MethodAsync(oid, cancellationToken))
            return RedirectToPage();
        var correlationId = HttpContext.TraceIdentifier;
        try
        {
            var tap = await onboarding.IssueTapAsync(oid, correlationId, cancellationToken);
            return new OneTimeTapResult(tap, htmlEncoder);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (GraphOperationException ex) when (ex.StatusCode == StatusCodes.Status429TooManyRequests)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }
    }

    private string RequireOid() =>
        User.FindFirstValue("oid") ?? throw new InvalidOperationException("Authenticated principal has no oid claim.");
}

public sealed class OneTimeTapResult(TapIssuance tap, HtmlEncoder encoder) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store, private";
        response.Headers.Pragma = "no-cache";
        var secret = encoder.Encode(tap.Secret);
        var expires = encoder.Encode(tap.ExpiresAt.ToString("u"));
        await response.WriteAsync($"""
            <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>Código temporário · Configuração da chave de acesso</title><link rel="stylesheet" href="/css/site.css"></head>
            <body><header><span class="brand"><span>PK</span> Configuração da chave de acesso</span></header>
            <main class="shell narrow"><p class="eyebrow">Etapa 2 de 3</p><h1>Use este código para cadastrar sua chave</h1>
            <p class="lead">Mantenha esta página aberta. O código aparece somente agora e funciona uma única vez.</p>
            <section class="tap-panel"><p class="field-label">Seu código temporário</p><p id="tap-value" class="secret">{secret}</p>
            <button type="button" class="button secondary" data-copy-target="tap-value">Copiar código</button>
            <p class="helper">Válido até {expires}.</p></section>
            <ol class="instructions">
              <li><strong>Copie o código</strong> acima.</li>
              <li><strong>Abra as Informações de segurança</strong> em uma nova aba.</li>
              <li>Se solicitado, entre usando o código temporário.</li>
              <li>Escolha <strong>Adicionar método de entrada → Chave de acesso (Passkey)</strong> e siga as orientações do Windows.</li>
              <li>Volte para esta página e conclua a verificação.</li>
            </ol>
            <div class="actions split">
              <a class="button" target="_blank" rel="noopener noreferrer" href="https://mysignins.microsoft.com/security-info">Abrir Informações de segurança</a>
              <a class="button secondary" href="/confirm">Já cadastrei minha chave</a>
            </div></main><script src="/js/site.js"></script></body></html>
            """);
    }
}
