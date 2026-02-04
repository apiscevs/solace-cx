using System.ComponentModel.DataAnnotations;

namespace Solace.Admin.Options;

public sealed class SolaceSempOptions
{
    public const string SectionName = "SolaceSemp";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    public string VpnName { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}
