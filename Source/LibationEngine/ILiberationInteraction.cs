using System.Threading.Tasks;

namespace LibationEngine;

/// <summary>Questions the engine puts to whoever is driving it, awaited where they arise.</summary>
public interface ILiberationInteraction
{
	Task<BadBookAnswer> AskBadBookAsync(LiberationJob job);
}
