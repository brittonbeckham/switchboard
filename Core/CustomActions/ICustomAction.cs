namespace Switchboard.Core.CustomActions;

/// <summary>
/// A hand-coded action beyond the built-in catalog. Registering one in
/// <see cref="CustomActionCatalog"/> merges it into ActionCatalog — it gets a
/// ghost key, shows up in the Action picker and the search box, all for free.
/// </summary>
public interface ICustomAction
{
    string Id { get; }
    string DisplayName { get; }
    string ShortLabel { get; }

    /// <summary>Human-readable, numbered steps describing what actually happens
    /// when this action runs — shown in the assignment wizard's confirm step.</summary>
    string Summary { get; }

    Task RunAsync();
}
