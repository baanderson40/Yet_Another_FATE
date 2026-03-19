using System;
using YetAnotherFATE.Modules.Ux;

namespace YetAnotherFATE.Commands;

public sealed class MainCommandDelegate(UxModule uxModule) : IMainCommandDelegate
{
    private readonly UxModule uxModule = uxModule;

    public void Execute(string command, string args)
    {
        if (string.Equals(args?.Trim(), "config", StringComparison.OrdinalIgnoreCase))
        {
            uxModule.ToggleConfig();
            return;
        }

        uxModule.ToggleMain();
    }
}
