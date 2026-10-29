using System.Collections.Immutable;

namespace TomatoTimerPro.RuntimeStore.States.Workspaces;

public sealed record WorkspaceState(string? ActivePath = null, ImmutableArray<string> RecentPaths = default);
