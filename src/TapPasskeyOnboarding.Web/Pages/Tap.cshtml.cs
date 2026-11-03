using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TapPasskeyOnboarding.Web.Pages;

public sealed class TapModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status410Gone;
}
