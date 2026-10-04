using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PersonalAgent.Web.Pages;

/// <summary>Displays the selected trusted development profile.</summary>
public sealed class IndexModel(IConfiguration configuration) : PageModel
{
    /// <summary>Gets the normalized development profile name.</summary>
    public string Profile { get; private set; } = "Local";

    /// <summary>Loads the profile label from host configuration.</summary>
    public void OnGet()
    {
        Profile = configuration["JARVIS_PROFILE"] ?? "Local";
    }
}
