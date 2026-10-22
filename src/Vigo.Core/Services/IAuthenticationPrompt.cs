using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>Asks the user how to get past a refused fetch / pull / push (mockup: "Échec d'authentification").</summary>
public interface IAuthenticationPrompt
{
    /// <summary>The user's choice; null when the dialog was canceled or could not be shown.</summary>
    Task<AuthenticationChoice?> ResolveAsync(AuthenticationProblem problem, CancellationToken cancellationToken = default);
}
