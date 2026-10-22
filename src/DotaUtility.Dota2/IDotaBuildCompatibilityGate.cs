namespace DotaUtility.Dota2;

public interface IDotaBuildCompatibilityGate
{
    bool TryValidate(out string detail);
}
