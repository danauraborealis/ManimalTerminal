namespace Manimal.Terminal.Server;

// Harmony postfixes run when an async method returns its Task. Chain the work so
// profile changes happen only after the server (and earlier patches) finish.
internal static class TerminalRaidCompletion
{
    internal static async Task AfterAsync(Task raid, Func<Task> completed)
    {
        await raid.ConfigureAwait(false);
        await completed().ConfigureAwait(false);
    }
}
