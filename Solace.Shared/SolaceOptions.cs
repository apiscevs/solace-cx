using System.ComponentModel.DataAnnotations;

namespace Solace.Shared;

public sealed class SolaceOptions
{
    public const string SectionName = "Solace";

    [Required]
    public string Host { get; set; } = string.Empty;

    [Required]
    public string VpnName { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string TopicPrefix { get; set; } = "solace/test";

    public string DefaultPublishTopic => $"{NormalizePrefix()}/messages";

    public string DefaultSubscriptionTopic => $"{NormalizePrefix()}/>";

    private string NormalizePrefix()
    {
        var trimmed = TopicPrefix.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(trimmed) ? "solace/test" : trimmed;
    }
}
