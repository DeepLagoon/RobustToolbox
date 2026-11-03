using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Robust.Server.Console
{
    /// <summary>
    /// The server console shell that executes commands.
    /// </summary>
    [NotContentImplementable]
    public interface IServerConsoleHost : IConsoleHost
    {
        /// <summary>
        /// Player responsible for the synchronously executing command, including sudo.
        /// Null outside command execution or for commands from the server console.
        /// </summary>
        ICommonSession? ExecutingSession { get; }

        /// <summary>
        /// Initializes the ConsoleShell service.
        /// </summary>
        void Initialize();
    }
}
