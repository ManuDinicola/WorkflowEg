namespace Flowd.Engine.Keys;

/// <summary>Port: produces 63-bit, time-ordered, globally unique keys (FR-DOM-001). Injected so the engine stays deterministic.</summary>
public interface IKeyGenerator
{
    long NextKey();
}
