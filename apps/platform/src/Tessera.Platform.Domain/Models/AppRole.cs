namespace Tessera.Platform.Domain.Models;

/// <summary>
/// A role defined inside an envelope. <see cref="Actions"/> lists the
/// Tessera capabilities the role is allowed to use (e.g. "create_widget").
/// NOTE: actions are tracked as a catalog for now — enforcement is on the
/// backlog.
/// </summary>
public class AppRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvelopeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Actions { get; set; } = [];
}
