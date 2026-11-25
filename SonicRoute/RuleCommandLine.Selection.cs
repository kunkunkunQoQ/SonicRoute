using System;
using System.Collections.Generic;
using System.Linq;
using SonicRoute.Core.Models;

namespace SonicRoute;

internal static partial class RuleCommandLine
{
    internal static AutoRule? SelectRule(string selector, IEnumerable<AutoRule> rules, out Result? failure)
    {
        bool isGuid = Guid.TryParse(selector, out var requestedId);
        var matches = rules.Where(r => string.Equals(r.Id, selector, StringComparison.OrdinalIgnoreCase)
            || (isGuid && Guid.TryParse(r.Id, out var id) && id == requestedId)).ToList();
        if (matches.Count == 0)
            matches = rules.Where(r => string.Equals(r.Name, selector, StringComparison.OrdinalIgnoreCase)).ToList();
        failure = matches.Count switch
        {
            0 => new Result { ExitCode = 3, Code = "rule_not_found" },
            1 => null,
            _ => new Result { ExitCode = 4, Code = "ambiguous_rule" }
        };
        return failure == null ? matches[0] : null;
    }
}
