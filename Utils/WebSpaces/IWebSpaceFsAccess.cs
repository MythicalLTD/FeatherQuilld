namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Minimal FS access for path-confined file ops (testable without full store DI).</summary>
public interface IWebSpaceFsAccess
{
    WebSpace? Get(Guid uuid);
    string EffectiveFsPath(Guid uuid);
}
