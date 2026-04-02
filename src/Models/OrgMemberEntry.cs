namespace PrMonitor.Models;

/// <summary>A cached GitHub org member: login and optional display name.</summary>
public sealed class OrgMemberEntry
{
    public string Login { get; set; } = "";
    public string? Name { get; set; }
}
